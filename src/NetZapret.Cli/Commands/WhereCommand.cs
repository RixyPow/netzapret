using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Отвечает на вопрос «куда пойдёт этот сервис».
/// </summary>
/// <remarks>
/// Принимает что угодно одной строкой: имя программы, домен или адрес —
/// и сама решает, чем это выглядит. Смысл в том, чтобы не заставлять
/// человека помнить, каким ключом описывается его случай.
/// </remarks>
internal static class WhereCommand
{
    public static int Run(CommandLine cmd, string defaultConfigPath)
    {
        var target = cmd.Positional.Count > 0
            ? string.Join(' ', cmd.Positional)
            : cmd.Value("target");

        if (string.IsNullOrWhiteSpace(target))
        {
            Console.Error.WriteLine("Укажите, что проверить: netzapret where telegram.exe");
            return 2;
        }

        var configPath = cmd.Value("config", defaultConfigPath);
        var engine = RuleSetLoader.LoadLayered(
            configPath,
            cmd.Value("user-rules", UserRulesFile.DefaultPath));

        var problems = RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover(cmd.Value("zapret-root"))?.Root);

        foreach (var problem in problems)
            Console.WriteLine($"Внимание: {problem}");

        var connection = Describe(target.Trim());
        var decision = engine.Evaluate(connection);

        Console.WriteLine();
        Console.WriteLine($"  {target.Trim()}  →  {Describe(decision.Mode)}");
        Console.WriteLine();
        Console.WriteLine($"  Опознано как: {DescribeInput(connection)}");
        Console.WriteLine($"  Основание:    {decision.Reason ?? "правило по умолчанию"}");

        if (decision.Rule is not null)
        {
            Console.WriteLine($"  Правило:      {decision.Rule}");
            Console.WriteLine($"  Слой:         {(decision.Rule.Source == RuleSource.User ? "ваш выбор" : "базовый набор")}");
        }

        if (decision.Mode == RoutingMode.Proxy)
            Console.WriteLine($"  Сервер:       {decision.Server ?? "авто"}");

        if (decision.HadUnevaluableDomainRules && connection.Hostname is null)
        {
            Console.WriteLine();
            Console.WriteLine("  Доменные правила пропущены: введено не доменное имя.");
        }

        PrintCaveats(connection, decision);

        PrintHowToChange(target.Trim(), decision.Mode);

        return 0;
    }

    /// <summary>
    /// Показывает, чем это можно заменить.
    /// </summary>
    /// <remarks>
    /// Нынешний режим из списка убран: он назван двумя строками выше, и
    /// предлагать его под заголовком «Изменить» значит предлагать ничего
    /// не менять. Слова взяты русские — те же, что в ответе, — чтобы
    /// подсказка и вывод не выглядели про разное.
    /// </remarks>
    private static void PrintHowToChange(string target, RoutingMode current)
    {
        var options = new (RoutingMode Mode, string Word)[]
        {
            (RoutingMode.Proxy, "vpn"),
            (RoutingMode.Desync, "десинк"),
            (RoutingMode.Direct, "напрямую"),
        };

        Console.WriteLine();
        Console.WriteLine("  Изменить:");

        foreach (var (mode, word) in options.Where(o => o.Mode != current))
            Console.WriteLine($"    nz route {target} {word}");
    }

    /// <summary>
    /// Определяет, чем является введённая строка.
    /// </summary>
    /// <remarks>
    /// Порядок проверок важен: <c>discord.com</c> и <c>discord.exe</c>
    /// различаются только расширением, а <c>1.2.3.4</c> разбирается как адрес
    /// раньше, чем как домен.
    /// </remarks>
    private static ConnectionEvent Describe(string target)
    {
        var connection = new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemotePort = 443,
            Direction = ConnectionDirection.Outbound,
        };

        if (IPAddress.TryParse(target, out var address))
            return connection with { RemoteAddress = address };

        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || target.Contains('\\'))
            return connection with { ExecutablePath = target };

        return connection with { Hostname = target.TrimStart('.').TrimEnd('.') };
    }

    private static string DescribeInput(ConnectionEvent connection)
    {
        if (connection.ExecutablePath is not null)
            return "программа";

        if (connection.RemoteAddress is not null)
            return "адрес";

        return "домен";
    }

    /// <summary>
    /// Предупреждает о том, что решение может не сработать на практике.
    /// </summary>
    private static void PrintCaveats(ConnectionEvent connection, RuleDecision decision)
    {
        if (decision.Mode != RoutingMode.Proxy)
            return;

        // Перехват решается по адресу назначения, поэтому правило по процессу
        // срабатывает только для того трафика, что вообще дошёл до туннеля.
        if (connection.ExecutablePath is not null)
        {
            Console.WriteLine();
            Console.WriteLine(
                "  Учтите: правило по программе сработает лишь для соединений,");
            Console.WriteLine(
                "  попавших в туннель. Приложениям, которые ходят по голым адресам");
            Console.WriteLine(
                "  без DNS, нужны их подсети в секции capture.");
        }
    }

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "VPN",
        RoutingMode.Desync => "десинк (Zapret)",
        _ => "напрямую",
    };
}
