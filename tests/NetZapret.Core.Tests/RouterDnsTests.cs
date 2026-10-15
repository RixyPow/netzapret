using System.Net;
using System.Text.Json.Nodes;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;
namespace NetZapret.Core.Tests;

public sealed class RouterDnsTests
{
    [Theory]
    [InlineData("192.168.1.1", "192.168.1.1")]
    [InlineData(" 10.0.0.1 ", "10.0.0.1")]
    [InlineData("172.16.0.1", "172.16.0.1")]
    [InlineData("172.31.255.254", "172.31.255.254")]
    public void LanAddressesAccepted(string value, string expected)
    { Assert.True(RouterDnsSettings.TryNormalize(value, out var result)); Assert.Equal(expected, result); }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("1.1.1.1")] [InlineData("127.0.0.1")]
    [InlineData("169.254.1.1")] [InlineData("172.32.0.1")] [InlineData("172.15.0.1")]
    [InlineData("192.168.1.1:53")] [InlineData("https://192.168.1.1")]
    [InlineData("router.lan")] [InlineData("::1")] [InlineData("224.0.0.1")]
    [InlineData("192.168.1.999")] [InlineData("10.1")] [InlineData("0x0a000001")]
    public void UnsafeOrUnsupportedRejected(string? value) => Assert.False(RouterDnsSettings.TryNormalize(value, out _));
    [Fact] public void DefaultsAreOptIn()
    { var s = new AppSettings(); Assert.False(s.UseRouterDns); Assert.Equal("192.168.1.1", s.RouterDnsAddress); }
    [Fact] public void SelectKeepsDohChoiceSoTurningOffReturnsIt()
    {
        var before = new AppSettings { DnsAuto = true, DnsVia = DnsRoute.Tunnel, DnsServer = "1.1.1.1" };
        var s = RouterDnsSettings.Select(before, "192.168.1.1");
        Assert.True(s.UseRouterDns); Assert.True(s.DnsAuto); Assert.Equal(DnsRoute.Tunnel, s.DnsVia);
        Assert.Equal("1.1.1.1", s.DnsServer);
        Assert.Equal(before, s with { UseRouterDns = false });
    }
    [Fact] public void InvalidSelectionThrows() => Assert.Throws<ArgumentException>(() => RouterDnsSettings.Select(new(), "127.0.0.1"));
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.1", "255.255.255.0", true)]
    [InlineData("192.168.2.20", "192.168.1.1", "255.255.255.0", false)]
    [InlineData("10.1.2.3", "10.0.0.1", "255.0.0.0", true)]
    [InlineData("::1", "192.168.1.1", "255.255.255.0", false)]
    public void SubnetMatching(string local, string remote, string mask, bool expected) =>
        Assert.Equal(expected, RouterDnsConnection.SameSubnet(IPAddress.Parse(local), IPAddress.Parse(remote), IPAddress.Parse(mask)));
    [Fact] public void ProbeChecksIdAndResponseFlag()
    {
        byte[] q = [1, 2]; byte[] response = [1, 2, 0x80, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.True(RouterDnsConnection.IsAnswer(q, response));
        response[0] = 9; Assert.False(RouterDnsConnection.IsAnswer(q, response));
        response[0] = 1; response[2] = 0; Assert.False(RouterDnsConnection.IsAnswer(q, response));
        Assert.False(RouterDnsConnection.IsAnswer(q, [1, 2]));
    }

    private static ProxyServer Server() => new()
    { Protocol = ProxyProtocol.Hysteria2, Tag = "Test", Host = "proxy.example", Port = 443, Credential = "TEST", Transport = "udp", Security = "tls" };
    private static SingBoxOptions Options(TunnelScope scope = TunnelScope.ProxyOnly) => new()
    { RouterDns = true, DnsServer = "192.168.1.1", DnsServerType = "udp", RouterDnsInterface = "Ethernet",
        Scope = scope, DnsThroughTunnel = true, DnsViaAuto = true, DnsServerAddresses = ["192.168.1.1/32"] };
    private static JsonNode Compile(string yaml, SingBoxOptions? options = null, bool server = true)
    {
        var loaded = RuleSetLoader.Load(yaml);
        return JsonNode.Parse(new SingBoxConfigCompiler().Compile(loaded.RuleSet, server ? [Server()] : [], options ?? Options()).Json)!;
    }
    private const string Rules = """
        mode: selective
        rules:
          - match: domain
            value: "chatgpt.com"
            mode: direct
          - match: domain
            value: "*.youtube.com"
            mode: desync
          - match: domain
            value: "*.discord.gg"
            mode: proxy
        """;
    [Fact] public void RouterUdpIsBoundToLanAndNeverDetoured()
    {
        var json = Compile(Rules); var remote = json["dns"]!["servers"]!.AsArray().Single(n => (string?)n!["tag"] == "remote")!;
        Assert.Equal("udp", (string?)remote["type"]); Assert.Equal("192.168.1.1", (string?)remote["server"]);
        Assert.Equal(53, (int?)remote["server_port"]); Assert.Equal("Ethernet", (string?)remote["bind_interface"]);
        Assert.Null(remote["detour"]); Assert.Null(remote["tls"]); Assert.Null(remote["path"]);
        Assert.DoesNotContain(json["dns"]!["servers"]!.AsArray(), n => (string?)n!["tag"] == "bootstrap");
        Assert.Equal("remote", (string?)json["route"]!["default_domain_resolver"]!["server"]);
    }
    [Fact] public void DirectAndDesyncUseRealDnsWhileProxyKeepsFakeIp()
    {
        var rules = Compile(Rules)["dns"]!["rules"]!.AsArray();
        Assert.Single(rules);
        Assert.Equal("fake", (string?)rules[0]!["server"]);
        Assert.Equal("discord.gg", (string?)rules[0]!["domain_suffix"]![0]);
        Assert.Equal("remote", (string?)Compile(Rules)["dns"]!["final"]);
    }
    [Fact] public void NoProxyExitMeansNoFakeIpRules()
    { Assert.DoesNotContain(Compile(Rules, server: false)["dns"]!["rules"]!.AsArray(), n => (string?)n!["server"] == "fake"); }
    [Fact] public void DirectExceptionsPrecedeFullTunnelFakeIp()
    {
        var rules = Compile(Rules.Replace("mode: selective", "mode: proxy"), Options(TunnelScope.Everything))["dns"]!["rules"]!.AsArray();
        Assert.Equal("remote", (string?)rules[0]!["server"]); Assert.Equal("chatgpt.com", (string?)rules[0]!["domain"]![0]);
        Assert.Equal("fake", (string?)rules.Last()!["server"]);
    }
    [Fact] public void NarrowTunStillCapturesWindowsResolverForProxyDomainRouting()
    {
        var tun = Compile(Rules)["inbounds"]!.AsArray().Single(n => (string?)n!["type"] == "tun")!;
        Assert.Contains(tun["route_address"]!.AsArray(), n => (string?)n == "192.168.1.1/32");
    }
    [Fact] public void MissingLanBindingRejected() => Assert.Throws<ArgumentException>(() => Compile(Rules, new()
        { RouterDns = true, DnsServer = "192.168.1.1", DnsServerType = "udp" }));
    [Fact] public void DohPretendingToBeRouterRejected() => Assert.Throws<ArgumentException>(() => Compile(Rules, new()
        { RouterDns = true, DnsServer = "192.168.1.1", RouterDnsInterface = "Ethernet" }));
    [Fact] public void DnsOnlyModeUsesRouterWithoutProxy()
    {
        var settings = RouterDnsSettings.Select(new AppSettings { PresetName = "Test" }.With(new EngineChoice { Desync = true, Tunnel = false }), "192.168.1.1");
        Assert.True(settings.NeedsDnsEngine);
        var json = JsonNode.Parse(DnsEngine.Compile(settings, ["192.168.1.1/32"], new Dictionary<string, string>(), EngineKeys.Generate(), "Ethernet").Json)!;
        Assert.DoesNotContain(json["dns"]!["rules"]!.AsArray(), n => (string?)n!["server"] == "fake");
        Assert.Equal("udp", (string?)json["dns"]!["servers"]![0]!["type"]);
    }
    [Fact] public void ExplanationMatchesMode() => Assert.Contains("UDP", DnsRoutes.Explain(RouterDnsSettings.Select(new(), "192.168.1.1")));
}
