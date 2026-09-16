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
            // Колонка осталась, а выбора в ней больше нет: со сборкой extended
            // sing-box обслуживает всё, включая xhttp. Убирать её целиком —
            // отдельная правка ширины таблицы, и она того не стоит.
            var engine = "sing-box";
            var previous = Console.ForegroundColor;

            Console.WriteLine(
                $"  {server.Protocol.ToString().ToLowerInvariant(),-11} {server.Transport,-11} " +
                $"{server.Security,-8} {server.Port,-6} {engine,-11} {server.Tag}");

            Console.ForegroundColor = previous;
        }

        // Разделители называются своим именем, а не «неподдерживаемыми».
        // Прежде здесь стояло «сервер(ов) с транспортом xhttp — sing-box его
        // не реализует»: неправда дважды, потому что extended его как раз
        // разбирает (замер 2026-09-11), и потому что пропускаются вовсе
        // не из-за транспорта.
        var separators = info.Servers.Where(s => !s.HasDialableAddress).ToList();

        if (separators.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine(
                $"{separators.Count} запис(ей) без адреса — это разделители списка, "
                + "а не серверы: поставщик кладёт их, чтобы клиент нарисовал заголовки групп.");

            foreach (var separator in separators)
                Console.WriteLine($"  {separator.Tag}");

            Console.WriteLine("Они пропускаются: попав в автоподбор, такая запись обрывает "
                + "всякое соединение мгновенно.");
        }

        return 0;
    }
}
