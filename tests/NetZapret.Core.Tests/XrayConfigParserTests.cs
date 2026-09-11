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

    [Fact]
    public void LinkListStillGoesThroughTheOldPath()
    {
        // Признак конфига — фигурная или квадратная скобка в начале. Список
        // ссылок ею не начинается, и разбирать его должен прежний путь.
        var (servers, _) = SubscriptionParser.ParseBody(
            "vless://b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33@example.com:443?type=tcp&security=tls#Node");

        Assert.Single(servers);
    }
}
