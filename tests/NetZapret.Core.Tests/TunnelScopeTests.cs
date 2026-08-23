using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Выборочный перехват: в туннель должен заходить только трафик прокси,
/// чтобы десинк работал на исходных потоках приложений.
/// </summary>
public class TunnelScopeTests
{
    private static ProxyServer Server() => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = "node",
        Host = "example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Security = "tls",
    };

    private static JsonElement Compile(string yaml, SingBoxOptions options)
    {
        var engine = RuleSetLoader.Load(yaml);
        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Server()], options).Json;

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private const string Rules = """
        mode: selective
        rules:
          - match: domain
            value: "*.rutracker.org"
            mode: proxy
          - match: domain
            value: "*.youtube.com"
            mode: desync
          - match: ip
            value: "203.0.113.0/24"
            mode: proxy
        """;

    [Fact]
    public void ProxyOnlyCapturesFakeIpRangesOnly()
    {
        var root = Compile(Rules, new SingBoxOptions { Scope = TunnelScope.ProxyOnly });
        var captured = root.GetProperty("inbounds")[0].GetProperty("route_address")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("198.18.0.0/15", captured);
        Assert.Contains("fc00::/18", captured);

        // Правило mode: proxy по адресу тоже обязано попасть в туннель:
        // fakeip ему не выдаётся, домена у него нет.
        Assert.Contains("203.0.113.0/24", captured);
    }

    [Fact]
    public void ProxyOnlyCapturesResolverAddressesSoFakeIpHasSomethingToRewrite()
    {
        var root = Compile(Rules, new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            DnsServerAddresses = ["192.168.1.1/32"],
        });

        var captured = root.GetProperty("inbounds")[0].GetProperty("route_address")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("192.168.1.1/32", captured);
    }

    [Fact]
    public void ProxyOnlyGivesFakeIpToProxyDomainsOnly()
    {
        // Смысл схемы: десинк-домены получают настоящий адрес и в туннель
        // не заходят, поэтому WinDivert видит исходный поток.
        var root = Compile(Rules, new SingBoxOptions { Scope = TunnelScope.ProxyOnly });
        var dnsRules = root.GetProperty("dns").GetProperty("rules").EnumerateArray().ToList();

        var suffixes = dnsRules
            .SelectMany(r => r.GetProperty("domain_suffix").EnumerateArray())
            .Select(e => e.GetString())
            .ToList();

        Assert.Contains("rutracker.org", suffixes);
        Assert.DoesNotContain("youtube.com", suffixes);
        Assert.Equal("remote", root.GetProperty("dns").GetProperty("final").GetString());
    }

    [Fact]
    public void EverythingScopeKeepsBlanketCaptureAndBlanketFakeIp()
    {
        var root = Compile(Rules, new SingBoxOptions { Scope = TunnelScope.Everything });
        var tun = root.GetProperty("inbounds")[0];

        Assert.False(tun.TryGetProperty("route_address", out _));

        var dnsRule = root.GetProperty("dns").GetProperty("rules")[0];
        Assert.False(dnsRule.TryGetProperty("domain_suffix", out _));
        Assert.Equal("fake", dnsRule.GetProperty("server").GetString());
    }

    [Fact]
    public void PresetSubnetsBecomeRouteExclusions()
    {
        var root = Compile(Rules, new SingBoxOptions
        {
            DesyncAddresses = ["64.233.161.0/24", "185.68.247.42/32"],
        });

        var excluded = root.GetProperty("inbounds")[0].GetProperty("route_exclude_address")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(2, excluded.Count);
        Assert.Contains("185.68.247.42/32", excluded);
    }

    [Fact]
    public void WildcardDomainsAreNotSentToFakeIpAsSuffixes()
    {
        // domain_suffix не понимает звёздочку в середине: такое правило
        // молча не сработало бы, а fakeip выдал бы адрес не тому домену.
        var root = Compile("""
            mode: selective
            rules:
              - match: domain
                value: "cdn-*.example.com"
                mode: proxy
            """, new SingBoxOptions { Scope = TunnelScope.ProxyOnly });

        Assert.Empty(root.GetProperty("dns").GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public void ProxyAllModeIgnoresSelectiveCapture()
    {
        // В режиме «весь трафик через VPN» выборочный перехват бессмыслен:
        // доменных правил нет, и fakeip некому выдавать.
        var root = Compile("""
            mode: proxy
            rules: []
            """, new SingBoxOptions { Scope = TunnelScope.ProxyOnly });

        Assert.Empty(root.GetProperty("dns").GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public void LocalProxyModeHasNoTunAtAll()
    {
        var root = Compile(Rules, new SingBoxOptions { UseTun = false, LocalListenPort = 21080 });
        var inbound = root.GetProperty("inbounds")[0];

        Assert.Equal("mixed", inbound.GetProperty("type").GetString());
        Assert.Equal(21080, inbound.GetProperty("listen_port").GetInt32());
    }
}
