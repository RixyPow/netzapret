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
    /// REALITY по умолчанию (RealityMlkem = true) получает chrome и support_x25519mlkem768,
    /// чтобы проходить проверку Xray 26.9.8+. Конфиг принимает сам движок.
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
                Protocol = ProxyProtocol.Vless, Tag = "tls-firefox", Host = "tls2.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "tls", Sni = "tls2.example.com",
                Fingerprint = "firefox",
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
                Protocol = ProxyProtocol.Vless, Tag = "reality-firefox", Host = "reality2.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
                RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
                Fingerprint = "firefox",
            },
            new()
            {
                Protocol = ProxyProtocol.Vless, Tag = "reality-random", Host = "reality3.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
                RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
                Fingerprint = "randomized",
            },
            new()
            {
                Protocol = ProxyProtocol.Vless, Tag = "reality-chrome-pq", Host = "reality4.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
                RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
                Fingerprint = "chrome_pq",
            },
            new()
            {
                Protocol = ProxyProtocol.Trojan, Tag = "trojan-upper", Host = "trojan.example.com", Port = 443,
                Credential = "PLACEHOLDER", Fingerprint = "Chrome",
            },
        ];

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        // Обычный TLS настройка REALITY не трогает.
        Assert.Null(Outbound(result.Json, "tls-unsafe")["tls"]!["utls"]);
        Assert.Equal("firefox", Outbound(result.Json, "tls-firefox")["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.Equal("chrome", Outbound(result.Json, "trojan-upper")["tls"]!["utls"]!["fingerprint"]!.GetValue<string>());

        // REALITY с RealityMlkem = true: support_x25519mlkem768 и chrome (профили chrome* сохраняются).
        foreach (string tag in new[] { "reality-unsafe", "reality-firefox", "reality-random" })
        {
            var tls = Outbound(result.Json, tag)["tls"]!.AsObject();
            Assert.Equal("chrome", tls["utls"]!["fingerprint"]!.GetValue<string>());
            Assert.True(tls["reality"]!["support_x25519mlkem768"]!.GetValue<bool>());
        }

        var chromePq = Outbound(result.Json, "reality-chrome-pq")["tls"]!.AsObject();
        Assert.Equal("chrome_pq", chromePq["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.True(chromePq["reality"]!["support_x25519mlkem768"]!.GetValue<bool>());

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    /// <summary>
    /// При выключенном <see cref="SingBoxOptions.RealityMlkem"/> сохраняется прежнее поведение
    /// для старых серверов REALITY: без <c>support_x25519mlkem768</c>, отпечаток ссылки
    /// не подменяется, а без отпечатка ставится <c>firefox</c>.
    /// </summary>
    [Fact]
    public void LegacyRealityModeOmitsMlkemAndKeepsFingerprint()
    {
        ProxyServer[] servers =
        [
            new()
            {
                Protocol = ProxyProtocol.Vless, Tag = "reality-unsafe", Host = "reality.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
                RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
                Fingerprint = "unsafe",
            },
            new()
            {
                Protocol = ProxyProtocol.Vless, Tag = "reality-safari", Host = "reality2.example.com", Port = 443,
                Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
                RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
                Fingerprint = "safari",
            },
        ];

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions { RealityMlkem = false });

        var unsafeTls = Outbound(result.Json, "reality-unsafe")["tls"]!.AsObject();
        Assert.Equal("firefox", unsafeTls["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.Null(unsafeTls["reality"]!["support_x25519mlkem768"]);

        var safariTls = Outbound(result.Json, "reality-safari")["tls"]!.AsObject();
        Assert.Equal("safari", safariTls["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.Null(safariTls["reality"]!["support_x25519mlkem768"]);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    /// <summary>Пробник серверов повторяет настройку <c>RealityMlkem</c> боевого конфига.</summary>
    [Fact]
    public void ProbeConfigFollowsRealityMlkem()
    {
        var server = new ProxyServer
        {
            Protocol = ProxyProtocol.Vless, Tag = "reality-probe", Host = "reality.example.com", Port = 443,
            Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
            RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
            Fingerprint = "firefox",
        };

        var compiler = new SingBoxConfigCompiler();
        var onJson = compiler.CompileProbeConfig(server, 24091, null, "warn", realityMlkem: true);
        var offJson = compiler.CompileProbeConfig(server, 24092, null, "warn", realityMlkem: false);

        var onTls = Outbound(onJson, "probe-out")["tls"]!.AsObject();
        Assert.Equal("chrome", onTls["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.True(onTls["reality"]!["support_x25519mlkem768"]!.GetValue<bool>());

        var offTls = Outbound(offJson, "probe-out")["tls"]!.AsObject();
        Assert.Equal("firefox", offTls["utls"]!["fingerprint"]!.GetValue<string>());
        Assert.Null(offTls["reality"]!["support_x25519mlkem768"]);

        var onCheck = SingBoxCheck.Run(onJson);
        Assert.True(onCheck.Ok, onCheck.Said);
        var offCheck = SingBoxCheck.Run(offJson);
        Assert.True(offCheck.Ok, offCheck.Said);
    }

    private static JsonObject Outbound(string json, string tag) =>
        JsonNode.Parse(json)!["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => o["tag"]?.GetValue<string>() == tag);
}
