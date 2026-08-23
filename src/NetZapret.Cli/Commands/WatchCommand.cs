using System.Diagnostics;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Dns;
using NetZapret.Etw;
using NetZapret.Wfp;

namespace NetZapret.Cli.Commands;

/// <summary>
/// PoC-классификатор: слушает соединения и печатает решение, которое приняли бы
/// правила. Ничего не перенаправляет.
/// </summary>
/// <remarks>
/// Источников два, оба за <see cref="IConnectionEventSource"/>.
/// По умолчанию — ETW: на этой системе WFP не отдаёт события о разрешённых
/// соединениях (см. комментарий к <see cref="EtwConnectionSource"/>).
/// WFP оставлен доступным через <c>--source wfp</c>: интероп проверен, и он
/// понадобится, когда дойдёт очередь до фильтров защиты от утечек.
/// </remarks>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(CommandLine cmd, string defaultConfigPath, CancellationToken cancellationToken)
    {
        var configPath = cmd.Value("config", defaultConfigPath);
        var engine = RuleSetLoader.LoadFromFile(configPath);

        bool showAll = cmd.Has("all");
        bool useWfp = string.Equals(cmd.Value("source", "etw"), "wfp", StringComparison.OrdinalIgnoreCase);

        // Источник ETW добывает имена сам, наблюдая за ответами DNS, поэтому
        // предупреждать об отсутствии --hosts-map в этом случае не о чем.
        bool sourceProvidesNames = !useWfp && !cmd.Has("no-dns");

        var hostnames = LoadHostnameSource(cmd, engine, sourceProvidesNames);

        WfpNetEventSource? wfp = null;
        EtwConnectionSource? etw = null;
        IConnectionEventSource source;

        if (useWfp)
        {
            wfp = new WfpNetEventSource(new WfpNetEventSourceOptions
            {
                OutboundOnly = !showAll,
                SkipLoopback = !showAll,
                TcpUdpOnly = true,
                RawDumpCount = cmd.Int("dump-raw", 0),
            });

            wfp.RawDumpSink = dump =>
            {
                Console.WriteLine("--- сырое событие WFP ---");
                Console.WriteLine(dump);
            };

            source = wfp;
        }
        else
        {
            etw = new EtwConnectionSource(new EtwConnectionSourceOptions
            {
                SkipLoopback = !showAll,
                UdpDeduplicationWindow = TimeSpan.FromSeconds(cmd.Int("udp-window", 15)),
                ObserveDns = !cmd.Has("no-dns"),
            });

            source = etw;
        }

        Console.WriteLine($"Конфиг: {configPath} ({engine.RuleSet.Rules.Count} правил, default={Format(engine.RuleSet.DefaultMode)})");
        Console.WriteLine($"Источник: {source.Name}");

        await using (source)
        {
            if (wfp is not null)
            {
                wfp.Start();
                Console.WriteLine(
                    $"Настройки WFP: collect_net_events={Describe(wfp.EffectiveCollectNetEvents)}, " +
                    $"keywords=0x{(wfp.EffectiveKeywords ?? 0):X}, classify_allow={(wfp.ClassifyAllowRequested ? "да" : "НЕТ")}");
            }
            else
            {
                etw!.Start();
            }

            Console.WriteLine("Наблюдение запущено. Ctrl+C — выход.");
            Console.WriteLine();
            PrintHeader();

            // --duration позволяет прогнать наблюдение без Ctrl+C: важно не столько
            // для удобства, сколько для того, чтобы выход был штатным. При жёстком
            // завершении ETW-сессия и настройки WFP остались бы висеть в системе.
            int durationSeconds = cmd.Int("duration", 0);
            using var timedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            if (durationSeconds > 0)
            {
                timedCts.CancelAfter(TimeSpan.FromSeconds(durationSeconds));
                Console.WriteLine($"Автоостановка через {durationSeconds} с.");
            }

            var statsInterval = TimeSpan.FromSeconds(cmd.Int("stats-interval", 15));
            var stopwatch = Stopwatch.StartNew();
            var lastStats = TimeSpan.Zero;
            long matched = 0;
            long total = 0;
            long withoutHostname = 0;

            try
            {
                await foreach (var connection in source.ReadEventsAsync(timedCts.Token))
                {
                    var enriched = connection.RemoteAddress is null
                        ? connection
                        : connection.WithHostname(hostnames.Resolve(connection.RemoteAddress));

                    var decision = engine.Evaluate(enriched);

                    total++;
                    if (decision.Rule is not null)
                        matched++;
                    if (enriched.Hostname is null)
                        withoutHostname++;

                    PrintRow(enriched, decision);

                    if (statsInterval > TimeSpan.Zero && stopwatch.Elapsed - lastStats >= statsInterval)
                    {
                        lastStats = stopwatch.Elapsed;
                        PrintStats(wfp, etw, total, matched, withoutHostname);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Штатное завершение по Ctrl+C.
            }

            Console.WriteLine();
            PrintStats(wfp, etw, total, matched, withoutHostname);
        }

        if (wfp is not null)
            Console.WriteLine("Настройки сбора событий WFP восстановлены.");
        else
            Console.WriteLine("ETW-сессия остановлена.");

        return 0;
    }

    private static IHostnameSource LoadHostnameSource(CommandLine cmd, RuleEngine engine, bool sourceProvidesNames)
    {
        var hostsMapPath = cmd.Value("hosts-map");

        if (hostsMapPath is not null)
        {
            var source = StaticHostnameSource.LoadFromFile(hostsMapPath);
            Console.WriteLine($"Карта имён: {hostsMapPath} ({source.Count} записей)");
            return source;
        }

        if (engine.RequiresHostnames && !sourceProvidesNames)
        {
            Console.WriteLine(
                "Внимание: в конфиге есть доменные правила, но источник имён не задан. " +
                "Они будут отмечены как непроверяемые. Укажите --hosts-map для проверки.");
        }

        return NullHostnameSource.Instance;
    }

    private static void PrintHeader()
    {
        Console.WriteLine($"{"ВРЕМЯ",-12} {"РЕЖИМ",-7} {"PID",-7} {"ПРОЦЕСС",-20} {"НАЗНАЧЕНИЕ",-38} ПРАВИЛО");
        Console.WriteLine(new string('-', 130));
    }

    private static void PrintRow(ConnectionEvent connection, RuleDecision decision)
    {
        var time = connection.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
        var pid = connection.ProcessId?.ToString() ?? "-";
        var process = connection.ExecutableName ?? "?";

        // Имя информативнее адреса, поэтому показываем его, когда оно известно.
        var endpoint = connection.Hostname is { } host
            ? $"{host}:{connection.RemotePort}"
            : connection.DescribeEndpoint();

        var rule = decision.Rule is null
            ? decision.Reason ?? "default"
            : $"#{decision.Rule.Ordinal} {decision.Reason}";

        if (connection.Verdict == ObservedVerdict.Dropped)
            rule = "[СИСТЕМА ОТБРОСИЛА] " + rule;

        // Пометку о непроверенных доменах в строку больше не пишем: имя неизвестно
        // у подавляющего большинства соединений, и повтор в каждой строке лишь
        // мешает читать. Счётчик в статистике сообщает то же самое компактнее.
        if (decision.HadUnevaluableDomainRules)
            rule += "  ·";

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = decision.Mode switch
        {
            RoutingMode.Proxy => ConsoleColor.Cyan,
            RoutingMode.Desync => ConsoleColor.Yellow,
            _ => previous,
        };

        Console.WriteLine(
            $"{time,-12} {Format(decision.Mode),-7} {pid,-7} {Truncate(process, 20),-20} {Truncate(endpoint, 38),-38} {rule}");

        Console.ForegroundColor = previous;
    }

    private static void PrintStats(
        WfpNetEventSource? wfp,
        EtwConnectionSource? etw,
        long total,
        long matched,
        long withoutHostname = 0)
    {
        if (etw is not null)
        {
            Console.WriteLine(
                $"[статистика] показано: {total}, совпало с правилом: {matched}, " +
                $"получено из ETW: {etw.ReceivedCount}, повторов UDP подавлено: {etw.SuppressedUdpCount}, " +
                $"потеряно из-за переполнения: {etw.DroppedCount}");

            Console.WriteLine(
                $"[dns] известных адресов: {etw.DnsNames.Count}, всего записей: {etw.DnsNames.RecordedCount}, " +
                $"соединений без имени: {withoutHostname} из {total}");

            if (total > 0 && withoutHostname * 2 > total)
            {
                Console.WriteLine(
                    "Имя неизвестно у большинства соединений. Это ожидаемо: браузеры с " +
                    "собственным резолвером ходят мимо службы DNS-клиента. Окончательно " +
                    "лечится fakeip, а не наблюдением за DNS.");
            }

            return;
        }

        if (wfp is null)
            return;

        Console.WriteLine(
            $"[статистика] показано: {total}, совпало с правилом: {matched}, " +
            $"allow: {wfp.ClassifyAllowCount}, drop: {wfp.ClassifyDropCount}, " +
            $"потеряно из-за переполнения: {wfp.DroppedCount}");

        var breakdown = wfp.EventTypeBreakdown();
        if (breakdown.Count > 0)
        {
            Console.WriteLine(
                "[типы событий] " + string.Join(", ", breakdown.Select(entry => $"{entry.Type}={entry.Count}")));
        }

        if (wfp.ClassifyAllowCount == 0 && wfp.ClassifyDropCount > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                "WFP не отдаёт события о разрешённых соединениях. Это известное поведение " +
                "на данной системе — запустите без --source wfp, чтобы использовать ETW.");
            Console.WriteLine();
        }
    }

    private static string Describe(uint? value) => value switch
    {
        null => "неизвестно",
        0 => "выключено",
        _ => "включено",
    };

    private static string Format(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "proxy",
        RoutingMode.Desync => "desync",
        _ => "direct",
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}
