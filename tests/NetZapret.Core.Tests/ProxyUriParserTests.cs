using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Формы ссылок взяты из реальной подписки (учётные данные заменены).
/// </summary>
public class ProxyUriParserTests
{
    private static ProxyServer Parse(string uri)
    {
        Assert.True(ProxyUriParser.TryParse(uri, out var server, out var error), error);
        return server!;
    }

    [Fact]
    public void VlessWithRealityAndFlowIsParsed()
    {
        var server = Parse(
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@us.example.com:443" +
            "?security=reality&encryption=none&flow=xtls-rprx-vision&fp=chrome" +
            "&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=0123456789abcdef" +
            "&sni=www.microsoft.com&type=tcp#%F0%9F%87%BA%F0%9F%87%B8%20Direct%20%7C%20USA");

        Assert.Equal(ProxyProtocol.Vless, server.Protocol);
        Assert.Equal("us.example.com", server.Host);
        Assert.Equal(443, server.Port);
        Assert.Equal("b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", server.Credential);
        Assert.Equal("tcp", server.Transport);
        Assert.Equal("reality", server.Security);
        Assert.Equal("xtls-rprx-vision", server.Flow);
        Assert.Equal("chrome", server.Fingerprint);
        Assert.Equal("www.microsoft.com", server.Sni);
        Assert.Equal("0123456789abcdef", server.RealityShortId);
        Assert.Equal("🇺🇸 Direct | USA", server.Tag);
        Assert.True(server.IsSupportedBySingBox);
    }

    [Fact]
    public void Hysteria2PathBeforeQueryDoesNotBreakThePort()
    {
        // Ловушка формата: hysteria2://pass@host:4443/?sni=...
        // Наивный разбор «всё до ?» даёт порт "4443/".
        var server = Parse("hysteria2://s3cret@nl.example.com:4443/?sni=nl.example.com#NL");

        Assert.Equal(ProxyProtocol.Hysteria2, server.Protocol);
        Assert.Equal("nl.example.com", server.Host);
        Assert.Equal(4443, server.Port);
        Assert.Equal("s3cret", server.Credential);
        Assert.Equal("tls", server.Security);
        Assert.Equal("NL", server.Tag);
    }

    [Fact]
    public void TrojanWithRealityIsParsed()
    {
        var server = Parse(
            "trojan://p%40ssword@de.example.com:4443" +
            "?security=reality&sni=www.microsoft.com&fp=chrome" +
            "&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=abcd&type=tcp#DE");

        Assert.Equal(ProxyProtocol.Trojan, server.Protocol);
        Assert.Equal("p@ssword", server.Credential);
        Assert.Equal("reality", server.Security);
        Assert.Equal("abcd", server.RealityShortId);
    }

    [Fact]
    public void XhttpTransportIsParsedButFlaggedUnsupportedBySingBox()
    {
        var server = Parse(
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@se.example.com:3443" +
            "?security=reality&encryption=none&type=xhttp&mode=auto&path=%2Fabc" +
            "&fp=chrome&pbk=jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0&sid=ab&sni=x.com#SE");

        Assert.Equal("xhttp", server.Transport);
        Assert.Equal("/abc", server.Path);

        // sing-box такой транспорт не реализует — для него нужен Xray.
        Assert.False(server.IsSupportedBySingBox);
    }

    [Fact]
    public void VmessBase64JsonIsParsed()
    {
        const string json = """
            {"v":"2","ps":"Node 1","add":"jp.example.com","port":"443","id":"b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33","aid":"0","net":"ws","path":"/ray","host":"jp.example.com","tls":"tls"}
            """;

        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));
        var server = Parse($"vmess://{encoded}");

        Assert.Equal(ProxyProtocol.Vmess, server.Protocol);
        Assert.Equal("jp.example.com", server.Host);
        Assert.Equal(443, server.Port);
        Assert.Equal("ws", server.Transport);
        Assert.Equal("tls", server.Security);
        Assert.Equal("/ray", server.Path);
        Assert.Equal("Node 1", server.Tag);
    }

    [Fact]
    public void ShadowsocksBothFormsGiveTheSameResult()
    {
        var userInfo = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("aes-256-gfb:hunter2"));
        var partial = Parse($"ss://{userInfo}@ss.example.com:8388#Node");

        var whole = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("aes-256-gfb:hunter2@ss.example.com:8388"));
        var full = Parse($"ss://{whole}#Node");

        Assert.Equal("aes-256-gfb", partial.Method);
        Assert.Equal("hunter2", partial.Credential);
        Assert.Equal(partial.Host, full.Host);
        Assert.Equal(partial.Port, full.Port);
        Assert.Equal(partial.Credential, full.Credential);
    }

    [Fact]
    public void IPv6HostIsParsed()
    {
        var server = Parse("vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@[2001:db8::1]:443?type=tcp#v6");

        Assert.Equal("2001:db8::1", server.Host);
        Assert.Equal(443, server.Port);
    }

    [Fact]
    public void MissingTagFallsBackToHostAndPort()
    {
        var server = Parse("vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@a.example.com:443?type=tcp");

        Assert.Equal("a.example.com:443", server.Tag);
    }

    [Theory]
    [InlineData("", "пустая строка")]
    [InlineData("не ссылка вовсе", "'://'")]
    [InlineData("wireguard://something@host:443", "неподдерживаемая схема")]
    [InlineData("vless://uuid@host", "не указан порт")]
    [InlineData("vless://host:443", "до '@'")]
    public void MalformedInputReportsAReadableReason(string uri, string expectedFragment)
    {
        Assert.False(ProxyUriParser.TryParse(uri, out _, out var error));
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error!, StringComparison.OrdinalIgnoreCase);
    }
}

public class SubscriptionParserTests
{
    [Fact]
    public void Base64BodyIsDetectedAndDecoded()
    {
        var plain = string.Join('\n',
            "hysteria2://s3cret@nl.example.com:4443/?sni=nl.example.com#NL",
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@us.example.com:443?type=tcp&security=reality#US");

        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain));
        var (servers, errors) = SubscriptionParser.ParseBody(encoded);

        Assert.Empty(errors);
        Assert.Equal(2, servers.Count);
        Assert.Equal(ProxyProtocol.Hysteria2, servers[0].Protocol);
        Assert.Equal(ProxyProtocol.Vless, servers[1].Protocol);
    }

    [Fact]
    public void PlainBodyIsParsedWithoutDecoding()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(
            "hysteria2://s3cret@nl.example.com:4443/?sni=nl.example.com#NL");

        Assert.Empty(errors);
        Assert.Single(servers);
    }

    [Fact]
    public void BadLinesAreCollectedWithoutLosingTheGoodOnes()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(string.Join('\n',
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@us.example.com:443?type=tcp#US",
            "wireguard://nope@host:443#skip",
            "hysteria2://s3cret@nl.example.com:4443/?sni=nl.example.com#NL"));

        Assert.Equal(2, servers.Count);
        Assert.Single(errors);
    }

    [Fact]
    public void ErrorMessagesDoNotLeakCredentials()
    {
        // Сообщение об ошибке уходит в лог; целиком ссылку туда писать нельзя.
        var (_, errors) = SubscriptionParser.ParseBody(
            "wireguard://super-secret-credential-value@host.example.com:443#node");

        Assert.Single(errors);
        Assert.DoesNotContain("super-secret-credential-value", errors[0]);
    }

    [Fact]
    public void HeadersBecomeQuotaAndExpiry()
    {
        var info = SubscriptionParser.BuildInfo(
            Array.Empty<ProxyServer>(),
            Array.Empty<string>(),
            userInfo: "upload=0; download=3077453; total=236223201280; expire=1790693959",
            title: "base64:V09XIFZQTg==",
            updateInterval: "1");

        Assert.Equal("WOW VPN", info.Title);
        Assert.Equal(3077453, info.DownloadBytes);
        Assert.Equal(236223201280, info.TotalBytes);
        Assert.Equal(1, info.UpdateIntervalHours);
        Assert.NotNull(info.ExpiresAt);
        Assert.Equal(2026, info.ExpiresAt!.Value.Year);
        Assert.Equal(236223201280 - 3077453, info.RemainingBytes);
    }

    [Fact]
    public void ZeroTotalMeansUnlimited()
    {
        var info = SubscriptionParser.BuildInfo(
            Array.Empty<ProxyServer>(), Array.Empty<string>(),
            userInfo: "upload=0; download=10; total=0", title: null, updateInterval: null);

        Assert.Null(info.RemainingBytes);
    }

    [Theory]
    [InlineData("happ://add/https://example.com/sub/abc", "https://example.com/sub/abc")]
    [InlineData("https://example.com/sub/abc", "https://example.com/sub/abc")]
    public void ClientWrapperLinksAreUnwrapped(string input, string expected)
    {
        Assert.Equal(expected, SubscriptionClient.Unwrap(new Uri(input)).ToString());
    }
}
