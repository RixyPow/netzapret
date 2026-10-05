using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Движок туннеля без выхода — только ради DNS при одном десинке (03.10).
/// </summary>
/// <remarks>
/// Замер 03.10: открытый DNS у владельца подменяется, и без туннеля YouTube
/// мог не разрешиться вовсе. Владелец: пробовать, если DNS стоит «через туннель».
/// </remarks>
public sealed class DnsEngineTests
{
    private static readonly IReadOnlyList<string> Resolvers = ["8.8.8.8/32", "192.168.1.1/32"];

    private static AppSettings DesyncOnly(bool throughTunnel) =>
        new AppSettings { PresetName = "Universal V10", DnsVia = throughTunnel ? DnsRoute.Tunnel : DnsRoute.Direct }
            .With(new EngineChoice { Desync = true, Tunnel = false });

    [Fact]
    public void Desync_alone_with_DNS_through_the_tunnel_raises_the_DNS_engine()
    {
        var settings = DesyncOnly(throughTunnel: true);

        Assert.True(settings.NeedsDnsEngine);
        Assert.False(settings.NeedsProxy);
    }

    [Fact]
    public void Direct_DNS_keeps_Windows_answering_as_before()
    {
        Assert.False(DesyncOnly(throughTunnel: false).NeedsDnsEngine);
    }

    [Fact]
    public void With_the_tunnel_up_its_own_engine_answers_DNS()
    {
        var settings = new AppSettings
        {
            SubscriptionUrl = "https://example.invalid/sub",
            PresetName = "Universal V10",
            DnsVia = DnsRoute.Tunnel,
        }.With(new EngineChoice { Desync = true, Tunnel = true });

        Assert.True(settings.NeedsProxy);
        Assert.False(settings.NeedsDnsEngine);
    }

    [Fact]
    public void Without_desync_there_is_nothing_to_guard()
    {
        var settings = new AppSettings { PresetName = null, DnsVia = DnsRoute.Tunnel }
            .With(new EngineChoice { Desync = true, Tunnel = false });

        Assert.False(settings.NeedsDnsEngine);
    }

    /// <summary>
    /// Выборочный без выхода — это тот же один десинк, и DNS через движок там нужен так же.
    /// </summary>
    [Fact]
    public void Selective_without_an_exit_is_desync_alone_too()
    {
        var settings = new AppSettings { SubscriptionUrl = null, PresetName = "Universal V10", DnsVia = DnsRoute.Tunnel }
            .With(new EngineChoice { Desync = true, Tunnel = true });

        Assert.False(settings.NeedsProxy);
        Assert.True(settings.NeedsDnsEngine);
    }

    private static JsonNode Compile() =>
        JsonNode.Parse(DnsEngine.Compile(
            DesyncOnly(throughTunnel: true),
            Resolvers,
            new Dictionary<string, string>(),
            EngineKeys.Generate()).Json)!;

    [Fact]
    public void Only_the_resolvers_and_fake_range_enter_the_tun()
    {
        var tun = Compile()["inbounds"]!.AsArray().Single(i => (string?)i!["type"] == "tun")!;
        var captured = tun["route_address"]!.AsArray().Select(n => (string?)n).ToList();

        Assert.Contains("8.8.8.8/32", captured);
        Assert.Contains("192.168.1.1/32", captured);
        Assert.Equal(Resolvers.Count + 2, captured.Count);
    }

    [Fact]
    public void There_is_no_exit_only_direct()
    {
        var config = Compile();
        var outbounds = config["outbounds"]!.AsArray().Select(o => (string?)o!["type"]).ToList();

        Assert.Equal(["direct"], outbounds);
        Assert.Equal("direct", (string?)config["route"]!["final"]);
        Assert.DoesNotContain(config["inbounds"]!.AsArray(), i => (string?)i!["tag"] == "health-in");
    }

    [Fact]
    public void Nobody_gets_a_fake_address_and_DoH_goes_direct()
    {
        var dns = Compile()["dns"]!;

        Assert.DoesNotContain(dns["rules"]!.AsArray(), r => (string?)r!["server"] == "fake");
        Assert.Equal("remote", (string?)dns["final"]);

        var remote = dns["servers"]!.AsArray().Single(s => (string?)s!["tag"] == "remote")!;
        Assert.Equal("https", (string?)remote["type"]);
        Assert.Null(remote["detour"]);
    }

    [Fact]
    public void Windows_DoH_to_its_resolvers_is_turned_back_to_plain_DNS()
    {
        var rules = Compile()["route"]!["rules"]!.AsArray();

        Assert.Contains(rules, r => (string?)r!["action"] == "hijack-dns");
        Assert.Contains(rules, r => (string?)r!["action"] == "reject"
            && r["ip_cidr"]!.AsArray().Any(a => (string?)a == "8.8.8.8/32"));
    }

    [Fact]
    public void The_engine_accepts_the_config()
    {
        var json = DnsEngine.Compile(DesyncOnly(throughTunnel: true), Resolvers, new Dictionary<string, string>(), EngineKeys.Generate()).Json;
        var (ok, said) = SingBoxCheck.Run(json);

        Assert.True(ok, said);
    }

    [Fact]
    public void The_DNS_engine_is_not_called_sing_box()
    {
        // По «sing-box» окно узнаёт туннель — движок ради DNS туннелем не назовётся.
        Assert.Equal(DnsEngine.ServiceName, new SingBoxService("sing-box.exe", "x.json", dnsOnly: true).Name);
        Assert.Equal("sing-box", new SingBoxService("sing-box.exe", "x.json").Name);
    }

    private static SupervisorState StateWith(params string[] services) => new()
    {
        SupervisorProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.Now,
        Services = services.Select(name => new ServiceState { Name = name, Health = ServiceHealth.Healthy }).ToList(),
    };

    [Fact]
    public void Either_engine_answers_DNS_desync_alone_does_not()
    {
        Assert.True(StateWith("winws2", DnsEngine.ServiceName).EngineAnswersDns());
        Assert.True(StateWith("winws2", "sing-box").EngineAnswersDns());
        Assert.False(StateWith("winws2").EngineAnswersDns());
    }
}
