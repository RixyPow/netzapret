using System.Diagnostics;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подписка, отданная готовым конфигом sing-box.
/// </summary>
/// <remarks>
/// Issue #3, 23.09: ltvpn в INCY — сто тридцать пять серверов, у нас — ноль,
/// при пришедших сроке и лимите. Мы представляемся панели движком, и она
/// отдаёт конфиг sing-box, а разбор JSON понимал только Xray. Образец ниже
/// собран по виду таких ответов: четыре протокола с экрана автора, группы
/// выбора и служебные исходящие.
/// </remarks>
public sealed class SingBoxSubscriptionTests
{
    private const string Body = """
        {
          "log": { "level": "warn" },
          "outbounds": [
            { "type": "selector", "tag": "proxy", "outbounds": ["auto", "FR VLESS"] },
            { "type": "urltest", "tag": "auto", "outbounds": ["FR VLESS", "FR TROJAN"] },
            {
              "type": "vless", "tag": "FR VLESS",
              "server": "fr.example.com", "server_port": 443,
              "uuid": "11111111-2222-3333-4444-555555555555",
              "flow": "xtls-rprx-vision",
              "tls": {
                "enabled": true, "server_name": "www.microsoft.com",
                "utls": { "enabled": true, "fingerprint": "chrome" },
                "reality": { "enabled": true, "public_key": "AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw", "short_id": "ab12" }
              }
            },
            {
              "type": "trojan", "tag": "FR TROJAN",
              "server": "fr2.example.com", "server_port": "8443",
              "password": "secret",
              "tls": { "enabled": true, "server_name": "fr2.example.com", "alpn": ["h2", "http/1.1"] },
              "transport": { "type": "ws", "path": "/ws", "headers": { "Host": "cdn.example.com" } }
            },
            {
              "type": "hysteria2", "tag": "FR HY2",
              "server": "fr3.example.com", "server_port": 4443,
              "password": "hy-secret",
              "obfs": { "type": "salamander", "password": "obfs-secret" },
              "tls": { "server_name": "fr3.example.com" }
            },
            {
              "type": "shadowsocks", "tag": "FR SS",
              "server": "203.0.113.7", "server_port": 8388,
              "method": "aes-128-gcm", "password": "ss-secret"
            },
            { "type": "direct", "tag": "direct" },
            { "type": "block", "tag": "block" }
          ]
        }
        """;

    [Fact]
    public void Every_server_is_read_and_the_groups_are_not()
    {
        var (servers, errors) = SubscriptionParser.ParseBody(Body);

        Assert.Empty(errors);
        Assert.Equal(["FR VLESS", "FR TROJAN", "FR HY2", "FR SS"], servers.Select(s => s.Tag));
    }

    [Fact]
    public void Reality_comes_through_whole()
    {
        var vless = SubscriptionParser.ParseBody(Body).Servers.Single(s => s.Protocol == ProxyProtocol.Vless);

        Assert.Equal("fr.example.com", vless.Host);
        Assert.Equal(443, vless.Port);
        Assert.Equal("reality", vless.Security);
        Assert.Equal("www.microsoft.com", vless.Sni);
        Assert.Equal("chrome", vless.Fingerprint);
        Assert.Equal("AwoRGB8mLTQ7QklQV15lbHN6gYiPlp2kq7K5wMfO1dw", vless.RealityPublicKey);
        Assert.Equal("ab12", vless.RealityShortId);
        Assert.Equal("xtls-rprx-vision", vless.Flow);
    }

    [Fact]
    public void Transport_port_as_text_and_alpn_come_through()
    {
        var trojan = SubscriptionParser.ParseBody(Body).Servers.Single(s => s.Protocol == ProxyProtocol.Trojan);

        Assert.Equal(8443, trojan.Port);
        Assert.Equal("ws", trojan.Transport);
        Assert.Equal("/ws", trojan.Path);
        Assert.Equal("cdn.example.com", trojan.HostHeader);
        Assert.Equal(["h2", "http/1.1"], trojan.Alpn);
    }

    [Fact]
    public void Hysteria2_keeps_its_obfuscation()
    {
        // Без неё сервер молчит, и подписка выглядит мёртвой — см. ProxyServer.ObfsType.
        var hy2 = SubscriptionParser.ParseBody(Body).Servers.Single(s => s.Protocol == ProxyProtocol.Hysteria2);

        Assert.Equal("tls", hy2.Security);
        Assert.Equal("salamander", hy2.ObfsType);
        Assert.Equal("obfs-secret", hy2.ObfsPassword);
    }

    [Fact]
    public void Shadowsocks_keeps_its_method()
    {
        var ss = SubscriptionParser.ParseBody(Body).Servers.Single(s => s.Protocol == ProxyProtocol.Shadowsocks);

        Assert.Equal("aes-128-gcm", ss.Method);
        Assert.Equal("ss-secret", ss.Credential);
    }

    [Fact]
    public void An_unknown_protocol_is_named_not_swallowed()
    {
        var (servers, errors) = SubscriptionParser.ParseBody("""
            { "outbounds": [ { "type": "tuic", "tag": "X", "server": "x.example.com", "server_port": 1 } ] }
            """);

        Assert.Empty(servers);
        Assert.Contains(errors, e => e.Contains("tuic"));
    }

    [Fact]
    public void What_was_read_passes_sing_box_check()
    {
        // Прочитать мало: собранный из прочитанного конфиг должен принять
        // сам движок — только ему здесь и можно верить.
        var singBox = FindSingBox();
        if (singBox is null)
            return;

        var servers = SubscriptionParser.ParseBody(Body).Servers;
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        Assert.Equal(4, result.UsedServers.Count);

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-check-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, result.Json);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var said = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, said);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? FindSingBox()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var tools = Path.Combine(directory.FullName, "tools");

            if (Directory.Exists(tools)
                && Directory.EnumerateFiles(tools, "sing-box.exe", SearchOption.AllDirectories).FirstOrDefault() is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
