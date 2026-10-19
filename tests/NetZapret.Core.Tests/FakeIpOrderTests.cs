using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// fakeip в «Гибриде» — в очереди маршрутов (19.10): правило VPN, перекрытое
/// правилом выше, имени подменного адреса больше не даёт (ошибка с 0.11.2).
/// </summary>
public sealed class FakeIpOrderTests
{
    private static JsonArray DnsRules(params RoutingRule[] rules)
    {
        var json = new SingBoxConfigCompiler().Compile(
            new RuleSet { Rules = rules, DefaultMode = RoutingMode.Desync, Operating = OperatingMode.Selective },
            [new ProxyServer { Protocol = ProxyProtocol.Hysteria2, Tag = "test", Host = "proxy.example", Port = 443,
                Credential = "TEST", Transport = "udp", Security = "tls" }],
            new SingBoxOptions { Scope = TunnelScope.ProxyOnly }).Json;

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, said);

        return JsonNode.Parse(WithoutCanary.Strip(json))!["dns"]!["rules"]!.AsArray();
    }

    private static RoutingRule Domain(string name, RoutingMode mode) =>
        new() { Match = MatchKind.Domain, Value = name, Mode = mode };

    private static RoutingRule List(string name, RoutingMode mode, params string[] domains)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = name, Mode = mode };
        rule.LoadHostList(domains);
        return rule;
    }

    private static int Index(JsonArray rules, string server, string name) =>
        rules.ToList().FindIndex(r => (string?)r!["server"] == server
            && (r["domain_suffix"] ?? r["domain"])!.AsArray().Any(n => (string?)n == name));

    [Fact]
    public void ADirectRuleAboveAVpnRuleResolvesHonestly()
    {
        // «Обновления Discord → напрямую» над discord.txt → VPN: имя обновлений
        // спрашивается раньше fakeip и получает настоящий адрес.
        var rules = DnsRules(
            Domain("*.updates.discord.com", RoutingMode.Direct),
            List("discord", RoutingMode.Proxy, "discord.com", "discord.gg"));

        int honest = Index(rules, "remote", "updates.discord.com");
        int fake = Index(rules, "fake", "discord.com");

        Assert.True(honest >= 0, "настоящего адреса для перекрывающего правила нет");
        Assert.True(fake > honest, "fakeip стоит раньше перекрывающего правила");
    }

    [Fact]
    public void AVpnRuleBelowAWiderDesyncRuleIsShadowed()
    {
        // Список «десинк» выше целиком перекрывает правило VPN ниже: маршрут
        // ведёт имя мимо туннеля, и DNS отвечает настоящим адресом раньше fakeip.
        var rules = DnsRules(
            List("youtube", RoutingMode.Desync, "youtube.com"),
            Domain("*.music.youtube.com", RoutingMode.Proxy));

        Assert.True(Index(rules, "remote", "youtube.com") < Index(rules, "fake", "music.youtube.com"));
    }

    [Fact]
    public void UnrelatedDirectRulesStayOutOfDns()
    {
        // remote и так умолчание: правило без пересечения с VPN ниже — лишняя строка.
        var rules = DnsRules(
            Domain("*.vk.ru", RoutingMode.Direct),
            Domain("*.rutracker.org", RoutingMode.Proxy));

        Assert.Equal(-1, Index(rules, "remote", "vk.ru"));
        Assert.True(Index(rules, "fake", "rutracker.org") >= 0);
    }

    [Fact]
    public void NeighbouringVpnListsShareOneRule()
    {
        var rules = DnsRules(
            List("a", RoutingMode.Proxy, "a.example"),
            List("b", RoutingMode.Proxy, "b.example"),
            Domain("*.c.example", RoutingMode.Proxy));

        var fake = Assert.Single(rules, r => (string?)r!["server"] == "fake");
        Assert.Equal(["a.example", "b.example", "c.example"],
            fake!["domain_suffix"]!.AsArray().Select(n => (string?)n));
    }
}
