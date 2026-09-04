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

    /// <summary>Порт Clash API движка — тот же, что прописывает супервизор.</summary>
    private const int ClashApiPort = 9090;

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
        var targets = CollectTargets(configPath, userRulesPath, zapretRoot, only, depth, settings.Mode, out var engine);

        if (targets.Count == 0)
        {
            Message(
                only is null
                    ? "Проверять нечего: доменные списки Zapret не найдены. Укажите --zapret-root."
                    : $"Сервиса с «{only}» в названии нет. Список — в пункте меню «Сервисы и маршруты».",
                ConsoleColor.Yellow);

            return 2;
        }

        bool enginesRunning = WarnIfEnginesRunning();

        var setup = await PrintSetupAsync(settings, enginesRunning, cancellationToken);
        PrintLegend();

        Console.WriteLine();
        Console.WriteLine("Проверяю, что умеет сеть…");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var escape = EscapeWatcher.Watch(stop);

        var baseline = await BlockCheck.MeasureBaselineAsync(stop.Token);

        Console.WriteLine();
        PrintBaseline(baseline);

        Console.WriteLine();
        Console.WriteLine(
            $"{DescribeDepth(depth)}: {targets.Count} {Plural(targets.Count, "цель", "цели", "целей")}."
            + (escape.Armed ? " Esc — прервать и показать, что успело набраться." : string.Empty));
        Console.WriteLine();
        PrintColumnHeader();

        // Читается до проб: строка помечается в тот же миг, когда печатается,
        // а не задним числом в итоге.
        var pinned = FindPinned(targets.Select(t => t.Host));

        var reports = await RunProbesAsync(
            targets,
            engine,

            // Не просто «движки работают», а «туннель в этом режиме есть».
            // В «только десинк» sing-box не поднимается вовсе, а правила
            // на именах остаются — и без этой оговорки отчёт помечал их «чз».
            enginesRunning && settings.NeedsProxy,
            pinned,
            LoadPreset(settings, zapretRoot),
            stop.Token);

        // Сторож снимается здесь, а не в конце метода. Он читает клавиши
        // через ReadKey и, оставшись жить, забирал их у следующих вопросов:
        // человек отвечал на «записать правило?», а ответ доставался сторожу.
        // Со стороны это выглядело зависанием, которое проходит, если долго
        // жать на клавиши, — часть нажатий всё-таки доходила.
        escape.Dispose();

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

        PrintSummary(reports, badAddresses, pinned);
        PrintKnownFalseAlarms(reports);
        PrintPinned(pinned, engine);

        // Предложение выводится и после прерывания. Проверенное остаётся
        // проверенным независимо от того, дошли ли до конца, а человек,
        // нажавший Esc, чаще всего уже увидел в бегущих строках то,
        // ради чего проверку и запускал.
        // Мнимые отказы советов не получают. Сказать «может быть неправдой»
        // и тут же предложить починку — значит переложить выбор на человека,
        // ничего ему не дав: у него ровно те же сведения, что и у нас.
        var suggestions = Suggestions(reports, engine, enginesRunning)
            .Where(s => !pinned.ContainsKey(s.Host))
            .Where(s => !FalseAlarmProne.ContainsKey(s.Host))
            .ToList();

        PrintDeadlocks(Deadlocks(reports, engine, setup).Where(d => !pinned.ContainsKey(d.Host)).ToList());
        PrintAdvice(suggestions, partial: stop.IsCancellationRequested);
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
        RuleEngine? engine,
        bool tunnelInUse,
        IReadOnlyDictionary<string, string> pinned,
        (ZapretPreset Preset, string? Root)? preset,
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
                // Выясняется до пробы, а не после: от этого зависит не пометка
                // в строке, а сам вердикт. Внутрь туннеля DPI не заглядывает,
                // и обрыв там — про туннель, а не про сеть.
                //
                // Режим спрашивается наравне с правилом, и это существенно.
                // В «только десинк» туннеля нет вовсе, а правила на именах
                // остаются прежними — и отчёт помечал их «чз», объявляя
                // «туннель не доставил» там, где туннеля не поднимали.
                // Telegram в таком отчёте выглядел бедой трубы, хотя упирался
                // в прибитый адрес, который рвёт DPI.
                bool tunnelled = tunnelInUse && engine is not null
                    && CurrentMode(engine, target.Host) == RoutingMode.Proxy;

                var report = await BlockCheck.CheckAsync(
                    target.Host, target.Service, cancellationToken, tunnelled);

                lock (console)
                {
                    reports.Add(report);
                    PrintRow(report, tunnelled, pinned.ContainsKey(target.Host), preset);
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

    /// <param name="pinned">
    /// Имя прибито в hosts: строка описывает прибитый адрес, а не сайт.
    /// </param>
    /// <param name="preset">
    /// Действующий пресет; по нему вычисляется, какая секция взяла бы это имя.
    /// </param>
    private static void PrintRow(
        TargetReport report,
        bool throughTunnel,
        bool pinned,
        (ZapretPreset Preset, string? Root)? preset)
    {
        var previous = Console.ForegroundColor;

        // Метка «чз» — через туннель, «hs» — через прибитый в hosts адрес.
        // Обе означают одно: измерен посредник, и вердикт относится к нему.
        var mark = pinned ? "hs" : throughTunnel ? "чз" : "  ";

        Console.ForegroundColor = report.Actionable && !pinned ? ConsoleColor.Red : ConsoleColor.DarkGray;
        Console.WriteLine(
            $"  {Truncate(report.Host, 27),-27} {mark,-3}" +
            $"{report.Tcp.Describe(),-5} " +
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

        // Какая секция пресета взяла бы это имя. Без этого правка пресета —
        // угадывание: чинят секцию, до которой исполнение не доходит, потому
        // что раньше сработала другая, по большому списку.
        if (preset is not { } p)
            return;

        Console.ForegroundColor = ConsoleColor.DarkGray;

        Console.WriteLine(PresetMatcher.For(p.Preset, report.Host, p.Root) is { } match
            ? $"      секция #{match.Ordinal} {Truncate(match.Describe(), 82)}"
            : "      секция: ни одна доменная не совпала");

        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Читает действующий пресет, чтобы знать, чья секция чем занята.
    /// </summary>
    /// <remarks>
    /// Отсутствие пресета — не ошибка: режим «только VPN» его не запускает,
    /// да и файл могли удалить. Тогда строк про секции просто не будет.
    /// </remarks>
    private static (ZapretPreset Preset, string? Root)? LoadPreset(AppSettings settings, string? zapretRoot)
    {
        if (settings.PresetName is null)
            return null;

        try
        {
            var path = ZapretPaths.FindPreset(settings.PresetName);

            return path is null ? null : (new PresetReader().Load(path), zapretRoot);
        }
        catch (Exception)
        {
            return null;
        }
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
    private static IReadOnlyDictionary<string, string> FindPinned(IEnumerable<string> names)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var hosts = HostsFile.Read();

        if (hosts.Count == 0)
            return found;

        foreach (var name in names)
        {
            if (hosts.TryGetValue(name, out var addresses) && addresses.Count > 0)
                found[name] = string.Join(", ", addresses);
        }

        return found;
    }

    /// <summary>
    /// Что имя разрешилось бы без пина.
    /// </summary>
    /// <remarks>
    /// Спрашивается у DoH: системный резолвер вернул бы сам пин — он его
    /// и назначает, — и сравнивать было бы не с чем.
    /// </remarks>
    private static string? RealAddress(string host)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            http.DefaultRequestHeaders.Accept.Add(new("application/dns-json"));

            var json = http.GetStringAsync(
                $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A").Result;

            using var document = System.Text.Json.JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Answer", out var answers))
                return null;

            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 1
                    && answer.TryGetProperty("data", out var data))
                {
                    return data.GetString();
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    /// <summary>
    /// Имена, которым мы назначили маршрут, но пин его отменяет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Всегда ошибка настройки, и притом бесшумная: правило написано, в отчёте
    /// о маршрутах числится, а трафик идёт мимо. Ловится без единого замера —
    /// достаточно сверить список пинов с правилами.
    /// </para>
    /// <para>
    /// Спорят они не так, как кажется. Пин отменяет разрешение имени, но
    /// не маршрут: TUN вынюхивает имя из рукопожатия, и правило по домену
    /// срабатывает поверх прибитого адреса. Победителем выходит правило,
    /// а пин оказывается ни при чём.
    /// </para>
    /// <para>
    /// Замер, который это показал. Один и тот же прибитый адрес отвечал
    /// сертификатом и живым HTTP на <c>deepl.com</c>, <c>strava.com</c>,
    /// <c>jetbrains.com</c> — у них правил нет, — и не отвечал вовсе
    /// на <c>chatgpt.com</c> и <c>auth.openai.com</c>, у которых правило
    /// <c>proxy</c> уводило соединение в туннель. Туннель был мёртв, и адрес
    /// из файла выглядел мёртвым заодно с ним, хотя был жив.
    /// </para>
    /// </remarks>
    private static void PrintPinConflicts(IReadOnlyDictionary<string, string> pinned, RuleEngine? engine)
    {
        if (engine is null)
            return;

        // Только «через VPN». Десинк адреса назначения не меняет — он калечит
        // пакеты по дороге, — и с пином не спорит вовсе: адрес берётся из
        // файла, десинк применяется к нему же. Пока сюда попадал и десинк,
        // раздел объявлял ошибкой настройки одиннадцать строк, из которых
        // девять работали. Опровергнуто наблюдением: у прибитого
        // tr.rbxcdn.com правило «десинк», и превью грузятся.
        var conflicts = pinned.Keys
            .Select(host => (Host: host, Mode: CurrentMode(engine, host)))
            .Where(pair => pair.Mode is RoutingMode.Proxy)
            .OrderBy(pair => pair.Host, StringComparer.Ordinal)
            .ToList();

        if (conflicts.Count == 0)
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;

        Console.WriteLine();
        Console.WriteLine("Пин отменяет назначенный маршрут — это всегда ошибка настройки");

        // Строка говорит то же, что и пояснение под ней. Прежде она утверждала
        // обратное — «идёт по пину напрямую», — и раздел противоречил сам себе
        // в двух соседних абзацах.
        foreach (var (host, mode) in conflicts)
            Console.WriteLine($"  {Truncate(host, 34),-34} победит правило «{Describe(mode)}», пин не сработает");

        Console.WriteLine();
        Console.WriteLine("  Прибитый адрес уводится в туннель, и пин теряет смысл: имя узнаётся");
        Console.WriteLine("  из рукопожатия, правило по домену срабатывает, и соединение уходит");
        Console.WriteLine("  не туда, куда указывает файл. Нужен туннель — снимите пин; нужен");
        Console.WriteLine("  адрес из файла — поставьте «напрямую» в пункте «Сервисы и маршруты».");
        Console.WriteLine();
        Console.WriteLine("  С «десинком» пин не спорит: тот адреса не меняет, а только");
        Console.WriteLine("  вмешивается в пакеты по дороге к нему.");

        Console.ForegroundColor = previous;
    }

    /// <param name="engine">
    /// Нужен, чтобы отличить безобидный пин от пина поверх маршрута.
    /// Второе — всегда ошибка настройки, и молчать о ней нельзя.
    /// </param>
    private static void PrintPinned(IReadOnlyDictionary<string, string> pinned, RuleEngine? engine)
    {
        if (pinned.Count == 0)
            return;

        PrintPinConflicts(pinned, engine);

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;

        Console.WriteLine();
        Console.WriteLine("Прибито в файле hosts — это отменяет любой наш маршрут");

        // Рядом с пином — настоящий адрес. Без него по отчёту не понять,
        // устарела запись или нет, а именно это чаще всего и требуется:
        // протухший пин ведёт в никуда и выглядит блокировкой.
        foreach (var (host, address) in pinned.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var real = RealAddress(host);

            Console.WriteLine(real is null || address.Contains(real, StringComparison.Ordinal)
                ? $"  {Truncate(host, 34),-34} → {address}"
                : $"  {Truncate(host, 34),-34} → {address}   (на деле {real})");
        }

        Console.WriteLine();
        Console.WriteLine("  Имя разрешается до нашего резолвера, fakeip не выдаётся, и адрес");
        Console.WriteLine("  берётся из файла. Но маршрут этим не отменяется: имя вынюхивается");
        Console.WriteLine("  из рукопожатия, и правило по домену срабатывает всё равно.");
        Console.WriteLine();
        Console.WriteLine("  Их строки выше описывают прибитый адрес, а не сайт, и в итог");
        Console.WriteLine("  по видам блокировок не попали. Убрать запись — пункт «Файл hosts».");
        Console.WriteLine($"  Файл: {HostsFile.DefaultPath}");

        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Чем именно снята эта картина: пресет, сервер, режим.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отчёт без этого нечем сравнить с другим отчётом. Проверка снимает
    /// не свойства сети, а поведение сети под конкретной настройкой, и два
    /// прогона под разными пресетами дают разные строки — при том что оба
    /// выглядят одинаково достоверно.
    /// </para>
    /// <para>
    /// Сервер спрашивается у самого движка, а не берётся из настроек:
    /// в настройках стоит «авто», а работает в этот момент один определённый,
    /// и назвать надо его.
    /// </para>
    /// </remarks>
    private static async Task<Setup> PrintSetupAsync(
        AppSettings settings,
        bool enginesRunning,
        CancellationToken cancellationToken)
    {
        var previous = Console.ForegroundColor;

        Console.WriteLine();
        Console.WriteLine($"Снято {DateTime.Now:dd.MM.yyyy HH:mm}");
        Console.ForegroundColor = ConsoleColor.DarkGray;

        Console.WriteLine($"  Режим:   {settings.DescribeMode()}");
        Console.WriteLine($"  Пресет:  {(enginesRunning ? settings.DescribePreset() : "не применён — движки остановлены")}");

        var server = enginesRunning
            ? await CurrentServerAsync(settings, cancellationToken)
            : null;

        Console.WriteLine($"  Сервер:  {server ?? "не определён"}");
        Console.WriteLine($"  DNS:     {settings.DnsServer}");

        Console.ForegroundColor = previous;

        if (!enginesRunning)
            return new Setup { Tunnel = TunnelState.Off, Exit = new ExitReading { Tunnelled = false } };

        // Порядок важен: состояние туннеля выясняется до всего прочего.
        // Мёртвый туннель обесценивает вердикты по проксируемым именам,
        // и знать об этом надо раньше, чем они напечатаны.
        var tunnel = await TunnelStateAsync(server, cancellationToken);
        var exit = await ReadExitAsync(cancellationToken);

        PrintExit(exit);
        WarnIfTunnelIsDead(tunnel);
        WarnIfExitIsDomestic(exit);

        return new Setup { Tunnel = tunnel, Exit = exit };
    }

    /// <summary>Обстановка, в которой снят отчёт.</summary>
    private sealed record Setup
    {
        public required TunnelState Tunnel { get; init; }

        public required ExitReading Exit { get; init; }
    }

    /// <summary>
    /// Проходит ли через туннель хоть что-нибудь.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Спрашивается у самого движка: Clash API умеет прогнать пробный запрос
    /// через названный выход и вернуть задержку. Это единственный способ
    /// отделить «сайт закрыт» от «труба не работает», не трогая маршруты.
    /// </para>
    /// <para>
    /// Заведено после случая, когда пятнадцать серверов подписки разом
    /// перестали отвечать — ни один порт не принимал соединение, — а проверка
    /// исправно обвиняла в этом сайты и предлагала менять им маршруты.
    /// </para>
    /// </remarks>
    private static async Task<TunnelState> TunnelStateAsync(string? server, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(server))
            return TunnelState.Unknown;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };

            var url = $"http://127.0.0.1:{ClashApiPort}/proxies/{Uri.EscapeDataString(server)}/delay"
                + "?timeout=5000&url=" + Uri.EscapeDataString("https://www.gstatic.com/generate_204");

            using var response = await http.GetAsync(url, cancellationToken);

            // Движок отвечает 200 с задержкой, когда проба дошла, и 5xx,
            // когда не дошла. Различать по телу не нужно: сам факт неуспеха
            // и означает, что через этот выход трафик не идёт.
            return response.IsSuccessStatusCode ? TunnelState.Alive : TunnelState.Dead;
        }
        catch (Exception)
        {
            // Движок мог не поднять Clash API. Это незнание, а не приговор:
            // объявить туннель мёртвым по недоступности его же диагностики
            // значило бы обвинить исправную трубу.
            return TunnelState.Unknown;
        }
    }

    /// <summary>
    /// Внешний адрес и то, через что он получен.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прежде здесь стоял простой запрос к службе определения адреса, а ответ
    /// подписывался «Выход туннеля». Замер показал, что это неверно: имя
    /// службы разрешается в настоящий адрес, а не в fakeip, — значит правило
    /// маршрутизации отправляет её напрямую, и полученная страна описывает
    /// домашнего провайдера. Отчёт уверенно называл выход туннеля российским,
    /// ни разу через туннель не сходив.
    /// </para>
    /// <para>
    /// Теперь замер сопровождается ответом на вопрос, через что он шёл.
    /// Утверждение о туннеле делается только когда он и вправду измерен.
    /// </para>
    /// </remarks>
    private static async Task<ExitReading> ReadExitAsync(CancellationToken cancellationToken)
    {
        const string probe = "api.ipify.org";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

            var address = (await http.GetStringAsync($"https://{probe}", cancellationToken)).Trim();
            var country = (await http.GetStringAsync("https://ipinfo.io/country", cancellationToken)).Trim();

            return new ExitReading
            {
                Address = address,
                Country = country,
                Tunnelled = await WentThroughTunnelAsync(probe, cancellationToken),
            };
        }
        catch (Exception)
        {
            // Не выяснилось — молчим. Отсутствие сведений хуже ложных,
            // но ложные здесь стоили бы смены рабочего сервера ни за чем.
            return new ExitReading { Tunnelled = false };
        }
    }

    /// <summary>Разрешается ли имя в fakeip — то есть уйдёт ли оно в туннель.</summary>
    private static async Task<bool> WentThroughTunnelAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken);

            return addresses.Any(a => TunnelHealth.IsFakeIp(a));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void PrintExit(ExitReading exit)
    {
        if (exit.Address is null)
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;

        // Через что шёл замер, называется рядом с адресом. Без этой пометки
        // строку нельзя прочесть верно, а прочтённая неверно она обвиняет
        // туннель в том, чего он не делал.
        Console.WriteLine(exit.Tunnelled
            ? $"  Выход:   {exit.Address} ({exit.Country}) — через туннель"
            : $"  Выход:   {exit.Address} ({exit.Country}) — мимо туннеля, это домашний канал");

        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Кричит, если туннель поднят, но ничего не пропускает.
    /// </summary>
    /// <remarks>
    /// Печатается раньше вердиктов и нарочно громко: это не одна из бед,
    /// а причина, по которой все прочие строки отчёта про проксируемые имена
    /// ничего не стоят.
    /// </remarks>
    private static void WarnIfTunnelIsDead(TunnelState state)
    {
        if (state != TunnelState.Dead)
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Red;

        Console.WriteLine();
        Console.WriteLine("  Туннель поднят, но не пропускает ничего.");
        Console.WriteLine();
        Console.WriteLine("  Всё, что заведено в туннель, ниже будет выглядеть закрытым — и это");
        Console.WriteLine("  про трубу, а не про сайты. Сперва почините связь с сервером:");
        Console.WriteLine("  выберите другой в пункте «VPN» или обновите подписку.");

        Console.ForegroundColor = previous;
    }

    private static void WarnIfExitIsDomestic(ExitReading exit)
    {
        if (!exit.TunnelExitsDomestically)
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;

        Console.WriteLine();
        Console.WriteLine("  Выход туннеля — в России. Для сервисов, закрывающихся от страны,");
        Console.WriteLine("  это не помогает: адрес остаётся российским. «Авто по задержке»");
        Console.WriteLine("  берёт ближайший сервер, а ближайший — свой же. Выберите");
        Console.WriteLine("  зарубежный в пункте «VPN».");

        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Какой сервер подписки работает прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Через Clash API движка. Настройка «авто по задержке» означает, что имени
    /// сервера в конфиге нет вовсе — выбор делается на ходу, и узнать его можно
    /// только у того, кто его сделал.
    /// </remarks>
    private static async Task<string?> CurrentServerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

            var json = await http.GetStringAsync(
                $"http://127.0.0.1:{ClashApiPort}/proxies/auto",
                cancellationToken);

            using var document = System.Text.Json.JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("now", out var now))
                return null;

            var name = now.GetString();

            // Селектор может указывать на группу, а не на сервер. Тогда
            // спрашиваем ещё раз у неё — иначе в отчёте будет «auto-latency»,
            // что не ответ на вопрос «через что мы сейчас ходим».
            if (name is not null && name.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
            {
                var inner = await http.GetStringAsync(
                    $"http://127.0.0.1:{ClashApiPort}/proxies/{Uri.EscapeDataString(name)}",
                    cancellationToken);

                using var group = System.Text.Json.JsonDocument.Parse(inner);

                if (group.RootElement.TryGetProperty("now", out var chosen))
                    return chosen.GetString();
            }

            return name;
        }
        catch (Exception)
        {
            // Движок мог не поднять API или уже остановиться. Отчёт без имени
            // сервера хуже, но всё ещё отчёт.
            return null;
        }
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
        Console.WriteLine();
        Console.WriteLine("  Метка «чз» — цель уже идёт через туннель. Её строка описывает");
        Console.WriteLine("  поведение туннеля, а не сети: TCP там встаёт всегда, потому что");
        Console.WriteLine("  соединение принимает sing-box, ещё не дозвонившись до хоста.");
        Console.WriteLine("  Метка «hs» — имя прибито в hosts, и строка описывает прибитый");
        Console.WriteLine("  адрес. Такие в итог по видам блокировок не идут: чужой посредник");
        Console.WriteLine("  может отказывать за сайт, который сам работает.");

        Console.ForegroundColor = previous;
    }

    private static void PrintColumnHeader()
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {"",-27} {"",-3}{"TCP",-5} {"1.2",-5} {"1.3",-5} {"HTTP",-5} {"ДАННЫЕ",-5}  вердикт");
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

    /// <param name="pinned">
    /// Имена, прибитые в hosts. Их вердикт относится к прибитому адресу,
    /// а не к сайту, и в итог по видам блокировок им нельзя.
    /// </param>
    /// <remarks>
    /// Исключение прибитых — не придирка. <c>claude.ai</c> был прибит к чужому
    /// прокси, тот отвечал 403, и проверка объявила «сайт отказывает по стране»,
    /// хотя настоящий адрес отвечал 302 и прекрасно работал. Мы измеряли
    /// не сайт, а посредника, и приписывать его отказ сайту нельзя.
    /// </remarks>
    private static void PrintSummary(
        IReadOnlyList<TargetReport> reports,
        IReadOnlyList<string> badAddresses,
        IReadOnlyDictionary<string, string> pinned)
    {
        var byKind = reports.Where(r => r.Actionable && !pinned.ContainsKey(r.Host))
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

    /// <summary>Что проверка предлагает поменять.</summary>
    private sealed record Suggestion
    {
        public required string Host { get; init; }
        public required RoutingMode Now { get; init; }
        public required RoutingMode Should { get; init; }
        public required string Why { get; init; }
    }

    /// <summary>Случай, которого наша лесенка не покрывает.</summary>
    private sealed record Deadlock
    {
        public required string Host { get; init; }
        public required RoutingMode Now { get; init; }
        public required string What { get; init; }
        public required string Hope { get; init; }
    }

    /// <summary>
    /// Подбирает маршрут лесенкой: напрямую → десинк → VPN.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядок не произволен: каждая следующая ступень дороже предыдущей.
    /// Десинк ломается при смене прошивки DPI, туннель добавляет задержку
    /// и расходует квоту подписки. Поэтому поднимаемся ровно настолько,
    /// насколько вынуждает измеренное, и ни ступенью выше.
    /// </para>
    /// <para>
    /// Вниз не спускаемся, пока движки работают, — и это главное исправление.
    /// Прежде «доступен» означало «хватит и напрямую», но при включённом
    /// обходе сайт доступен как раз благодаря десинку. Совет снять десинк
    /// с работающего сайта его бы и сломал.
    /// </para>
    /// <para>
    /// Ступени, выше которой у нас ничего нет, не существует: если и туннель
    /// не помогает, это отдельный исход, о котором надо сказать вслух,
    /// а не молча промолчать. См. <see cref="Deadlocks"/>.
    /// </para>
    /// </remarks>
    /// <param name="enginesRunning">
    /// Обход был включён во время замера, поэтому «работает» ничего
    /// не говорит о том, работало ли бы оно без него.
    /// </param>
    private static IReadOnlyList<Suggestion> Suggestions(
        IReadOnlyList<TargetReport> reports,
        RuleEngine? engine,
        bool enginesRunning)
    {
        if (engine is null)
            return [];

        var result = new List<Suggestion>();

        foreach (var report in reports)
        {
            var now = CurrentMode(engine, report.Host);

            var should = report.Kind switch
            {
                // Работает — оставляем как есть. Спуститься ниже можно только
                // зная, что оно работало бы и без нынешней ступени, а этого
                // мы при включённых движках не знаем.
                BlockKind.None => enginesRunning ? now : RoutingMode.Direct,

                // Рвут рукопожатие — ровно то, с чем десинк и справляется.
                BlockKind.TlsDpi => RoutingMode.Desync,

                // Вмешиваться не во что: рукопожатия либо нет вовсе, либо оно
                // проходит, а убивают позже. Пропускать ступень десинка честно —
                // она тут заведомо ни при чём.
                BlockKind.Stall or BlockKind.HttpsPort or BlockKind.Full
                    or BlockKind.Sinkhole or BlockKind.GeoBlock => RoutingMode.Proxy,

                // Отказ резолвера и оборванный CNAME маршрутом не лечатся.
                _ => now,
            };

            if (!report.Actionable || now == should)
                continue;

            // Вниз — никогда, пока движки работают. Прежде это правило стояло
            // только для «доступен», и лесенка спотыкалась о собственный вывод:
            // image.tmdb.org шёл через VPN, оттуда приходил RST, проверка
            // называла это «DPI по TLS» и предлагала спуститься на десинк —
            // одновременно с разделом «маршрутом не лечится» про тот же домен.
            // Диагноз ставится по нынешнему маршруту, и переносить его
            // на другую ступень нельзя.
            if (enginesRunning && Rung(should) < Rung(now))
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

    /// <summary>
    /// Случаи, где лесенка кончилась, а работать не начало.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Схема калибровки требует называть такое вслух, а не молчать: человек
    /// должен знать, что перепробовано всё, и получить возможность сообщить
    /// об этом. Молчание здесь читается как «средство не заметило», и человек
    /// идёт крутить настройки, которые ничего не решают.
    /// </para>
    /// <para>
    /// Две разновидности. Домен уже на верхней ступени и всё равно закрыт —
    /// значит и туннель не спас. Домен на десинке, а закрыт способом, который
    /// десинку не поддаётся, — тут ещё есть надежда на другую стратегию
    /// в пресете, и об этом стоит сказать отдельно.
    /// </para>
    /// </remarks>
    /// <param name="setup">
    /// Обстановка замера. Из неё берутся два обстоятельства, без которых
    /// вердикт по проксируемому имени неверен: жив ли туннель вообще
    /// и выходит ли он там же, откуда зашёл.
    /// </param>
    private static IReadOnlyList<Deadlock> Deadlocks(
        IReadOnlyList<TargetReport> reports,
        RuleEngine? engine,
        Setup setup)
    {
        if (engine is null)
            return [];

        var result = new List<Deadlock>();

        foreach (var report in reports)
        {
            if (!report.Actionable)
                continue;

            var now = CurrentMode(engine, report.Host);

            if (report.Kind == BlockKind.BrokenCname)
            {
                result.Add(new Deadlock
                {
                    Host = report.Host,
                    Now = now,
                    What = "имя ведёт в никуда — соединяться не с чем",
                    Hope = "подставьте адрес в config\\addresses.yaml",
                });

                continue;
            }

            // Уже наверху и всё равно закрыт — но приговор туннелю выносится
            // лишь тогда, когда туннель вообще был опробован.
            if (now == RoutingMode.Proxy)
            {
                result.Add(ProxiedButClosed(report, now, setup));

                continue;
            }

            // На десинке, но закрыто не тем, что десинк умеет. Ступень выше
            // ещё не пробовали, и предложение о ней уже выдано отдельно —
            // здесь только про надежду на другой рецепт.
            if (now == RoutingMode.Desync && report.Kind == BlockKind.TlsDpi)
            {
                result.Add(new Deadlock
                {
                    Host = report.Host,
                    Now = now,
                    What = "десинк применён, но рукопожатие всё равно рвут",
                    Hope = "возможна другая стратегия в пресете — сообщите",
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Имя заведено в туннель и всё равно закрыто — но чья это вина.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Три разных положения, которые прежде сливались в одно «даже через VPN».
    /// Мёртвый туннель означает, что измерена труба, а не сайт, и говорить
    /// о сайте нечего вовсе. Домашний выход означает, что туннель не менял
    /// того, на что сайт смотрит, — отказ по стране повторится один в один.
    /// И лишь когда труба жива и выходит за границей, отказ и вправду
    /// относится к сайту.
    /// </para>
    /// <para>
    /// Различать пришлось после случая, когда все пятнадцать серверов
    /// подписки перестали отвечать, а отчёт объявил закрытыми полтора
    /// десятка сайтов и предложил менять им маршруты.
    /// </para>
    /// </remarks>
    private static Deadlock ProxiedButClosed(TargetReport report, RoutingMode now, Setup setup)
    {
        if (setup.Tunnel == TunnelState.Dead)
        {
            return new Deadlock
            {
                Host = report.Host,
                Now = now,
                What = "заведён в туннель, а туннель не пропускает ничего",
                Hope = "это про трубу, а не про сайт — почините связь с сервером и повторите",
            };
        }

        if (setup.Exit.TunnelExitsDomestically)
        {
            return new Deadlock
            {
                Host = report.Host,
                Now = now,
                What = $"{report.Describe()}, но выход туннеля в России",
                Hope = "возьмите зарубежный сервер и повторите — это ещё не приговор",
            };
        }

        // «Туннель не доставил» уже сказано в самом вердикте, и добавлять
        // к нему «даже через VPN» значит повторять то же дважды и звучать
        // приговором там, где чаще всего хватает другого сервера.
        if (report.Kind == BlockKind.TunnelFailed)
        {
            return new Deadlock
            {
                Host = report.Host,
                Now = now,
                What = "туннель до него не дотянулся",
                Hope = "смените сервер подписки и повторите; десинку тут делать нечего",
            };
        }

        return new Deadlock
        {
            Host = report.Host,
            Now = now,
            What = $"{report.Describe()} даже через VPN",
            Hope = "сообщите — возможно, дело в сервере подписки или в самом сайте",
        };
    }

    /// <summary>
    /// Ступень лесенки: чем выше, тем дороже обходится.
    /// </summary>
    /// <remarks>
    /// Порядок задаёт цену, а не силу. Десинк ломается при смене прошивки DPI,
    /// туннель добавляет задержку и расходует квоту подписки, — поэтому
    /// поднимаемся только вынужденно, а спускаемся лишь тогда, когда есть
    /// чем доказать, что ступенью ниже тоже работает.
    /// </remarks>
    private static int Rung(RoutingMode mode) => mode switch
    {
        RoutingMode.Direct => 0,
        RoutingMode.Desync => 1,
        _ => 2,
    };

    private static RoutingMode CurrentMode(RuleEngine engine, string host) =>
        engine.Evaluate(new Core.Connections.ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = Core.Connections.ProtocolKind.Tcp,
            RemoteAddress = null,
            RemotePort = 443,
            Hostname = host,
        }).Mode;

    /// <summary>
    /// Показывает тупики — то, что лесенкой не берётся.
    /// </summary>
    private static void PrintDeadlocks(IReadOnlyList<Deadlock> deadlocks)
    {
        if (deadlocks.Count == 0)
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;

        Console.WriteLine();
        Console.WriteLine("Маршрутом не лечится");

        foreach (var d in deadlocks)
        {
            Console.WriteLine($"  {Truncate(d.Host, 28),-28} {Describe(d.Now),-10} {d.What}");
            Console.WriteLine($"  {"",-28} {"",-10} {d.Hope}");
        }

        Console.ForegroundColor = previous;
    }

    /// <param name="partial">Проверка была прервана и охватила не всё.</param>
    /// <remarks>
    /// Помечено экспериментальным, и это не оговорка на всякий случай.
    /// Вывод делается по одному замеру: блокировки мигают, а пути проверяются
    /// не все — что через VPN заработает, здесь не проверялось вовсе, только
    /// то, что напрямую не работает. Совет разумный, но не доказанный,
    /// и человек вправе знать, чему именно он доверяет.
    /// </remarks>
    private static void PrintAdvice(IReadOnlyList<Suggestion> suggestions, bool partial)
    {
        if (suggestions.Count == 0)
            return;

        var previous = Console.ForegroundColor;

        Console.WriteLine();
        Console.WriteLine("Стоит изменить" + (partial ? " (по неполной проверке)" : string.Empty));

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  Возможность опытная. Вывод сделан по одному замеру, а что заработает");
        Console.WriteLine("  через VPN — здесь не проверялось. После правки убедитесь сами.");
        Console.ForegroundColor = previous;
        Console.WriteLine();

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
    /// <summary>
    /// Сервисы, чей отказ в отчёте чаще всего мнимый.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Есть сервисы, где наша проба и настоящее приложение ходят разными
    /// путями, и расходятся они закономерно. Проверка стучится по имени
    /// на 443; WhatsApp работает по своим адресам и порту 5222, Discord —
    /// голосом по UDP и обновлениями по своему каналу. Отказ на 443 у них
    /// не значит, что сервис не работает.
    /// </para>
    /// <para>
    /// Замалчивать такие строки нельзя — они бывают и настоящими. Но и
    /// молчать о том, что они особенные, значит раз за разом посылать
    /// человека чинить работающее: у этого пользователя Discord и WhatsApp
    /// исправны, а в отчёте числились сломанными три прогона подряд.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> FalseAlarmProne =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["whatsapp.com"] = "приложение ходит по своим адресам и порту 5222",
            ["whatsapp.net"] = "приложение ходит по своим адресам и порту 5222",
            ["updates.discord.com"] = "обновления идут своим каналом, не через 443",
            ["disboard.org"] = "открывается через сам Discord, а не по этому имени",
            ["itch.io"] = "сайт и клиент ходят разными путями; проверено — работает",
            ["tiktok.com"] = "приложение ходит по своим адресам, не по этому имени",
        };

    /// <summary>Говорит, что часть отказов ниже может быть мнимой.</summary>
    private static void PrintKnownFalseAlarms(IReadOnlyList<TargetReport> reports)
    {
        var hits = reports
            .Where(r => r.Actionable && FalseAlarmProne.ContainsKey(r.Host))
            .OrderBy(r => r.Host, StringComparer.Ordinal)
            .ToList();

        if (hits.Count == 0)
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.DarkGray;

        Console.WriteLine();
        Console.WriteLine("  Здесь отказ может быть мнимым — проверьте сами, прежде чем чинить:");

        foreach (var report in hits)
            Console.WriteLine($"    {Truncate(report.Host, 26),-26} {FalseAlarmProne[report.Host]}");

        Console.ForegroundColor = previous;
    }

    /// <summary>
    /// Имена, которые проверять незачем: зеркала и запасные домены.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Списки Zapret ведутся для десинка, и в них соседствуют основной домен
    /// и его зеркала — <c>twitter.com</c> рядом с <c>x.com</c>,
    /// <c>rutracker.net</c> рядом с <c>rutracker.org</c>. Для правила это
    /// правильно: закрыть надо оба. Для отчёта — нет: человек читает
    /// «twitter.com закрыт» и идёт чинить то, чем не пользуется, тогда как
    /// сам сервис открывается по другому имени и в той же таблице помечен
    /// доступным.
    /// </para>
    /// <para>
    /// Собрано по разбору живых отчётов: из десяти строк «маршрутом
    /// не лечится» шесть были про домены, которых у пользователя нет
    /// в обиходе. Проверка от этого не становится слепой — правило
    /// на зеркало продолжает действовать, просто в отчёт оно не идёт.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> Mirrors = new(StringComparer.OrdinalIgnoreCase)
    {
        "twitter.com",      // работает как x.com
        "rutracker.net",    // зеркало rutracker.org
        "notion.site",      // раздача страниц, сам notion.so отдельно
        "riotgames.es",     // испанское зеркало riotgames.com
        "itch.zone",        // раздача файлов itch.io
        "rutor.info",       // зеркала одного трекера
        "rutor.is",
        "rutracker.cr",     // ещё одно зеркало rutracker.org
        "notion.so",        // сам Notion открывается, это запасной домен

        // Голые зоны сетей доставки. Записи A у них нет и не было: работают
        // только поддомены, а сама зона существует ради правил десинка,
        // которые сопоставляются по суффиксу. Проверять там нечего, и десять
        // строк «нет адреса у имени» в каждом отчёте — чистый шум.
        "cdninstagram.com",
        "akamai.net",
        "akamaiedge.net",
        "tiktokcdn.com",
        "soundcloud.cloud",
        "licdn.com",
        "nocookie.net",
        "cloudfront.net",
        "ooklaserver.net",
        "rgpub.io",
    };

    private static IReadOnlyList<(string Host, string Service)> CollectTargets(
        string configPath,
        string userRulesPath,
        string? zapretRoot,
        string? only,
        CheckDepth depth,

        // Режим передаётся снаружи, иначе проверка судит о чужой настройке:
        // правила читаются из rules.yaml, где стоит selective, и отчёт
        // в режиме «всё через VPN» писал «десинк» ровно тем именам,
        // которые в этот момент уходили в туннель.
        OperatingMode mode,
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
            engine = RuleSetLoader.LoadLayered(configPath, userRulesPath, mode);
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

                    if (Mirrors.Contains(host))
                        continue;

                    if (!seen.Add(host))
                        continue;

                    targets.Add((host, $"{service.Name} · {part.Name}"));
                    taken++;
                }
            }
        }

        return targets;
    }

    /// <returns>Работал ли обход во время проверки.</returns>
    private static bool WarnIfEnginesRunning()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
            return false;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine();
        Console.WriteLine("Движки работают — проверка покажет сеть уже с обходом, а не без него.");
        Console.WriteLine("Чтобы увидеть, что закрыто на самом деле, остановите их и повторите.");
        Console.WriteLine("Пока они работают, совета «хватит и напрямую» не будет: доступное");
        Console.WriteLine("сейчас может быть доступно как раз благодаря обходу.");
        Console.ForegroundColor = previous;

        return true;
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
