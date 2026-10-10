using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>
/// Куда пойдёт имя, программа или адрес и почему — строками: правило,
/// выключатели, десинк, DNS.
/// </summary>
/// <remarks>
/// Было в <c>nz where</c> и только там. 10.10 понадобилось и окну — пункт
/// «Почему так» в меню строки «Наблюдения» (макет владельца), — и решение
/// переехало сюда, чтобы окно и nz отвечали одними словами (CLAUDE.md:
/// логика — в библиотеках).
/// </remarks>
public static class RouteWhy
{
    /// <summary>Строки объяснения; первая — сама цель.</summary>
    public static IReadOnlyList<string> Explain(string target, AppSettings? settings = null)
    {
        target = target.Trim();
        settings ??= AppSettings.Load(AppSettings.DefaultPath);

        var (engine, _) = NetZapret.Zapret.RuleSetExpander.LoadFor(settings);
        var connection = ConnectionEvent.Describe(target);
        var decision = engine.Evaluate(connection);
        var engines = settings.Engines;
        bool tunnelUp = settings.NeedsProxy;

        var lines = new List<string>
        {
            $"в правилах:       {Word(decision.Mode)}"
                + (decision.Rule is null ? "  (по умолчанию)" : $"  ({decision.Rule})"),
            $"при выключателях: {Word(engines.Effective(decision.Mode, tunnelUp))}  ({engines.Describe()})",
        };

        if (connection.Hostname is { } host && settings.NeedsDesync)
        {
            var exclusions = HostsFile.DescribeDesyncExclusions(engine.RuleSet, tunnelUp: tunnelUp);
            var bypass = HostsFile.BypassFor(exclusions, host, HostsFile.CollectShieldHoles(engine.RuleSet, exclusions));

            if (bypass != DesyncBypass.None)
                lines.Add($"десинк:           {HostsFile.DescribeBypass(bypass)}");
        }

        // Через что разрешается имя — сейчас, по hosts и конфигу работающего
        // движка (DnsPath). 30.09 имена мимо VPN разрешались через туннель,
        // и при заминке сервера пропадали у всей машины.
        if (connection.Hostname is { } name)
        {
            var steps = DnsPath.Explain(
                name,
                HostsFile.Read(),
                EngineConfig(),
                SupervisorState.Load(SupervisorState.DefaultPath)?.EngineAnswersDns() == true,
                SystemResolvers.Discover());

            for (int i = 0; i < steps.Count; i++)
                lines.Add((i == 0 ? "DNS:              " : "                  ") + steps[i]);
        }

        return lines;
    }

    /// <summary>Конфиг работающего движка; не прочитался — <c>null</c>.</summary>
    public static System.Text.Json.Nodes.JsonNode? EngineConfig()
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(EngineKeys.DefaultConfigPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Word(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "VPN",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };
}
