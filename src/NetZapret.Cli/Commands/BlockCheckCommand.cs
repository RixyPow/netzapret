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
internal static class BlockCheckCommand
{
    public static Task<int> RunAsync(CommandLine cmd, string defaultConfigPath, CancellationToken cancellationToken) =>
        ExecuteAsync(
            AppSettings.Load(cmd.Value("settings", AppSettings.DefaultPath)),
            cmd.Value("config", defaultConfigPath),
            cmd.Value("user-rules", UserRulesFile.DefaultPath),
            ZapretPaths.Discover(cmd.Value("zapret-root"))?.Root,
            cmd.Value("service"),
            cancellationToken);

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
        CancellationToken cancellationToken)
    {
        // Цели собираются до замеров: если проверять нечего, шесть секунд
        // на пробу сети потрачены впустую, а человек об этом узнаёт последним.
        var targets = CollectTargets(configPath, userRulesPath, zapretRoot, only, out var engine);

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

        Console.WriteLine();
        Console.WriteLine("Проверяю, что умеет сеть…");

        var baseline = await BlockCheck.MeasureBaselineAsync(cancellationToken);

        Console.WriteLine();
        PrintBaseline(baseline);

        Console.WriteLine();
        Console.WriteLine($"Проверяю {targets.Count} {Plural(targets.Count, "цель", "цели", "целей")}…");
        Console.WriteLine();

        var reports = new List<TargetReport>();
        var previous = Console.ForegroundColor;

        foreach (var (host, service) in targets)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var report = await BlockCheck.CheckAsync(host, service, cancellationToken);
            reports.Add(report);

            Console.ForegroundColor = report.Actionable ? ConsoleColor.Red : ConsoleColor.DarkGray;
            Console.WriteLine(
                $"  {Truncate(host, 30),-30} {report.Tcp.Describe(),-5} " +
                $"{report.Tls12.Describe(),-5} {report.Tls13.Describe(),-5} " +
                $"{report.Http.Describe(),-5}  {report.Describe()}");
            Console.ForegroundColor = previous;

            // Причина отказа выводится всегда: вердикт без объяснения нечем
            // проверить, а ошибиться проверка может — и ошибается.
            if (report.Actionable)
            {
                var why = report.Tcp.Detail ?? report.Tls13.Detail ?? report.Tls12.Detail ?? report.Http.Detail;

                if (why is not null)
                    Console.WriteLine($"      {Truncate(why, 88)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("Проверяю адреса от системного резолвера…");

        var badAddresses = await BlockCheck.FindBadSystemAddressesAsync(
            targets.Select(t => t.Host).Take(12).ToList(),
            cancellationToken);

        PrintSummary(reports, badAddresses);
        PrintAdvice(reports, engine, settings);

        return reports.Any(r => r.Actionable) ? 1 : 0;
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
    private static void PrintAdvice(
        IReadOnlyList<TargetReport> reports,
        RuleEngine? engine,
        AppSettings settings)
    {
        if (engine is null)
            return;

        var rows = new List<(string Host, string Now, string Should, string Why)>();

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

            var should = report.Kind switch
            {
                BlockKind.None => RoutingMode.Direct,
                BlockKind.TlsDpi => RoutingMode.Desync,
                BlockKind.HttpsPort or BlockKind.Full or BlockKind.Dns => RoutingMode.Proxy,
                _ => now,
            };

            // Доступное напрямую не трогаем: десинк ему не мешает, и советовать
            // менять то, что работает, значит плодить лишние правки. Имя без
            // адреса не трогаем тем более — менять там нечего.
            if (!report.Actionable || now == should)
                continue;

            rows.Add((report.Host, Describe(now), Describe(should), report.Describe()));
        }

        if (rows.Count == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("Стоит изменить");

        foreach (var row in rows)
            Console.WriteLine($"  {Truncate(row.Host, 28),-28} {row.Now,-10} → {row.Should,-10} {row.Why}");

        Console.WriteLine();
        Console.WriteLine("  Менять — в пункте меню «Сервисы и маршруты».");

        static string Describe(RoutingMode mode) => mode switch
        {
            RoutingMode.Proxy => "VPN",
            RoutingMode.Desync => "десинк",
            _ => "напрямую",
        };
    }

    /// <summary>
    /// Собирает цели из справочника сервисов.
    /// </summary>
    /// <remarks>
    /// По одному имени с части: проверять все сорок три домена Discord незачем,
    /// закрывают их одинаково. Берётся первое — оно же показывается в разделе
    /// сервисов как пример.
    /// </remarks>
    private static IReadOnlyList<(string Host, string Service)> CollectTargets(
        string configPath,
        string userRulesPath,
        string? zapretRoot,
        string? only,
        out RuleEngine? engine)
    {
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

                if (domains.Count == 0)
                    continue;

                var host = domains[0].TrimStart('*', '.');

                if (seen.Add(host))
                    targets.Add((host, $"{service.Name} · {part.Name}"));
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
