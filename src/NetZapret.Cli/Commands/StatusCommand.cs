namespace NetZapret.Cli.Commands;

/// <summary>
/// Состояние подключения к прокси-серверу.
/// </summary>
internal static class StatusCommand
{
    public static int Run()
    {
        Console.WriteLine("Прокси-движок ещё не подключён: модуль NetZapret.Proxy пока содержит только контракты.");
        Console.WriteLine("Команда начнёт отдавать данные, когда появится оркестратор sing-box и его Clash API.");
        Console.WriteLine();
        Console.WriteLine("Сейчас доступны: netzapret rules, netzapret test, netzapret watch.");

        return 0;
    }
}
