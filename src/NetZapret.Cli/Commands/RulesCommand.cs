using NetZapret.Core.Rules;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Показывает набор правил в том порядке, в котором его действительно
/// вычисляет движок — после сортировки по приоритету класса.
/// </summary>
internal static class RulesCommand
{
    public static int Run(CommandLine cmd, string defaultConfigPath)
    {
        var configPath = cmd.Value("config", defaultConfigPath);
        // Оба слоя, как и везде: показывать базовый набор в отрыве от выбора
        // человека значит показывать не то, что применяется. Команды разошлись
        // в этом незаметно — where и config читали два слоя, rules один.
        var engine = RuleSetLoader.LoadLayered(
            configPath,
            cmd.Value("user-rules", UserRulesFile.DefaultPath));
        var ruleSet = engine.RuleSet;

        Console.WriteLine($"Конфиг: {configPath}");
        Console.WriteLine($"Режим работы: {DescribeOperating(ruleSet.Operating)}");
        Console.WriteLine($"Правил: {ruleSet.Rules.Count}");

        if (ruleSet.Operating != OperatingMode.Selective)
        {
            Console.WriteLine();
            Console.WriteLine("В этом режиме правила не вычисляются — они показаны для справки.");
        }

        Console.WriteLine();
        Console.WriteLine("Порядок вычисления (process → domain → ip → default):");
        Console.WriteLine();
        Console.WriteLine($"  {"#",-4} {"ТИП",-9} {"ЗНАЧЕНИЕ",-32} {"РЕЖИМ",-8} СЕРВЕР");
        Console.WriteLine("  " + new string('-', 76));

        foreach (var rule in ruleSet.Rules)
        {
            var kind = rule.Match.ToString().ToLowerInvariant();
            var mode = rule.Mode.ToString().ToLowerInvariant();
            var server = rule.Mode == RoutingMode.Proxy ? rule.Server ?? ruleSet.DefaultServer ?? "auto" : "-";

            Console.WriteLine($"  {rule.Ordinal,-4} {kind,-9} {Truncate(rule.Value, 32),-32} {mode,-8} {server}");
        }

        Console.WriteLine("  " + new string('-', 76));
        Console.WriteLine($"  {"—",-4} {"default",-9} {"*",-32} {ruleSet.DefaultMode.ToString().ToLowerInvariant(),-8} {ruleSet.DefaultServer ?? "-"}");

        if (engine.RequiresHostnames)
        {
            Console.WriteLine();
            Console.WriteLine("Доменные правила требуют источника имён (fakeip либо --hosts-map).");
        }

        return 0;
    }

    private static string DescribeOperating(OperatingMode mode) => mode switch
    {
        OperatingMode.Off => "off — всё напрямую, ничего не применяется",
        OperatingMode.ProxyAll => "proxy — весь трафик через VPN",
        _ => "selective — по умолчанию десинк, прокси точечно по правилам",
    };

    /// <summary>
    /// Обрезает по левому краю: у длинных путей значимая часть — хвост с именем файла.
    /// </summary>
    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat("…", value.AsSpan(value.Length - max + 1));
}
