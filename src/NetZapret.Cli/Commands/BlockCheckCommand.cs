using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Измеряет, что закрыто в этой сети и чем это лечится.
/// </summary>
/// <remarks>
/// <para>
/// Цели берутся из справочника сервисов, а не из отдельного списка: проверять
/// имеет смысл ровно то, чем мы умеем управлять. Заодно вывод сразу
/// сопоставляется с нынешними правилами — видно не только «закрыто», но и
/// «настроено не тем способом».
/// </para>
/// <para>
/// Сокращено намеренно. Полный разбор каждой пробы — сотня строк, из которых
/// девяносто говорят «ок»; читать их незачем, а нужное в них тонет.
/// Подробности показываются только там, где что-то не так.
/// </para>
/// </remarks>
/// <summary>Насколько подробно проверять.</summary>
internal enum CheckDepth
{
    /// <summary>По имени с части, только там, где десинк вообще применяется.</summary>
    Quick,

    /// <summary>Весь справочник, по два имени с части.</summary>
    Full,

    /// <summary>Один сервис, но целиком — до четырёх имён с части.</summary>
    Focused,
}

internal static class BlockCheckCommand
{
    /// <summary>
    /// Сколько проб держать в воздухе одновременно.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ограничение не техническое, а измерительное. Пробы к одному хосту
    /// по-прежнему идут по очереди: одновременные соединения DPI иногда
    /// обрывает скопом, и картина смазывается. Параллельно идут разные хосты,
    /// и вот их можно много — время проверки упирается в ожидание, а не
    /// в работу.
    /// </para>
    /// <para>
    /// Восемь, а не тридцать: каждая проба открывает до четырёх соединений,
    /// и тридцать целей разом — это сотня одновременных сокетов с одного
    /// адреса. Оператору такое похоже на сканирование, а нам не нужно, чтобы
    /// проверка блокировок сама выглядела поводом для блокировки.
    /// </para>
    /// </remarks>
    private const int Parallelism = 8;

    /// <remarks>
    /// В конце придерживает окно. Проверка идёт минуты и заканчивается тем
    /// ради чего затевалась — перечнем того, что стоит поменять; закрыть его
    /// не дав прочитать значит выбросить всю работу. Придерживает только там,
    /// где окно закроется само: в терминале приглашение и так останется,
    /// а лишний Enter в сценарии — помеха.
    /// </remarks>
    public static async Task<int> RunAsync(CommandLine cmd, string defaultConfigPath, CancellationToken cancellationToken)
    {
        var code = await Run(cmd, defaultConfigPath, cancellationToken);

        if (ConsoleWindow.ClosesWithUs())
        {
            Console.WriteLine();
            Console.Write("Enter — закрыть…");
            Console.ReadLine();
        }

        return code;
    }

    private static Task<int> Run(CommandLine cmd, string defaultConfigPath, CancellationToken cancellationToken)
    {
        var only = cmd.Value("service");

        // Названный сервис сам по себе означает точечную проверку: просить
        // ещё и --depth было бы требованием повторить уже сказанное.
        var depth = cmd.Has("full") ? CheckDepth.Full
            : cmd.Has("quick") ? CheckDepth.Quick
            : only is not null ? CheckDepth.Focused
            : CheckDepth.Quick;

        return ExecuteAsync(
            AppSettings.Load(cmd.Value("settings", AppSettings.DefaultPath)),
            cmd.Value("config", defaultConfigPath),
            cmd.Value("user-rules", UserRulesFile.DefaultPath),
            ZapretPaths.Discover(cmd.Value("zapret-root"))?.Root,
            only,
            depth,
            cancellationToken);
    }

    /// <summary>
    /// Та же проверка, вызываемая из меню.
    /// </summary>
    /// <remarks>
    /// Отделено от разбора командной строки не ради красоты: совет в конце
    /// отсылает в раздел «Сервисы и маршруты», и запускаться проверка должна
    /// оттуда же — иначе человеку предлагается выйти из меню, вспомнить имя
    /// команды и вернуться.
    /// </remarks>
    internal static async Task<int> ExecuteAsync(
        AppSettings settings,
        string configPath,
        string userRulesPath,
        string? zapretRoot,
        string? only,
        CheckDepth depth,
        CancellationToken cancellationToken)
    {
        // Цели собираются до замеров: если проверять нечего, время на пробу
        // сети потрачено впустую, а человек об этом узнаёт последним.
        var targets = CollectTargets(configPath, userRulesPath, zapretRoot, only, depth, out var engine);

        if (targets.Count == 0)
        {
            Message(
                only is null
                    ? "Проверять нечего: доменные списки Zapret не найдены. Укажите --zapret-root."
                    : $"Сервиса с «{only}» в названии нет. Список — в пункте меню «Сервисы и маршруты».",
                ConsoleColor.Yellow);

            return 2;
        }

        WarnIfEnginesRunning();

        PrintLegend();

        Console.WriteLine();
        Console.WriteLine("Проверяю, что умеет сеть…");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var escape = EscapeWatcher.Watch(stop);

        var baseline = await BlockCheck.MeasureBaselineAsync(stop.Token);

        Console.WriteLine();
        PrintBaseline(baseline);

        Console.WriteLine();
        Console.WriteLine(
            $"{DescribeDepth(depth)}: {targets.Count} {Plural(targets.Count, "цель", "цели", "целей")}. " +
            (escape.Armed ? "Esc — прервать и показать, что успело набраться." : string.Empty));
        Console.WriteLine();
        PrintColumnHeader();

        var reports = await RunProbesAsync(targets, stop.Token);

        if (stop.IsCancellationRequested)
            Message($"Прервано. Успело проверить {reports.Count} из {targets.Count}.", ConsoleColor.Yellow);

        IReadOnlyList<string> badAddresses = [];

        // Прерванную проверку добивать не надо: человек нажал Esc, чтобы
        // перестать ждать, а не чтобы подождать ещё немного.
        if (!stop.IsCancellationRequested)
        {
            Console.WriteLine();
            Console.WriteLine("Проверяю адреса от системного резолвера…");

            badAddresses = await BlockCheck.FindBadSystemAddressesAsync(
                targets.Select(t => t.Host).Take(12).ToList(),
                stop.Token);
        }

        var pinned = FindPinned(reports);

        PrintSummary(reports, badAddresses);
        PrintPinned(pinned);

        var suggestions = Suggestions(reports, engine)
            .Where(s => !pinned.ContainsKey(s.Host))
            .ToList();

        PrintAdvice(suggestions);
        ApplyIfConfirmed(suggestions, userRulesPath);

        return reports.Any(r => r.Actionable) ? 1 : 0;
    }

    /// <summary>
    /// Гоняет пробы, не дожидаясь каждой по очереди.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Проверка почти целиком состоит из ожидания: живой хост отвечает за
    /// доли секунды, мёртвый молчит все четыре, и сорок целей подряд — это
    /// минуты, проведённые ни за чем. Разные хосты друг другу не мешают,
    /// поэтому идут разом.
    /// </para>
    /// <para>
    /// Строки печатаются по мере готовности, а не в порядке списка: ждать
    /// отстающего, чтобы соблюсти очерёдность, значит вернуть ровно то
    /// ожидание, ради устранения которого всё и затевалось. Порядок
    /// восстанавливается в итоге, где он и нужен.
    /// </para>
    /// </remarks>
    private static async Task<List<TargetReport>> RunProbesAsync(
        IReadOnlyList<(string Host, string Service)> targets,
        CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(Parallelism);
        var console = new object();
        var reports = new List<TargetReport>();

        var running = targets.Select(async target =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                var report = await BlockCheck.CheckAsync(target.Host, target.Service, cancellationToken);

                lock (console)
                {
                    reports.Add(report);
                    PrintRow(report);
                }
            }
            finally
            {
                slots.Release();
            }
        });

        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException)
        {
            // Прерывание — штатный исход, а не сбой. Что успели, то и покажем.
        }

        return reports;
    }

    private static void PrintRow(TargetReport report)
    {
        var previous = Console.ForegroundColor;

        Console.ForegroundColor = report.Actionable ? ConsoleColor.Red : ConsoleColor.DarkGray;
        Console.WriteLine(
            $"  {Truncate(report.Host, 30),-30} {report.Tcp.Describe(),-5} " +
            $"{report.Tls12.Describe(),-5} {report.Tls13.Describe(),-5} " +
            $"{report.Http.Describe(),-5} {report.Data.Describe(),-5}  {report.Describe()}");
        Console.ForegroundColor = previous;

        // Причина отказа выводится всегда: вердикт без объяснения нечем
        // проверить, а ошибиться проверка может — и ошибается.
        if (!report.Actionable)
            return;

        var why = report.Data.Detail ?? report.Tcp.Detail
            ?? report.Tls13.Detail ?? report.Tls12.Detail ?? report.Http.Detail;

        if (why is not null)
            Console.WriteLine($"      {Truncate(why, 88)}");
    }

    private static string DescribeDepth(CheckDepth depth) => depth switch
    {
        CheckDepth.Quick => "Быстрая проверка",
        CheckDepth.Full => "Полная проверка",
        _ => "Точечная проверка",
    };

    /// <summary>
    /// Показывает имена, прибитые в файле hosts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отдельным разделом, потому что это отменяет всё остальное. Запись
    /// в hosts перебивает любой наш маршрут: имя разрешается ещё до того,
    /// как до него доберётся наш резолвер, fakeip не выдаётся, и трафик
    /// уходит по прибитому адресу мимо туннеля.
    /// </para>
    /// <para>
    /// Найдено на живом случае. ChatGPT «умер» и не открывался; в hosts
    /// лежало шестьсот семьдесят имён, прибитых к одному адресу, и адрес
    /// этот давно не отвечал. Ни десинк, ни VPN тут ни при чём, а по
    /// признакам выглядело как блокировка.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Какие из проверенных имён прибиты в hosts.
    /// </summary>
    /// <remarks>
    /// Совпадение точное. Зоны здесь не годятся: <c>FindPinned</c> ищет
    /// поддомены, что верно для правила на всю зону, но здесь речь про
    /// конкретное имя, которое мы только что проверяли, — и приписывать
    /// ему запись о соседе значило бы врать.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> FindPinned(IReadOnlyList<TargetReport> reports)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hosts = HostsFile.Read();

        if (hosts.Count == 0)
            return found;

        foreach (var report in reports)
        {
            if (hosts.TryGetValue(report.Host, out var addresses) && addresses.Count > 0)
                found[report.Host] = string.Join(", ", addresses);
        }

        return found;
    }

    private static void PrintPinned(IReadOnlyDictionary<string, string> pinned)
    {
        if (pinned.Count == 0)
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;

        Console.WriteLine();
        Console.WriteLine("Прибито в файле hosts — это отменяет любой наш маршрут");

        foreach (var (host, address) in pinned.OrderBy(p => p.Key, StringComparer.Ordinal))
            Console.WriteLine($"  {Truncate(host, 34),-34} → {address}");

        Console.WriteLine();
        Console.WriteLine("  Имя разрешается до нашего резолвера, fakeip не выдаётся, и трафик");
        Console.WriteLine("  идёт по этому адресу мимо туннеля. Совета по ним не будет: пока");
        Console.WriteLine("  запись на месте, маршрут ничего не решает.");
        Console.WriteLine($"  Файл: {HostsFile.DefaultPath}");

        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Объясняет столбцы до того, как они появятся.
    /// </summary>
    /// <remarks>
    /// Без этого таблица читается как набор «ок» и «нет», из которого не
    /// следует ничего. Смысл проверки в том, что каждый столбец отвечает
    /// за свой способ вмешательства, и лечатся они разным — об этом и
    /// сказано во втором столбце пояснения.
    /// </remarks>
    private static void PrintLegend()
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;

        Console.WriteLine();
        Console.WriteLine("  Что проверяется                              Что означает отказ");
        Console.WriteLine("  " + new string('-', 74));
        Console.WriteLine("  TCP     соединение на 443                     порт или маршрут закрыт → VPN");
        Console.WriteLine("  TLS1.2  рукопожатие старой версии             DPI разбирает имя сайта → десинк");
        Console.WriteLine("  TLS1.3  рукопожатие нынешней версии           DPI разбирает имя сайта → десинк");
        Console.WriteLine("  HTTP    обычный запрос на 80                  отвечает ли хост вообще");
        Console.WriteLine("  ДАННЫЕ  идёт ли поток после рукопожатия       поток убивают позже → VPN");
        Console.WriteLine();
        Console.WriteLine("  Столбец ДАННЫЕ важнее прочих: соединение может встать, а сайт");
        Console.WriteLine("  не открыться — так ведёт себя обрыв потока после опознания.");

        Console.ForegroundColor = previous;
    }

    private static void PrintColumnHeader()
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {"",-30} {"TCP",-5} {"1.2",-5} {"1.3",-5} {"HTTP",-5} {"ДАННЫЕ",-5}  вердикт");
        Console.ForegroundColor = previous;
    }

    private static void PrintBaseline(NetworkBaseline baseline)
    {
        Console.WriteLine("Сеть");
        Line("TLS", baseline.Tls);
        Line("HTTP :80", baseline.Http);
        Line("DoH", baseline.Doh);

        // ICMP и IPv6 отделены от прочего: их отсутствие — свойство сети,
        // а не след вмешательства, и на вердикт оно влиять не должно.
        Line("ICMP", baseline.Icmp, "недоступен — на вердикт не влияет");
        Line("IPv6", baseline.IpV6, "недоступен — пробы по IPv6 пропущены");

        static void Line(string name, bool ok, string? whenNot = null)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.DarkGray;
            Console.WriteLine($"  {name,-12} {(ok ? "доступен" : whenNot ?? "недоступен")}");
            Console.ForegroundColor = previous;
        }
    }

    private static void PrintSummary(IReadOnlyList<TargetReport> reports, IReadOnlyList<string> badAddresses)
    {
        var byKind = reports.Where(r => r.Actionable)
            .GroupBy(r => r.Kind)
            .OrderByDescending(g => g.Count())
            .ToList();

        Console.WriteLine();
        Console.WriteLine("Итог");

        if (byKind.Count == 0)
        {
            Console.WriteLine("  всё доступно напрямую — обходить нечего");
        }
        else
        {
            foreach (var group in byKind)
            {
                var names = string.Join(", ", group.Select(r => r.Host).Take(6));
                var more = group.Count() > 6 ? $" и ещё {group.Count() - 6}" : string.Empty;

                Console.WriteLine($"  {group.First().Describe()}: {group.Count()} — {group.First().Remedy()}");
                Console.WriteLine($"    {names}{more}");
            }
        }

        // Сказано ровно то, что измерено. «Подмена DNS» звучала бы весомее,
        // но снаружи не отличить подставленный адрес от честного, до которого
        // закрыт маршрут, — а вот разница между двумя резолверами измерена,
        // и чинится она сменой резолвера.
        if (badAddresses.Count == 0)
        {
            Console.WriteLine("  системный резолвер: адреса рабочие");
        }
        else
        {
            Console.WriteLine(
                $"  системный резолвер даёт нерабочий адрес: {string.Join(", ", badAddresses)}");
            Console.WriteLine("    у DoH адрес рабочий — поможет пункт меню «DNS»");
        }

        // Имена без адреса называются отдельно и вполголоса. Молчать о них
        // нельзя — иначе непонятно, куда делись строки из списка, — но и
        // в перечень закрытого им нельзя: закрывать там нечего.
        var noAddress = reports.Where(r => r.Kind == BlockKind.NoAddress).ToList();

        if (noAddress.Count > 0)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(
                $"  без записи A (проверять нечего): {string.Join(", ", noAddress.Select(r => r.Host).Take(6))}" +
                (noAddress.Count > 6 ? $" и ещё {noAddress.Count - 6}" : string.Empty));
            Console.ForegroundColor = previous;
        }
    }

    /// <summary>
    /// Сверяет измеренное с настроенным.
    /// </summary>
    /// <remarks>
    /// Ради этого всё и затевалось. Знать, что цель закрыта, полезно; знать,
    /// что она закрыта способом, которого нынешнее правило не лечит, — вот
    /// что меняет настройку.
    /// </remarks>
    /// <summary>Что проверка предлагает поменять.</summary>
    private sealed record Suggestion
    {
        public required string Host { get; init; }
        public required RoutingMode Now { get; init; }
        public required RoutingMode Should { get; init; }
        public required string Why { get; init; }
    }

    /// <summary>
    /// Сверяет измеренное с настроенным.
    /// </summary>
    /// <remarks>
    /// Ради этого всё и затевалось. Знать, что цель закрыта, полезно; знать,
    /// что она закрыта способом, которого нынешнее правило не лечит, — вот
    /// что меняет настройку.
    /// </remarks>
    private static IReadOnlyList<Suggestion> Suggestions(
        IReadOnlyList<TargetReport> reports,
        RuleEngine? engine)
    {
        if (engine is null)
            return [];

        var result = new List<Suggestion>();

        foreach (var report in reports)
        {
            var now = engine.Evaluate(new Core.Connections.ConnectionEvent
            {
                Timestamp = DateTimeOffset.Now,
                Protocol = Core.Connections.ProtocolKind.Tcp,
                RemoteAddress = null,
                RemotePort = 443,
                Hostname = report.Host,
            }).Mode;

            // Отказ резолвера маршрутом не лечится, и предлагать здесь VPN
            // значило бы спорить с собственным советом двумя строками выше,
            // где сказано «свой DNS».
            var should = report.Kind switch
            {
                BlockKind.None => RoutingMode.Direct,
                BlockKind.TlsDpi => RoutingMode.Desync,
                BlockKind.Stall or BlockKind.HttpsPort or BlockKind.Full or BlockKind.Sinkhole => RoutingMode.Proxy,
                _ => now,
            };

            // Доступное напрямую не трогаем: десинк ему не мешает, и советовать
            // менять то, что работает, значит плодить лишние правки. Имя без
            // адреса не трогаем тем более — менять там нечего.
            if (!report.Actionable || now == should)
                continue;

            result.Add(new Suggestion
            {
                Host = report.Host,
                Now = now,
                Should = should,
                Why = report.Describe(),
            });
        }

        return result;
    }

    private static void PrintAdvice(IReadOnlyList<Suggestion> suggestions)
    {
        if (suggestions.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("Стоит изменить");

        foreach (var s in suggestions)
            Console.WriteLine($"  {Truncate(s.Host, 28),-28} {Describe(s.Now),-10} → {Describe(s.Should),-10} {s.Why}");
    }

    /// <summary>
    /// Предлагает записать предложенное и, если согласились, записывает.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Спрашивает, а не делает молча. Проверка видит один момент времени,
    /// блокировки мигают, и запись без спроса означала бы, что разовая
    /// сетевая икота меняет настройку насовсем.
    /// </para>
    /// <para>
    /// Пишет доменные правила в пользовательский слой — тот же файл и тот же
    /// вид, что у пункта «свои домены». Ничего нового в формате не заводится,
    /// и написанное отсюда можно снять оттуда.
    /// </para>
    /// </remarks>
    private static void ApplyIfConfirmed(IReadOnlyList<Suggestion> suggestions, string userRulesPath)
    {
        if (suggestions.Count == 0)
            return;

        Console.WriteLine();
        Console.Write(
            $"Записать {Plural(suggestions.Count, "это", "эти", "эти")} {suggestions.Count} " +
            $"{Plural(suggestions.Count, "правило", "правила", "правил")}? [д/н]: ");

        var answer = Console.ReadLine();

        // Конец ввода — не согласие. Проверку запускают и из сценариев,
        // и молча переписать там правила было бы неожиданностью.
        if (answer is null || !(answer.Trim().StartsWith('д') || answer.Trim().StartsWith('y')))
        {
            Console.WriteLine("Ничего не записано. Изменить вручную — пункт меню «Сервисы и маршруты».");
            return;
        }

        var file = UserRulesFile.Load(userRulesPath);

        foreach (var s in suggestions)
            file.Set(MatchKind.Domain, $"*.{s.Host}", s.Should);

        file.Save();

        Message($"Записано в {userRulesPath}. Чтобы применить — перезапустите (пункт 7).", ConsoleColor.Green);
    }

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "VPN",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };

    /// <summary>
    /// Собирает цели из справочника сервисов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Не все домены части: сорок три домена Discord закрывают одинаково,
    /// и проверять каждый — впустую потраченные минуты. Но и не один: у Steam
    /// первым в списке идёт <c>steamcommunity.com</c>, а сломан был заодно
    /// и <c>store.steampowered.com</c>, о котором никто бы не узнал.
    /// </para>
    /// <para>
    /// Берутся первые непроверенные. Прежде бралось строго первое, и если его
    /// уже занял соседний список, часть выпадала из проверки целиком, ничем
    /// этого не показав.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(string Host, string Service)> CollectTargets(
        string configPath,
        string userRulesPath,
        string? zapretRoot,
        string? only,
        CheckDepth depth,
        out RuleEngine? engine)
    {
        int perPart = depth switch
        {
            CheckDepth.Quick => 1,
            CheckDepth.Full => 2,
            _ => 4,
        };

        engine = null;

        try
        {
            engine = RuleSetLoader.LoadLayered(configPath, userRulesPath);
            RuleSetExpander.Expand(engine.RuleSet, zapretRoot);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Правила не читаются, советов не будет: {ex.GetBaseException().Message}");
        }

        var targets = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in ServiceCatalog.All)
        {
            if (only is not null && !service.Name.Contains(only, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var part in service.Parts)
            {
                // Адресные части проверять нечем: у них нет имени, а стучаться
                // в подсеть наугад — не проверка.
                if (part.ByAddress)
                    continue;

                var domains = HostListReader.Read(part.List, zapretRoot, out _);
                int taken = 0;

                foreach (var domain in domains)
                {
                    if (taken == perPart)
                        break;

                    var host = domain.TrimStart('*', '.');

                    if (!seen.Add(host))
                        continue;

                    targets.Add((host, $"{service.Name} · {part.Name}"));
                    taken++;
                }
            }
        }

        return targets;
    }

    private static void WarnIfEnginesRunning()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("Движки работают — проверка покажет сеть уже с обходом, а не без него.");
        Console.WriteLine("Чтобы увидеть, что закрыто на самом деле, остановите их и повторите.");
        Console.ForegroundColor = previous;
    }

    private static void Message(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine();
        Console.WriteLine(text);
        Console.ForegroundColor = previous;
    }

    /// <summary>Согласует существительное с числом.</summary>
    private static string Plural(int count, string one, string few, string many)
    {
        var tens = count % 100;

        if (tens is >= 11 and <= 14)
            return many;

        return (count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";
}
