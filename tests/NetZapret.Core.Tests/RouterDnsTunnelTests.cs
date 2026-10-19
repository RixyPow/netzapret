using System.Text.Json.Nodes;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

public sealed class RouterDnsTunnelTests
{
    private static RoutingRule Registry(RoutingMode mode, params string[] names)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = ServiceCatalog.RegistryList, Mode = mode };
        rule.LoadHostList(names);
        return rule;
    }

    private static RoutingRule Ru() => new() { Match = MatchKind.Domain, Value = "*.ru", Mode = RoutingMode.Direct };

    private static JsonNode Compile(OperatingMode mode, params RoutingRule[] rules) => JsonNode.Parse(WithoutCanary.Strip(
        new SingBoxConfigCompiler().Compile(new RuleSet { Operating = mode, DefaultMode = RoutingMode.Direct, Rules = rules },
        [new ProxyServer { Protocol = ProxyProtocol.Hysteria2, Tag = "test", Host = "proxy.example", Port = 443,
            Credential = "TEST", Transport = "udp", Security = "tls" }],
        new SingBoxOptions { Scope = TunnelScope.Everything, RouterDns = true, DnsServerType = "udp",
            DnsServer = "192.168.1.1", RouterDnsInterface = "Ethernet" }).Json))!;

    [Theory]
    [InlineData(RoutingMode.Proxy)]
    [InlineData(RoutingMode.Desync)]
    public void TunnelRegistryGetsFakeBeforeDirectRu(RoutingMode mode)
    {
        var json = Compile(OperatingMode.ProxyAll, Registry(mode, "dtf.ru"), Ru());
        var dns = json["dns"]!["rules"]!.AsArray();
        Assert.Equal("fake", (string?)dns[0]!["server"]);
        Assert.Equal("dtf.ru", (string?)dns[0]!["domain_suffix"]![0]);
        Assert.Equal("remote", (string?)dns[1]!["server"]);
        Assert.Equal("ru", (string?)dns[1]!["domain_suffix"]![0]);
        Assert.Equal("fake", (string?)dns.Last()!["server"]);
        // То же исключение стоит раньше ru и в маршрутах.
        var route = json["route"]!["rules"]!.AsArray();
        int registryAt = route.ToList().FindIndex(r => (string?)r?["domain_suffix"]?[0] == "dtf.ru");
        int ruAt = route.ToList().FindIndex(r => (string?)r?["domain_suffix"]?[0] == "ru");
        Assert.True(registryAt >= 0 && registryAt < ruAt);
    }

    [Fact]
    public void EarlierDirectRuIsNotReorderedBehindRegistry()
    {
        var dns = Compile(OperatingMode.ProxyAll, Ru(), Registry(RoutingMode.Proxy, "dtf.ru"))["dns"]!["rules"]!.AsArray();
        Assert.Equal(2, dns.Count);
        Assert.Equal("remote", (string?)dns[0]!["server"]);
        Assert.Equal("ru", (string?)dns[0]!["domain_suffix"]![0]);
    }

    [Fact]
    public void TunnelCopiesOnlyRegistryNamesOverlappingDirectExceptions()
    {
        var dns = Compile(OperatingMode.ProxyAll, Registry(RoutingMode.Proxy, "dtf.ru", "unrelated.invalid"), Ru())["dns"]!["rules"]!.AsArray();
        Assert.Equal("fake", (string?)dns[0]!["server"]);
        Assert.Single(dns[0]!["domain_suffix"]!.AsArray());
        Assert.Equal("dtf.ru", (string?)dns[0]!["domain_suffix"]![0]);
    }

    [Fact]
    public void StrictTunnelDoesNotIncludeDirectExceptions()
    {
        var dns = Compile(OperatingMode.ProxyStrict, Registry(RoutingMode.Proxy, "dtf.ru"), Ru())["dns"]!["rules"]!.AsArray();
        Assert.Single(dns);
        Assert.Equal("fake", (string?)dns[0]!["server"]);
    }
}
