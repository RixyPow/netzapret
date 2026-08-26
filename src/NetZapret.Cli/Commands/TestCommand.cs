using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Прогоняет придуманное соединение через правила и показывает решение.
/// </summary>
/// <remarks>
/// Быстрый способ проверить конфиг, не дожидаясь реального трафика и не поднимая
/// WFP: права администратора здесь не нужны.
/// </remarks>
internal static class TestCommand
{
    public static int Run(CommandLine cmd, string defaultConfigPath)
    {
        var configPath = cmd.Value("config", defaultConfigPath);
        var engine = RuleSetLoader.LoadLayered(
            configPath,
            cmd.Value("user-rules", UserRulesFile.DefaultPath));

        var process = cmd.Value("process");
        var domain = cmd.Value("domain");
        var ipText = cmd.Value("ip");
        var port = (ushort)cmd.Int("port", 443);

        if (process is null && domain is null && ipText is null)
        {
            Console.Error.WriteLine("Задайте хотя бы одно из: --process, --domain, --ip.");
            return 2;
        }

        IPAddress? address = null;
        if (ipText is not null && !IPAddress.TryParse(ipText, out address))
        {
            Console.Error.WriteLine($"'{ipText}' не разбирается как IP-адрес.");
            return 2;
        }

        var connection = new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemoteAddress = address,
            RemotePort = port,
            ExecutablePath = process,
            Hostname = domain,
            Direction = ConnectionDirection.Outbound,
        };

        var decision = engine.Evaluate(connection);

        Console.WriteLine($"Конфиг:     {configPath}");
        Console.WriteLine($"Процесс:    {process ?? "—"}");
        Console.WriteLine($"Домен:      {domain ?? "—"}");
        Console.WriteLine($"Назначение: {(address is null ? "—" : connection.DescribeEndpoint())}");
        Console.WriteLine();
        Console.WriteLine($"РЕШЕНИЕ:    {decision.Mode.ToString().ToLowerInvariant()}");
        Console.WriteLine($"Основание:  {decision.Reason ?? "default"}");

        if (decision.Rule is not null)
            Console.WriteLine($"Правило:    {decision.Rule}");

        if (decision.Mode == RoutingMode.Proxy)
            Console.WriteLine($"Сервер:     {decision.Server ?? "auto"}");

        if (decision.HadUnevaluableDomainRules)
        {
            Console.WriteLine();
            Console.WriteLine("Часть доменных правил пропущена: домен не задан (--domain).");
        }

        return 0;
    }
}
