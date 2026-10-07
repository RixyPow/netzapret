using System.Text.Json.Nodes;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Настройки пинга и фрагментация рукопожатия с сервером (владелец 07.10).
/// </summary>
public sealed class PingAndFragmentTests
{
    [Fact]
    public void PingUrlFallsBackToCloudflare()
    {
        Assert.Equal(Ping.Cloudflare, Ping.UrlOf(new AppSettings()));
        Assert.Equal(Ping.Google, Ping.UrlOf(new AppSettings { PingUrl = Ping.Google }));
        Assert.Equal("https://example.com/204", Ping.UrlOf(new AppSettings { PingUrl = " https://example.com/204 " }));

        // Негодный адрес в файле — не повод мерить в никуда.
        Assert.Equal(Ping.Cloudflare, Ping.UrlOf(new AppSettings { PingUrl = "cp.cloudflare.com" }));
        Assert.Equal(Ping.Cloudflare, Ping.UrlOf(new AppSettings { PingUrl = "ftp://example.com/" }));
    }

    /// <summary>
    /// «Лучший из двух» убран 07.10, в тот же день, что появился, — а поле
    /// уже записано в файлы настроек. Оно обязано читаться молча: не прочитайся
    /// файл, программа взяла бы настройки по умолчанию целиком — вплоть
    /// до мастера первого запуска.
    /// </summary>
    [Fact]
    public void SettingsWithTheRemovedBestOfTwoStillRead()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-settings-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "OnboardingDone": true, "PingBestOfTwo": true, "PingUrl": "http://www.gstatic.com/generate_204" }""");

            var settings = AppSettings.TryLoad(path, out var result);

            Assert.Equal(AppSettings.ReadResult.Read, result);
            Assert.True(settings.OnboardingDone);
            Assert.Equal(Ping.Google, Ping.UrlOf(settings));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static readonly ProxyServer[] Servers =
    [
        new()
        {
            Protocol = ProxyProtocol.Vless, Tag = "vless-tls", Host = "tls.example.com", Port = 443,
            Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "tls", Sni = "tls.example.com",
        },
        new()
        {
            Protocol = ProxyProtocol.Vless, Tag = "vless-reality", Host = "reality.example.com", Port = 443,
            Credential = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", Security = "reality", Sni = "www.microsoft.com",
            RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", RealityShortId = "0123456789abcdef",
            Flow = "xtls-rprx-vision",
        },
        new()
        {
            Protocol = ProxyProtocol.Trojan, Tag = "trojan", Host = "trojan.example.com", Port = 443,
            Credential = "PLACEHOLDER",
        },
        new()
        {
            Protocol = ProxyProtocol.Hysteria2, Tag = "hy2", Host = "hy2.example.com", Port = 443,
            Credential = "PLACEHOLDER",
        },
        new()
        {
            Protocol = ProxyProtocol.Shadowsocks, Tag = "ss", Host = "ss.example.com", Port = 8388,
            Credential = "PLACEHOLDER", Method = "aes-256-gcm",
        },
    ];

    private static JsonObject Outbound(string json, string tag) =>
        JsonNode.Parse(json)!["outbounds"]!.AsArray().OfType<JsonObject>().Single(o => o["tag"]?.GetValue<string>() == tag);

    /// <summary>
    /// Режется приветствие TLS поверх TCP — у VLESS (и с REALITY), Trojan;
    /// у Hysteria2 рукопожатие в пакетах QUIC, у Shadowsocks TLS нет. Конфиг
    /// с обоими способами принимает сам движок.
    /// </summary>
    [Theory]
    [InlineData(TlsFragment.Records, "record_fragment")]
    [InlineData(TlsFragment.Packets, "fragment")]
    public void FragmentGoesOnlyToTcpTls(TlsFragment mode, string field)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, Servers, new SingBoxOptions { TlsFragment = mode });

        foreach (var tag in new[] { "vless-tls", "vless-reality", "trojan" })
            Assert.True(Outbound(result.Json, tag)["tls"]![field]!.GetValue<bool>(), tag);

        Assert.Null(Outbound(result.Json, "hy2")["tls"]![field]);
        Assert.Null(Outbound(result.Json, "ss")["tls"]);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    [Fact]
    public void FragmentIsOffByDefault()
    {
        Assert.Equal(TlsFragment.Off, new AppSettings().TlsFragment);

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, Servers, new SingBoxOptions());
        var tls = Outbound(result.Json, "vless-tls")["tls"]!;

        Assert.Null(tls["fragment"]);
        Assert.Null(tls["record_fragment"]);
    }

    /// <summary>Пробник меряет тем же путём, что идёт трафик: с той же фрагментацией.</summary>
    [Fact]
    public void ProbeCarriesTheFragment()
    {
        var json = new SingBoxConfigCompiler().CompileProbeConfig(Servers[0], 20808, null, "warn", fragment: TlsFragment.Packets);

        Assert.True(Outbound(json, "probe-out")["tls"]!["fragment"]!.GetValue<bool>());
    }
}
