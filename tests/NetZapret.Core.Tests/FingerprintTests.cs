using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Отпечаток uTLS из ссылки. sing-box не знает имени — валит весь конфиг:
/// подписка с <c>fp=unsafe</c> (слово Xray) оставляла «Гибрид» без туннеля.
/// </summary>
public sealed class FingerprintTests
{
    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("Chrome", "chrome")]
    [InlineData(" firefox ", "firefox")]
    [InlineData("randomizednoalpn", "randomized")]
    [InlineData("unsafe", null)]
    [InlineData("hellogolang", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void FingerprintIsTranslatedForSingBox(string? link, string? expected)
    {
        Assert.Equal(expected, SingBoxConfigCompiler.Fingerprint(link));
    }

    /// <summary>
    /// Без REALITY чужое имя — родной отпечаток, как и просил Xray своим «unsafe»;
    /// REALITY без подделки не отвечает — ему chrome. Конфиг принимает сам движок.
    /// </summary>
    [Fact]
    public void ForeignFingerprintsDoNotBreakTheEngine()
    {
        ProxyServer[] servers =
        [
            new()
            {
                Protocol = ProxyProtocol.Vless, Tag = "tls-unsafe", Host = "tls.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "tls", Sni = "tls.example.com",
                Fingerprint = "unsafe",
            },
            new()
            {
                Protocol = ProxyProtocol.Vless, Tag = "reality-unsafe", Host = "reality.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
                RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
                Flow = "xtls-rprx-vision", Fingerprint = "unsafe",
            },
            new()
            {
                Protocol = ProxyProtocol.Trojan, Tag = "trojan-upper", Host = "trojan.example.com", Port = 443,
                Credential = "PLACEHOLDER", Fingerprint = "Chrome",
            },
        ];

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        Assert.Null(Outbound(result.Json, "tls-unsafe")["tls"]!["utls"]);
        Assert.Equal("firefox", Outbound(result.Json, "reality-unsafe")["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.Equal("chrome", Outbound(result.Json, "trojan-upper")["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    private static JsonObject Outbound(string json, string tag) =>
        JsonNode.Parse(json)!["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => o["tag"]?.GetValue<string>() == tag);
}
