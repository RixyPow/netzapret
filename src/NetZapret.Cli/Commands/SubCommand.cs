using NetZapret.Subscriptions;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Показывает содержимое подписки: квоту, срок и список серверов.
/// </summary>
internal static class SubCommand
{
    public static async Task<int> RunAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var info = await SubscriptionSource.LoadAsync(cmd, cancellationToken);
        if (info is null)
            return 2;

        SubscriptionSource.PrintSummary(info);

        if (info.Servers.Count == 0)
            return 0;

        Console.WriteLine();
        Console.WriteLine($"  {"ПРОТОКОЛ",-11} {"ТРАНСПОРТ",-11} {"TLS",-8} {"ПОРТ",-6} {"ДВИЖОК",-11} НАЗВАНИЕ");
        Console.WriteLine("  " + new string('-', 92));

        foreach (var server in info.Servers)
        {
            var engine = server.IsSupportedBySingBox ? "sing-box" : "только Xray";
            var previous = Console.ForegroundColor;

            if (!server.IsSupportedBySingBox)
                Console.ForegroundColor = ConsoleColor.DarkGray;

            Console.WriteLine(
                $"  {server.Protocol.ToString().ToLowerInvariant(),-11} {server.Transport,-11} " +
                $"{server.Security,-8} {server.Port,-6} {engine,-11} {server.Tag}");

            Console.ForegroundColor = previous;
        }

        var unsupported = info.Servers.Count(s => !s.IsSupportedBySingBox);
        if (unsupported > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{unsupported} сервер(ов) с транспортом xhttp — sing-box его не реализует, " +
                "они будут пропущены при генерации конфига.");
        }

        return 0;
    }
}
