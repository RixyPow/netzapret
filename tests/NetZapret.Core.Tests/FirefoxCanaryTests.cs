using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Конфиг без сигнала Firefox — для тестов о прочих правилах DNS.</summary>
/// <remarks>Сигнал стоит первым всегда (<see cref="FirefoxCanaryTests"/>), и счёт правил по номерам о нём не знает.</remarks>
internal static class WithoutCanary
{
    public static string Strip(string json)
    {
        var root = JsonNode.Parse(json)!;

        if (root["dns"]?["rules"] is JsonArray { Count: > 0 } rules
            && rules[0]?["action"]?.GetValue<string>() == "predefined")
        {
            rules.RemoveAt(0);
        }

        return root.ToJsonString();
    }
}

/// <summary>
/// NXDOMAIN на use-application-dns.net — Firefox не включает свой DoH мимо движка (19.10).
/// </summary>
public sealed class FirefoxCanaryTests
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
            value: "*.rutracker.org"
            mode: proxy
        """;

    [Theory]
    [InlineData(TunnelScope.ProxyOnly)]
    [InlineData(TunnelScope.Everything)]
    public void CanaryIsAnsweredFirstAndTheConfigIsAccepted(TunnelScope scope)
    {
        var json = new SingBoxConfigCompiler().Compile(
            RuleSetLoader.Load(Rules).RuleSet, [Server()], new SingBoxOptions { Scope = scope }).Json;

        var first = JsonNode.Parse(json)!["dns"]!["rules"]![0]!;

        // Первым: в «Туннеле» общее правило fakeip иначе отдало бы сигналу
        // подменный адрес, и Firefox счёл бы, что DoH включать можно.
        Assert.Equal(SingBoxConfigCompiler.FirefoxCanary, (string?)first["domain"]![0]);
        Assert.Equal("predefined", (string?)first["action"]);
        Assert.Equal("NXDOMAIN", (string?)first["rcode"]);

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, said);
    }
}
