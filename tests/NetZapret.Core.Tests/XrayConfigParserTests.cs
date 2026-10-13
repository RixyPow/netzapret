using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Разбор подписки, отданной готовым конфигом Xray.
/// </summary>
/// <remarks>
/// Замер 2026-09-11: поставщик отдавал по ссылке не список vless://, а целиком
/// настроенный документ — со своей маршрутизацией и балансировщиком. Подписка
/// разбиралась в ноль серверов и одну строку ошибки, за которой стоял весь
/// документ. Квота и срок при этом читались верно, потому что приходят
/// заголовками, — и со стороны это выглядело как мёртвая подписка.
/// </remarks>
public class XrayConfigParserTests
{
    private const string Config = """
        {
          "outbounds": [
            {
              "tag": "proxy",
              "protocol": "vless",
              "settings": {
                "vnext": [
                  {
                    "address": "example.com",
                    "port": 443,
                    "users": [{ "id": "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", "flow": "" }]
                  }
                ]
              },
              "streamSettings": {
                "network": "xhttp",
                "xhttpSettings": { "mode": "auto", "host": "front.example.com", "path": "/abc" },
                "security": "tls",
                "tlsSettings": { "serverName": "front.example.com", "fingerprint": "firefox", "alpn": ["h2"] }
              }
            },
            { "tag": "direct", "protocol": "freedom" },
            { "tag": "block", "protocol": "blackhole" }
          ]
        }
        """;

    [Fact]
    public void ConfigBodyIsRecognisedAndServersExtracted()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(Config);

        Assert.Empty(errors);
        var server = Assert.Single(servers);

        Assert.Equal(ProxyProtocol.Vless, server.Protocol);
        Assert.Equal("example.com", server.Host);
        Assert.Equal(443, server.Port);
        Assert.Equal("xhttp", server.Transport);
        Assert.Equal("tls", server.Security);
        Assert.Equal("/abc", server.Path);
        Assert.Equal("front.example.com", server.HostHeader);
        Assert.Equal("front.example.com", server.Sni);
        Assert.Equal("firefox", server.Fingerprint);
        Assert.Equal("proxy", server.Tag);
    }

    [Fact]
    public void DirectAndBlockAreNotServers()
    {
        // freedom — это «напрямую», blackhole — «в никуда». Взять их за серверы
        // значило бы показать в списке два выхода, ведущих никуда.
        var (servers, _) = SubscriptionParser.ParseBody(Config);

        Assert.DoesNotContain(servers, s => s.Tag is "direct" or "block");
    }

    [Fact]
    public void ArrayOfConfigsIsAccepted()
    {
        // Поставщики отдают то объект, то массив. Различие ни на что не влияет,
        // а разбор об него спотыкался.
        var (servers, errors) = SubscriptionParser.ParseBody("[" + Config + "]");

        Assert.Empty(errors);
        Assert.Single(servers);
    }

    [Fact]
    public void PortAsStringIsAccepted()
    {
        var (servers, _) = SubscriptionParser.ParseBody(Config.Replace("\"port\": 443", "\"port\": \"443\""));

        Assert.Equal(443, Assert.Single(servers).Port);
    }

    [Fact]
    public void ConfigWithoutServersSaysSoPlainly()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(
            """{ "outbounds": [ { "tag": "direct", "protocol": "freedom" } ] }""");

        Assert.Empty(servers);
        Assert.Contains(errors, e => e.Contains("нет ни одного сервера"));
    }

    /// <summary>
    /// Открытый ключ REALITY у Xray с 2025 и у autoXRAY лежит в
    /// <c>realitySettings.password</c>, а не в <c>publicKey</c>. Пустое поле
    /// sing-box отвергает словами <c>invalid public_key</c> и не поднимает
    /// службу — падает и всё, что в подписке было разобрано.
    /// </summary>
    [Fact]
    public void RealityPasswordIsThePublicKey()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(RealityWithPassword);

        Assert.Empty(errors);
        var server = Assert.Single(servers);

        Assert.Equal("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", server.RealityPublicKey);
        Assert.Equal("0123456789abcdef", server.RealityShortId);
        Assert.Equal("reality", server.Security);
        Assert.Equal("ex.com", server.Sni);
        Assert.Equal("chrome", server.Fingerprint);
        Assert.Equal("tcp", server.Transport);
        Assert.Equal("srv | VLESS RAW REALITY VISION", server.Tag);
    }

    [Fact]
    public void RealityIsRecognisedWhateverTheCase()
    {
        var server = Assert.Single(SubscriptionParser.ParseBody(
            RealityWithPassword.Replace("\"security\": \"reality\"", "\"security\": \"REALITY\"")).Servers);

        Assert.Equal("reality", server.Security);
        Assert.Equal("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", server.RealityPublicKey);
    }

    /// <summary>У TLS поле password — не ключ: брать его можно только у REALITY.</summary>
    [Fact]
    public void TlsPasswordIsNotTakenForAKey()
    {
        var body = RealityWithPassword
            .Replace("\"security\": \"reality\"", "\"security\": \"tls\"")
            .Replace("\"realitySettings\"", "\"tlsSettings\"");

        var server = Assert.Single(SubscriptionParser.ParseBody(body).Servers);

        Assert.Equal("tls", server.Security);
        Assert.Null(server.RealityPublicKey);
    }

    [Fact]
    public void RealityPublicKeyWinsWhenBothNamesArePresent()
    {
        var body = RealityWithPassword.Replace(
            "\"password\": \"jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0\"",
            "\"publicKey\": \"AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw\", \"password\": \"jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0\"");

        var server = Assert.Single(SubscriptionParser.ParseBody(body).Servers);

        Assert.Equal("AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw", server.RealityPublicKey);
    }

    /// <summary>
    /// autoXRAY отдаёт Hysteria2 не ссылкой <c>hy2://</c>, а исходящим Xray
    /// <c>protocol: hysteria</c> версии 2: адрес в <c>settings</c>, пароль
    /// в <c>hysteriaSettings.auth</c>. Прежний разбор такой протокол не знал.
    /// </summary>
    [Fact]
    public void HysteriaVersionTwoIsHysteria2()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(Hysteria2Config);

        Assert.Empty(errors);
        var server = Assert.Single(servers);

        Assert.Equal(ProxyProtocol.Hysteria2, server.Protocol);
        Assert.Equal("ex.com", server.Host);
        Assert.Equal((ushort)8080, server.Port);
        Assert.Equal("short-auth", server.Credential);
        Assert.Equal("udp", server.Transport);
        Assert.Equal("tls", server.Security);
        Assert.Equal("ex.com", server.Sni);
        Assert.Equal(new[] { "h3" }, server.Alpn);
        Assert.Equal("srv | HYSTERIA2", server.Tag);
        Assert.Null(server.ObfsType);
    }

    [Fact]
    public void Hysteria2SalamanderComesFromFinalmask()
    {
        // v2rayN кладёт обфускацию не в obfs ссылки, а в finalmask.udp.
        // Без пароля сервер молчит, и это неотличимо от мёртвого выхода.
        var body = Hysteria2Config.Replace(
            "\"quicParams\": { \"congestion\": \"bbr\" }",
            "\"udp\": [ { \"type\": \"salamander\", \"settings\": { \"password\": \"hidden\" } } ]");

        var server = Assert.Single(SubscriptionParser.ParseBody(body).Servers);

        Assert.Equal("salamander", server.ObfsType);
        Assert.Equal("hidden", server.ObfsPassword);
    }

    [Fact]
    public void HysteriaOfAnotherVersionIsRefusedOutLoud()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(Hysteria2Config
            .Replace("\"version\": 2 },", "\"version\": 1 },")
            .Replace("\"hysteriaSettings\": { \"version\": 2", "\"hysteriaSettings\": { \"version\": 1"));

        Assert.Empty(servers);
        Assert.Contains(errors, e => e.Contains("версии 1"));
    }

    [Fact]
    public void Hysteria2AddressMaySitInServers()
    {
        var body = """
            {
              "outbounds": [ {
                "tag": "proxy", "protocol": "hysteria",
                "settings": { "version": 2, "servers": [ { "address": "b.example", "port": 4443 } ] },
                "streamSettings": {
                  "network": "hysteria", "security": "tls",
                  "tlsSettings": { "serverName": "b.example", "allowInsecure": true },
                  "hysteriaSettings": { "version": 2, "auth": "pw" }
                }
              } ]
            }
            """;

        var server = Assert.Single(SubscriptionParser.ParseBody(body).Servers);

        Assert.Equal(ProxyProtocol.Hysteria2, server.Protocol);
        Assert.Equal("b.example", server.Host);
        Assert.Equal((ushort)4443, server.Port);
        Assert.Equal("pw", server.Credential);
        Assert.True(server.AllowInsecure);
    }

    [Fact]
    public void AutoXraySubscriptionKeepsRealityAndHysteria2()
    {
        // Так устроена подписка autoXRAY: массив конфигов, в каждом одно
        // исходящее и remarks. Оба должны дойти до списка серверов.
        var (servers, errors) = SubscriptionParser.ParseBody("[" + RealityWithPassword + "," + Hysteria2Config + "]");

        Assert.Empty(errors);
        Assert.Equal(2, servers.Count);
        Assert.Contains(servers, s => s.Protocol == ProxyProtocol.Vless && s.RealityPublicKey is { Length: > 0 });
        Assert.Contains(servers, s => s.Protocol == ProxyProtocol.Hysteria2);
    }

    [Fact]
    public void ParsedAutoXrayPiecesPassSingBoxCheck()
    {
        var (servers, errors) = SubscriptionParser.ParseBody("[" + RealityWithPassword + "," + Hysteria2Config + "]");
        Assert.Empty(errors);

        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions()).Json;
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;

        var reality = root.GetProperty("outbounds").EnumerateArray()
            .Single(o => o.GetProperty("type").GetString() == "vless");
        Assert.Equal("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0",
            reality.GetProperty("tls").GetProperty("reality").GetProperty("public_key").GetString());

        Assert.Contains(root.GetProperty("outbounds").EnumerateArray(),
            o => o.GetProperty("type").GetString() == "hysteria2");

        var check = SingBoxCheck.Run(json);
        Assert.True(check.Ok, check.Said);
    }

    [Fact]
    public void LinkListStillGoesThroughTheOldPath()
    {
        // Признак конфига — фигурная или квадратная скобка в начале. Список
        // ссылок ею не начинается, и разбирать его должен прежний путь.
        var (servers, _) = SubscriptionParser.ParseBody(
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@example.com:443?type=tcp&security=tls#Node");

        Assert.Single(servers);
    }

    private const string RealityWithPassword = """
        {
          "remarks": "srv | VLESS RAW REALITY VISION",
          "outbounds": [ {
            "tag": "proxy", "protocol": "vless",
            "settings": { "vnext": [ { "address": "ex.com", "port": 443,
              "users": [ { "id": "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33", "flow": "xtls-rprx-vision", "encryption": "none" } ] } ] },
            "streamSettings": {
              "network": "raw", "security": "reality",
              "realitySettings": {
                "fingerprint": "chrome", "serverName": "ex.com",
                "password": "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0",
                "shortId": "0123456789abcdef", "spiderX": "/"
              }
            }
          } ]
        }
        """;

    private const string Hysteria2Config = """
        {
          "remarks": "srv | HYSTERIA2",
          "outbounds": [ {
            "tag": "proxy", "protocol": "hysteria",
            "settings": { "address": "ex.com", "port": 8080, "version": 2 },
            "streamSettings": {
              "network": "hysteria", "security": "tls",
              "tlsSettings": { "serverName": "ex.com", "alpn": ["h3"], "fingerprint": "chrome" },
              "hysteriaSettings": { "version": 2, "auth": "short-auth" },
              "finalmask": { "quicParams": { "congestion": "bbr" } }
            }
          } ]
        }
        """;
}
