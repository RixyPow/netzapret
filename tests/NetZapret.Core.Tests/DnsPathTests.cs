using System.Net;
using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Через что разрешается имя — на конфиге, собранном самим компилятором.
/// </summary>
/// <remarks>
/// Конфиг собирается настоящим <see cref="SingBoxConfigCompiler"/>, а не пишется
/// руками: DnsPath читает правила DNS движка, и проверять его на выдуманных
/// правилах значило бы проверять, как он понимает свою же выдумку.
/// </remarks>
public sealed class DnsPathTests
{
    private const string Rules = """
        mode: selective
        rules:
          - match: domain
            value: "*.rutracker.org"
            mode: proxy
          - match: domain
            value: "*.aternos.org"
            mode: direct
        """;

    private static readonly IReadOnlyList<string> Resolvers = ["8.8.8.8/32"];

    private static readonly Dictionary<string, List<IPAddress>> NoHosts = new(StringComparer.OrdinalIgnoreCase);

    private static JsonNode Compile(bool throughTunnel, IReadOnlyList<string>? panels = null)
    {
        var engine = RuleSetLoader.Load(Rules);
        var server = new ProxyServer
        {
            Protocol = ProxyProtocol.Vless,
            Tag = "NL",
            Host = "nl.example.com",
            Port = 443,
            Credential = "PLACEHOLDER",
            Transport = "tcp",
            Security = "tls",
            Sni = "nl.example.com",
        };

        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [server], new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = Resolvers,
            DnsThroughTunnel = throughTunnel,
            PanelHosts = panels ?? [],
        });

        return JsonNode.Parse(result.Json)!;
    }

    private static string Last(IReadOnlyList<string> steps) => steps[^1];

    [Fact]
    public void A_name_in_hosts_never_reaches_the_network()
    {
        var hosts = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["api.anthropic.com"] = [IPAddress.Parse("87.228.47.201")],
        };

        var steps = DnsPath.Explain("api.anthropic.com", hosts, Compile(true), tunnelRunning: true, Resolvers);

        Assert.Single(steps);
        Assert.StartsWith("hosts: 87.228.47.201", steps[0]);
    }

    [Fact]
    public void Without_the_tunnel_Windows_answers_itself()
    {
        var steps = DnsPath.Explain("aternos.org", NoHosts, Compile(true), tunnelRunning: false, Resolvers);

        Assert.Contains("туннель не поднят", Last(steps));
    }

    /// <summary>Имя, которое идёт в VPN, получает подставной адрес.</summary>
    [Fact]
    public void A_name_routed_to_the_tunnel_gets_a_fake_address()
    {
        var steps = DnsPath.Explain("forum.rutracker.org", NoHosts, Compile(false), tunnelRunning: true, Resolvers);

        Assert.Contains("уходит в туннель, отвечает движок", steps[0]);
        Assert.Contains("подставной адрес", Last(steps));
    }

    /// <summary>
    /// Случай 30.09: имя мимо VPN, а DNS через туннель — видно, что оно
    /// зависит от здоровья сервера.
    /// </summary>
    [Fact]
    public void A_direct_name_with_dns_through_the_tunnel_is_named_as_such()
    {
        var steps = DnsPath.Explain("aternos.org", NoHosts, Compile(true), tunnelRunning: true, Resolvers);

        Assert.Contains("через туннель", Last(steps));
        Assert.Contains("зависит от здоровья VPN-сервера", Last(steps));
    }

    [Fact]
    public void A_direct_name_with_dns_direct_goes_around_the_tunnel()
    {
        var steps = DnsPath.Explain("aternos.org", NoHosts, Compile(false), tunnelRunning: true, Resolvers);

        Assert.Contains("напрямую, мимо туннеля", Last(steps));
    }

    [Fact]
    public void A_subscription_panel_resolves_around_the_tunnel()
    {
        var steps = DnsPath.Explain("panel.example.net", NoHosts, Compile(true, ["panel.example.net"]), tunnelRunning: true, Resolvers);

        Assert.Contains("резолвер Windows, мимо туннеля", Last(steps));
    }

    /// <summary>Резолвер, которого нет в перехвате, движок не видит вовсе.</summary>
    [Fact]
    public void A_resolver_outside_the_capture_bypasses_the_engine()
    {
        var steps = DnsPath.Explain("aternos.org", NoHosts, Compile(true), tunnelRunning: true, ["77.88.8.8/32"]);

        Assert.Single(steps);
        Assert.Contains("не в перехвате туннеля", steps[0]);
    }

    /// <summary>Суффикс — по границе метки: «notrutracker.org» не «rutracker.org».</summary>
    [Fact]
    public void A_suffix_matches_on_a_label_boundary_only()
    {
        var steps = DnsPath.Explain("notrutracker.org", NoHosts, Compile(false), tunnelRunning: true, Resolvers);

        Assert.DoesNotContain("подставной адрес", Last(steps));
    }
}
