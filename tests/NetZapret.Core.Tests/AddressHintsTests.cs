using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Имена «через VPN» не получают записи HTTPS с настоящими адресами (08.11:
/// Discord брал адрес discord.com из ipv4hint и шёл мимо туннеля).
/// </summary>
public sealed class AddressHintsTests
{
    private static ProxyServer Server() => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Transport = "udp",
        Security = "tls",
        Sni = "nl.example.com",
    };

    private const string Rules = """
        mode: selective
        rules:
          - match: domain
            value: "status.discord.com"
            mode: direct
          - match: domain
            value: "*.discord.com"
            mode: proxy
          - match: domain
            value: "x.com"
            mode: proxy
          - match: domain
            value: "*.rutracker.org"
            mode: desync
        """;

    private static JsonArray DnsRules(string json) => JsonNode.Parse(json)!["dns"]!["rules"]!.AsArray();

    [Fact]
    public void Https_records_of_tunnel_names_are_answered_empty_after_the_queue()
    {
        var json = new SingBoxConfigCompiler().Compile(
            RuleSetLoader.Load(Rules).RuleSet, [Server()], new SingBoxOptions { Scope = TunnelScope.ProxyOnly }).Json;

        var rules = DnsRules(json);
        int hintsAt = rules.ToList().FindIndex(r => (string?)r!["rcode"] == "NOERROR");

        Assert.True(hintsAt >= 0);
        var hints = rules[hintsAt]!;

        Assert.Equal(["HTTPS", "SVCB"], hints["query_type"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("predefined", (string?)hints["action"]);

        var names = hints["domain"]?.AsArray().Select(n => (string)n!).ToList() ?? [];
        names.AddRange(hints["domain_suffix"]?.AsArray().Select(n => (string)n!) ?? []);

        Assert.Contains("discord.com", names);
        Assert.Contains("x.com", names);
        Assert.DoesNotContain("rutracker.org", names);

        // «Напрямую» выше без query_type — забирает HTTPS своего имени раньше.
        int directAt = rules.ToList().FindIndex(r =>
            r!["domain"] is JsonArray d && d.Any(n => (string?)n == "status.discord.com"));
        Assert.True(directAt >= 0 && directAt < hintsAt);
        Assert.Null(rules[directAt]!["query_type"]);

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, said);
    }

    [Fact]
    public void Without_servers_there_is_nothing_to_hide()
    {
        var json = new SingBoxConfigCompiler().Compile(
            RuleSetLoader.Load(Rules).RuleSet, [], new SingBoxOptions { Scope = TunnelScope.ProxyOnly }).Json;

        Assert.DoesNotContain(DnsRules(json), r => (string?)r!["rcode"] == "NOERROR");
    }
}
