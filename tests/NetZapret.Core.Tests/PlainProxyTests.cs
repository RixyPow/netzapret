using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Обычные прокси SOCKS и HTTP(S) — с 24.10 (обсуждение №35, владелец: «делай их поддержку»).
/// </summary>
/// <remarks>
/// Учётные данные выдуманы, адреса — example.com и loopback. Главная опасность
/// правки — <c>http(s)://</c>: та же схема у ссылки на подписку, и подписка
/// не должна стать «ключом».
/// </remarks>
public sealed class PlainProxyTests
{
    private static ProxyServer Parse(string uri)
    {
        Assert.True(ProxyUriParser.TryParse(uri, out var server, out var error), error);
        return server!;
    }

    [Fact]
    public void Socks5_with_login_is_parsed()
    {
        var server = Parse("socks5://us%40er:p%3Ass@proxy.example.com:1080#Мой%20прокси");

        Assert.Equal(ProxyProtocol.Socks, server.Protocol);
        Assert.Equal("proxy.example.com", server.Host);
        Assert.Equal(1080, server.Port);
        Assert.Equal("us@er", server.Credential);
        Assert.Equal("p:ss", server.Password);
        Assert.Equal("5", server.SocksVersion);
        Assert.Equal("Мой прокси", server.Tag);
        Assert.Equal("SOCKS5", server.ProtocolName);
    }

    [Fact]
    public void Socks_login_in_base64_like_v2rayN_is_parsed()
    {
        // base64("user:pass")
        var server = Parse("socks://dXNlcjpwYXNz@1.2.3.4:1080#v2rayN");

        Assert.Equal("user", server.Credential);
        Assert.Equal("pass", server.Password);
    }

    [Fact]
    public void Local_socks_without_login_is_parsed()
    {
        var server = Parse("socks5://127.0.0.1:2080");

        Assert.Equal(string.Empty, server.Credential);
        Assert.Null(server.Password);
        Assert.True(server.IsUsableOutbound);
        Assert.Equal("SOCKS 127.0.0.1:2080", server.Tag);
    }

    [Fact]
    public void Socks4_keeps_its_version()
    {
        Assert.Equal("4", Parse("socks4://10.0.0.1:1080").SocksVersion);
        Assert.Equal("4a", Parse("socks4a://10.0.0.1:1080").SocksVersion);
    }

    [Fact]
    public void Http_and_https_proxies_are_parsed()
    {
        var plain = Parse("http://user:pass@proxy.example.com:3128");
        Assert.Equal(ProxyProtocol.Http, plain.Protocol);
        Assert.Equal("none", plain.Security);
        Assert.Equal("HTTP", plain.ProtocolName);

        var secure = Parse("https://proxy.example.com:443/#TLS");
        Assert.Equal("tls", secure.Security);
        Assert.Equal("proxy.example.com", secure.Sni);
        Assert.Equal("HTTPS", secure.ProtocolName);
        Assert.Equal(string.Empty, secure.Credential);
    }

    [Theory]
    [InlineData("https://panel.example.com/sub/abcdef")]
    [InlineData("https://panel.example.com:2096/sub/abcdef")]
    [InlineData("https://panel.example.com:8443/?token=abc")]
    [InlineData("https://panel.example.com")]
    [InlineData("http://panel.example.com/api/v1/client/subscribe?token=x")]
    public void A_subscription_link_is_not_a_proxy(string link)
    {
        Assert.False(ProxyUriParser.LooksLikeHttpProxy(link));
        Assert.False(KeyRing.IsKey(link));
        Assert.False(ProxyUriParser.TryParse(link, out _, out _));

        var found = KeyImport.FromText(link);
        Assert.Empty(found.Keys);
        Assert.NotNull(found.SubscriptionUrl);
    }

    [Fact]
    public void Proxy_links_are_keys_and_split_apart()
    {
        Assert.True(KeyRing.IsKey("http://127.0.0.1:8080"));

        var keys = KeyRing.Split("socks5://a:b@x.example.com:1080socks://127.0.0.1:2080 http://127.0.0.1:8080");

        Assert.Equal(["socks5://a:b@x.example.com:1080", "socks://127.0.0.1:2080", "http://127.0.0.1:8080"], keys);
    }

    [Fact]
    public void The_outbounds_carry_the_login_only_when_there_is_one()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var servers = new[]
        {
            Parse("socks5://user:pass@s.example.com:1080#S"),
            Parse("socks4://127.0.0.1:1081#S4"),
            Parse("http://h.example.com:3128#H"),
            Parse("https://user:pass@t.example.com:443#T"),
        };

        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());
        Assert.Equal(4, result.UsedServers.Count);

        var outbounds = JsonNode.Parse(result.Json)!["outbounds"]!.AsArray()
            .Select(o => o!.AsObject())
            .Where(o => o["type"]!.GetValue<string>() is "socks" or "http")
            .ToDictionary(o => o["tag"]!.GetValue<string>());

        var socks = outbounds.Values.Single(o => o["server"]!.GetValue<string>() == "s.example.com");
        Assert.Equal("5", socks["version"]!.GetValue<string>());
        Assert.Equal("user", socks["username"]!.GetValue<string>());
        Assert.Equal("pass", socks["password"]!.GetValue<string>());

        var socks4 = outbounds.Values.Single(o => o["server"]!.GetValue<string>() == "127.0.0.1");
        Assert.Equal("4", socks4["version"]!.GetValue<string>());
        Assert.False(socks4.ContainsKey("username"));

        var http = outbounds.Values.Single(o => o["server"]!.GetValue<string>() == "h.example.com");
        Assert.False(http.ContainsKey("tls"));
        Assert.False(http.ContainsKey("password"));

        var https = outbounds.Values.Single(o => o["server"]!.GetValue<string>() == "t.example.com");
        Assert.True(https["tls"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("t.example.com", https["tls"]!["server_name"]!.GetValue<string>());

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    [Fact]
    public void Sing_box_and_xray_subscriptions_with_proxies_are_read()
    {
        var (fromSingBox, singBoxErrors) = SubscriptionParser.ParseBody("""
            { "outbounds": [
              { "type": "socks", "tag": "S", "server": "s.example.com", "server_port": 1080,
                "version": "5", "username": "u", "password": "p" },
              { "type": "http", "tag": "H", "server": "h.example.com", "server_port": 443,
                "tls": { "enabled": true, "server_name": "h.example.com" } }
            ] }
            """);

        Assert.Empty(singBoxErrors);
        Assert.Equal([ProxyProtocol.Socks, ProxyProtocol.Http], fromSingBox.Select(s => s.Protocol));
        Assert.Equal("u", fromSingBox[0].Credential);
        Assert.Equal("p", fromSingBox[0].Password);
        Assert.Equal("tls", fromSingBox[1].Security);
        Assert.Equal(string.Empty, fromSingBox[1].Credential);

        var (fromXray, xrayErrors) = XrayConfigParser.Parse("""
            { "outbounds": [
              { "protocol": "socks", "tag": "XS",
                "settings": { "servers": [ { "address": "x.example.com", "port": 1080,
                  "users": [ { "user": "u", "pass": "p" } ] } ] } },
              { "protocol": "http", "tag": "XH",
                "settings": { "servers": [ { "address": "y.example.com", "port": 8080 } ] } },
              { "protocol": "freedom", "tag": "direct" }
            ] }
            """);

        Assert.Empty(xrayErrors);
        Assert.Equal([ProxyProtocol.Socks, ProxyProtocol.Http], fromXray.Select(s => s.Protocol));
        Assert.Equal("u", fromXray[0].Credential);
        Assert.Equal("p", fromXray[0].Password);
        Assert.Equal(8080, fromXray[1].Port);
    }
}
