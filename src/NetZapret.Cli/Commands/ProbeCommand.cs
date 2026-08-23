using NetZapret.Proxy;
using NetZapret.Subscriptions;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Проверяет, что через серверы подписки действительно идёт трафик.
/// </summary>
/// <remarks>
/// Ответ на вопрос «прокси жив?» в одну команду. Прав администратора не требует:
/// поднимается локальный инбаунд, TUN не создаётся, маршруты не трогаются.
/// </remarks>
internal static class ProbeCommand
{
    public static async Task<int> RunAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var singBox = EngineLocator.FindSingBox();

        if (singBox is null)
        {
            Console.Error.WriteLine("sing-box.exe не найден в tools/.");
            return 2;
        }

        var info = await SubscriptionSource.LoadAsync(cmd, cancellationToken);
        if (info is null)
            return 2;

        var candidates = SelectServers(info.Servers, cmd);

        if (candidates.Count == 0)
        {
            Console.Error.WriteLine("Ни один сервер не подошёл под условия отбора.");
            return 2;
        }

        var options = new ProbeOptions
        {
            ListenPort = cmd.Int("port", 21080),
            RequestTimeout = TimeSpan.FromSeconds(cmd.Int("timeout", 6)),
            TotalTimeout = TimeSpan.FromSeconds(cmd.Int("budget", 14)),
            Parallelism = cmd.Int("parallel", 5),
            LogLevel = cmd.Value("log-level", "debug"),
            KeepArtifacts = cmd.Has("keep-artifacts"),
        };

        Console.WriteLine();
        Console.Write("Внешний адрес без прокси: ");
        var directIp = await ProxyProbe.GetDirectIpAsync(options, cancellationToken);
        Console.WriteLine(directIp ?? "не определён");

        if (directIp is null)
        {
            Console.WriteLine(
                "Предупреждение: адрес без прокси не определился, сравнить будет не с чем. " +
                "Проверка всё равно покажет, проходит ли трафик.");
        }

        Console.WriteLine();
        Console.WriteLine($"Проверяю серверов: {candidates.Count}");
        Console.WriteLine();
        Console.WriteLine($"  {"РЕЗУЛЬТАТ",-12} {"ВРЕМЯ",-9} {"ВНЕШНИЙ IP",-17} СЕРВЕР");
        Console.WriteLine("  " + new string('-', 88));

        var probe = new ProxyProbe(singBox);
        var results = await probe.RunManyAsync(
            candidates,
            options,
            result => PrintRow(result, directIp),
            cancellationToken);

        var working = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();

        PrintSummary(working, failed, directIp);

        // Код возврата пригодится супервизору и скриптам: 0 — есть рабочий сервер.
        return working.Count > 0 ? 0 : 1;
    }

    private static IReadOnlyList<ProxyServer> SelectServers(IReadOnlyList<ProxyServer> servers, CommandLine cmd)
    {
        var usable = servers.Where(s => s.IsSupportedBySingBox).ToList();
        var filter = cmd.Value("server");

        if (filter is not null)
        {
            usable = usable
                .Where(s => s.Tag.Contains(filter, StringComparison.OrdinalIgnoreCase)
                         || s.Protocol.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (cmd.Has("quick"))
        {
            // Быстрый срез: по одному серверу каждого протокола. Годится, чтобы
            // отличить «сломан транспорт» от «сломан конкретный сервер», но
            // выборка может целиком состоять из неудачников — так и вышло
            // на подписке пользователя.
            return usable
                .GroupBy(s => s.Protocol)
                .Select(g => g.First())
                .ToList();
        }

        return usable;
    }

    private static void PrintRow(ProbeResult result, string? directIp)
    {
        var previous = Console.ForegroundColor;
        string verdict;

        if (!result.Success)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            verdict = "НЕ РАБОТАЕТ";
        }
        else if (directIp is not null && result.ExternalIp == directIp)
        {
            // Адрес не изменился — значит запрос ушёл мимо прокси.
            Console.ForegroundColor = ConsoleColor.Yellow;
            verdict = "УТЕЧКА";
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            verdict = "РАБОТАЕТ";
        }

        Console.WriteLine(
            $"  {verdict,-12} {result.Elapsed.TotalSeconds,6:0.0} с  {result.ExternalIp ?? "-",-17} {Truncate(result.ServerTag, 40)}");

        Console.ForegroundColor = previous;

        if (!result.Success && result.Error is not null)
            Console.WriteLine($"               {result.Error}");
    }

    private static void PrintSummary(
        IReadOnlyList<ProbeResult> working,
        IReadOnlyList<ProbeResult> failed,
        string? directIp)
    {
        Console.WriteLine();
        Console.WriteLine($"Работает: {working.Count}, не работает: {failed.Count}");

        if (working.Count > 0)
        {
            var best = working.MinBy(r => r.Elapsed)!;
            Console.WriteLine($"Быстрейший: {best.ServerTag} ({best.Elapsed.TotalSeconds:0.0} с, {best.ExternalIp})");

            if (directIp is not null)
                Console.WriteLine($"Адрес сменился: {directIp} -> {best.ExternalIp}");

            return;
        }

        Console.WriteLine();
        Console.WriteLine("Ни один сервер не отработал. Хвост лога последней попытки:");

        var last = failed.LastOrDefault();
        if (last?.LogTail.Count > 0)
        {
            foreach (var line in last.LogTail)
                Console.WriteLine($"  {line}");
        }

        if (last?.LogPath is not null)
            Console.WriteLine($"Полный лог: {Path.GetFullPath(last.LogPath)}");
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");
}

/// <summary>Ищет движки в каталоге tools/.</summary>
internal static class EngineLocator
{
    public static string? FindSingBox() => Find("sing-box.exe");

    public static string? FindXray() => Find("xray.exe");

    private static string? Find(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var tools = Path.Combine(directory.FullName, "tools");

            if (Directory.Exists(tools))
            {
                var found = Directory
                    .EnumerateFiles(tools, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found is not null)
                    return found;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
