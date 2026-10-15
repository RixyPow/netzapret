using System.Net;
using System.Text.Json.Nodes;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;
namespace NetZapret.Core.Tests;
public sealed class RouterDnsRevisionTests
{
    private static JsonNode Compile(params RoutingRule[] rules) => JsonNode.Parse(new SingBoxConfigCompiler().Compile(
        new RuleSet { Rules = rules, DefaultMode = RoutingMode.Direct, Operating = OperatingMode.Selective },
        [new ProxyServer { Protocol = ProxyProtocol.Hysteria2, Tag = "test", Host = "proxy.example", Port = 443,
            Credential = "TEST", Transport = "udp", Security = "tls" }],
        new SingBoxOptions { Scope = TunnelScope.ProxyOnly, RouterDns = true, DnsServerType = "udp",
            DnsServer = "192.168.1.1", RouterDnsInterface = "Ethernet" }).Json)!;
    private static RoutingRule Domain(string name, RoutingMode mode) => new() { Match = MatchKind.Domain, Value = name, Mode = mode };
    [Fact] public void LargeUnrelatedRegistryDoesNotEnterDnsRules()
    {
        var registry = new RoutingRule { Match = MatchKind.HostList, Value = "registry", Mode = RoutingMode.Desync };
        registry.LoadHostList(Enumerable.Range(0, 117000).Select(i => $"blocked{i}.invalid").ToArray());
        var dns = Compile(registry, Domain("*.discord.gg", RoutingMode.Proxy))["dns"]!;
        Assert.Single(dns["rules"]!.AsArray());
        Assert.True(dns.ToJsonString().Length < 2000);
    }
    [Theory]
    [InlineData("chat.example.com", "*.example.com")]
    [InlineData("*.example.com", "chat.example.com")]
    [InlineData("*.chat.example.com", "*.example.com")]
    [InlineData("*.example.com", "*.chat.example.com")]
    public void EarlierDirectExceptionIsKeptWhenOverlappingLaterProxy(string direct, string proxy)
    {
        var rules = Compile(Domain(direct, RoutingMode.Direct), Domain(proxy, RoutingMode.Proxy))["dns"]!["rules"]!.AsArray();
        Assert.Equal(2, rules.Count); Assert.Equal("remote", (string?)rules[0]!["server"]); Assert.Equal("fake", (string?)rules[1]!["server"]);
    }
    [Fact] public void LaterDirectRuleDoesNotOverrideEarlierProxy()
    {
        var rules = Compile(Domain("*.example.com", RoutingMode.Proxy), Domain("chat.example.com", RoutingMode.Direct))["dns"]!["rules"]!.AsArray();
        Assert.Single(rules); Assert.Equal("fake", (string?)rules[0]!["server"]);
    }
    [Fact] public void HostListContainsOnlyOverlappingExceptions()
    {
        var list = new RoutingRule { Match = MatchKind.HostList, Value = "exceptions", Mode = RoutingMode.Direct };
        list.LoadHostList(["unrelated.invalid", "chat.example.com"]);
        var rules = Compile(list, Domain("*.example.com", RoutingMode.Proxy))["dns"]!["rules"]!.AsArray();
        Assert.Single(rules[0]!["domain_suffix"]!.AsArray());
        Assert.Equal("chat.example.com", (string?)rules[0]!["domain_suffix"]![0]);
    }
    [Fact] public void MissingLanFallsBackWithoutChangingPreference()
    {
        var original = RouterDnsSettings.Select(new() { DnsServer = "1.1.1.1" }, "192.168.1.1");
        var result = RouterDnsConnection.Resolve(original, _ => throw new InvalidOperationException());
        Assert.False(result.Settings.UseRouterDns); Assert.Equal("1.1.1.1", result.Settings.DnsServer);
        Assert.Equal(DnsRoute.Direct, result.Settings.DnsVia); Assert.Contains("DoH", result.Warning); Assert.Contains("PBR", result.Warning);
        Assert.True(original.UseRouterDns);
    }
    [Fact] public void LanFoundRetainsRouterDns()
    {
        var original = RouterDnsSettings.Select(new(), "192.168.1.1");
        var result = RouterDnsConnection.Resolve(original, _ => new("Ethernet", 1, IPAddress.Parse("192.168.1.20"), 24));
        Assert.True(result.Settings.UseRouterDns); Assert.Equal("Ethernet", result.Interface!.Name); Assert.Null(result.Warning);
    }
    [Fact] public void DisabledRouterDoesNotInspectNetwork()
    { Assert.Null(RouterDnsConnection.Resolve(new(), _ => throw new Exception("must not run")).Warning); }
    [Fact] public void SavedFallbackConfigIsClearlyReported()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, """{"dns":{"servers":[{"tag":"remote","type":"https","server":"1.1.1.1"}]}}""");
            Assert.Contains("DoH", RouterDnsConnection.ConfigWarning(new() { UseRouterDns = true, ProxyConfigPath = path }));
        }
        finally { File.Delete(path); }
    }
}
