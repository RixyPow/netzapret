using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Имена «через VPN» должны получать подставной адрес, иначе идут мимо туннеля
/// (обсуждение №55, 05.11: YogaDNS отдал 2ip.io настоящий адрес).
/// </summary>
public sealed class TunnelNameCheckTests
{
    private static readonly string[] Fake = ["198.18.0.0/15", "fc00::/18"];

    private static RoutingRule Domain(string value, RoutingMode mode) =>
        new() { Match = MatchKind.Domain, Value = value, Mode = mode, Source = RuleSource.User };

    private static RoutingRule List(string value, RoutingMode mode, params string[] names)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = value, Mode = mode };
        rule.LoadHostList(names);
        return rule;
    }

    [Theory]
    [InlineData("*.2ip.io", "2ip.io")]
    [InlineData(".chatgpt.com", "chatgpt.com")]
    [InlineData("Claude.AI.", "claude.ai")]
    [InlineData("*google*", null)]
    [InlineData("localhost", null)]
    public void A_pattern_becomes_a_plain_name_or_nothing(string value, string? expected) =>
        Assert.Equal(expected, TunnelNameCheck.Plain(value));

    [Fact]
    public void Candidates_are_names_the_rules_really_send_into_the_vpn()
    {
        var engine = RuleEngine.Build(
            [
                Domain("*.direct.example.com", RoutingMode.Direct),
                Domain("*.2ip.io", RoutingMode.Proxy),
                List("config/lists/openai.txt", RoutingMode.Proxy, "pinned.example", "direct.example.com", "chatgpt.com"),
                Domain("*.youtube.com", RoutingMode.Desync),
            ],
            RoutingMode.Desync);

        var names = TunnelNameCheck.Candidates(engine, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pinned.example" });

        Assert.Equal(["2ip.io", "chatgpt.com"], names);
    }

    [Fact]
    public void A_fake_address_passes_and_a_real_one_fails()
    {
        var fake = TunnelNameCheck.Judge("chatgpt.com", [IPAddress.Parse("198.18.0.100"), IPAddress.Parse("fc00::64")], Fake);
        var real = TunnelNameCheck.Judge("2ip.io", [IPAddress.Parse("188.40.167.81")], Fake);

        Assert.Equal(TunnelNameKind.Fake, fake.Kind);
        Assert.Equal(TunnelNameKind.Real, real.Kind);
        Assert.Equal([IPAddress.Parse("188.40.167.81")], real.Real);
    }

    /// <summary>Без IPv6 у TUN подставного IPv6 нет, и настоящий AAAA ничего не говорит.</summary>
    [Fact]
    public void A_real_ipv6_address_is_ignored_without_a_fake_ipv6_range()
    {
        var verdict = TunnelNameCheck.Judge("chatgpt.com",
            [IPAddress.Parse("198.18.0.100"), IPAddress.Parse("2606:4700::6812:2030")], ["198.18.0.0/15"]);

        Assert.Equal(TunnelNameKind.Fake, verdict.Kind);
    }

    [Fact]
    public async Task The_check_goes_on_to_the_next_name_when_one_does_not_resolve()
    {
        Task<IPAddress[]> Resolve(string name, CancellationToken token) => name == "gone.example"
            ? Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException(11001))
            : Task.FromResult(new[] { IPAddress.Parse("188.40.167.81") });

        var verdict = await TunnelNameCheck.CheckAsync(["gone.example", "2ip.io"], Fake, CancellationToken.None, Resolve);

        Assert.NotNull(verdict);
        Assert.Equal(TunnelNameKind.Real, verdict.Kind);
        Assert.Equal("2ip.io", verdict.Name);
    }

    /// <summary>Сеть сменилась после запуска: резолвер нового роутера вне TUN.</summary>
    [Fact]
    public void A_resolver_outside_the_tun_is_named()
    {
        var outside = TunnelNameCheck.Outside(
            ["192.168.0.1/32", "192.168.31.1/32", "fe80::1/128"],
            ["198.18.0.0/15", "192.168.0.1/32", "fe80::1/128"]);

        Assert.Equal(["192.168.31.1/32"], outside);
        Assert.True(TunnelNameCheck.IsLinkLocal("fe80::1/128"));
        Assert.False(TunnelNameCheck.IsLinkLocal("fd7d:8082:e90f::1/128"));
    }

    [Fact]
    public void The_tun_networks_are_read_from_the_config()
    {
        var path = Path.GetTempFileName();

        try
        {
            File.WriteAllText(path, """
                {"inbounds":[{"type":"tun","route_address":["198.18.0.0/15","8.8.8.8/32"],"route_exclude_address":["104.16.0.0/13"]}]}
                """);

            var (routes, excluded) = TunnelEndpoints.TunRoutes(path);

            Assert.Equal(["198.18.0.0/15", "8.8.8.8/32"], routes);
            Assert.Equal(["104.16.0.0/13"], excluded);

            File.WriteAllText(path, """{"inbounds":[{"type":"tun"}]}""");

            Assert.Null(TunnelEndpoints.TunRoutes(path).Routes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static ConnectionEvent Connection(string? host, string address) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Protocol = ProtocolKind.Tcp,
        RemoteAddress = IPAddress.Parse(address),
        RemotePort = 443,
        ExecutablePath = @"C:\Program Files\Browser\browser.exe",
        Hostname = host,
    };

    private static RuleDecision Vpn()
    {
        var engine = RuleEngine.Build([Domain("*.2ip.io", RoutingMode.Proxy)], RoutingMode.Desync);
        return engine.Evaluate(Connection("2ip.io", "188.40.167.81"));
    }

    /// <summary>Как на снимке №55: правило «через VPN», а адрес настоящий и в TUN не попадает.</summary>
    [Fact]
    public void Watch_marks_a_vpn_name_with_a_real_address_as_past_the_tunnel()
    {
        var context = new WatchContext(Fake, [], ["198.18.0.0/15", "fc00::/18", "149.154.160.0/20"]);

        var entry = WatchEntry.From(Connection("2ip.io", "188.40.167.81"), Vpn(), context);

        Assert.Equal(WatchRoute.Direct, entry.Mode);
        Assert.True(entry.Bypass);
        Assert.True(entry.Certain);
        Assert.StartsWith("мимо туннеля · #", entry.RuleName);

        var back = WatchEntry.Parse(entry.ToLine());

        Assert.NotNull(back);
        Assert.True(back.Bypass);
    }

    [Fact]
    public void Watch_keeps_tunnel_for_addresses_the_tun_takes_and_without_knowing_the_tun()
    {
        var inside = new WatchContext(Fake, [], ["198.18.0.0/15", "188.40.0.0/16"]);
        var unknown = new WatchContext(Fake, []);

        Assert.Equal(WatchRoute.Proxy, WatchEntry.From(Connection("2ip.io", "188.40.167.81"), Vpn(), inside).Mode);
        Assert.Equal(WatchRoute.Proxy, WatchEntry.From(Connection("2ip.io", "188.40.167.81"), Vpn(), unknown).Mode);
    }

    [Fact]
    public void Watch_marks_an_address_taken_out_of_the_tun()
    {
        var context = new WatchContext(Fake, [], ["188.40.0.0/16"], ["188.40.167.0/24"]);

        Assert.True(WatchEntry.From(Connection("2ip.io", "188.40.167.81"), Vpn(), context).Bypass);
    }
}
