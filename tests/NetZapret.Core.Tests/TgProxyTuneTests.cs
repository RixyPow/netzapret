using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Автонастройка прокси Telegram (03.11): замер решает прямой путь и порядок доменов.</summary>
public sealed class TgProxyTuneTests
{
    [Fact]
    public void Flowseal_list_decodes_the_way_the_proxy_shows_it()
    {
        // Первые строки списка 03.11 и то, что прокси 2.6.0 напечатал в шапке.
        Assert.Equal("pclead.co.uk", TgProxyTune.Decode("virkgj.com"));
        Assert.Equal(["pclead.co.uk", "offshor.co.uk"], TgProxyTune.Parse("# заметка\nvirkgj.com\r\nvmmzovy.com\n\nvirkgj.com\n"));
    }

    [Fact]
    public void Tuning_decides_direct_path_and_puts_live_domains_first()
    {
        var manual = new TgWsProxyRoutes(DirectMain: false, DirectOther: true, WorkerDomains: "w.workers.dev");
        var tuning = new TgProxyTuning(DateTimeOffset.Now, true, 410,
            [.. Enumerable.Range(1, 7).Select(i => new TgBridge($"d{i}.co.uk", 100 + i))], 20);

        var routes = TgProxyTune.Apply(manual, tuning);

        Assert.True(routes.DirectMain);
        Assert.True(routes.DirectOther);
        Assert.Equal(["d1.co.uk", "d2.co.uk", "d3.co.uk", "d4.co.uk", "d5.co.uk"], routes.CfDomains);

        var arguments = TgWsProxy.Arguments(1443, routes);
        int at = arguments.ToList().IndexOf("--cf-domain");

        Assert.True(at > 0);
        Assert.Equal("d1.co.uk,d2.co.uk,d3.co.uk,d4.co.uk,d5.co.uk", arguments[at + 1]);
        Assert.Contains("--default-domains", arguments);
        Assert.Contains("2:149.154.167.220", arguments);
    }

    [Fact]
    public void Dead_direct_path_is_switched_off_even_if_set_by_hand()
    {
        var routes = TgProxyTune.Apply(new TgWsProxyRoutes(DirectMain: true),
            new TgProxyTuning(DateTimeOffset.Now, false, null, [], 20));

        Assert.False(routes.DirectMain);
        Assert.DoesNotContain("--cf-domain", TgWsProxy.Arguments(1443, routes));
    }

    [Fact]
    public void Result_survives_a_round_trip()
    {
        var now = DateTimeOffset.Now;
        var path = Path.Combine(Path.GetTempPath(), $"nz-tg-tune-{Guid.NewGuid():N}.json");
        var tuning = new TgProxyTuning(now, true, 410, [new("onedaychamp.co.uk", 130)], 20);

        try
        {
            TgProxyTune.Save(tuning, path);
            var back = TgProxyTune.Load(path);

            Assert.NotNull(back);
            Assert.Equal("onedaychamp.co.uk", back!.Bridges[0].Domain);
            Assert.Contains("1 из 20", TgProxyTune.Describe(back));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Issue #88: tg-ws-proxy.exe идёт мимо туннеля раньше правила *.telegram.org → VPN,
    /// но после hijack-dns (чтобы получать честный DoH), и не отменяет «Игнорировать исключения».
    /// </summary>
    [Fact]
    public void Sing_box_routes_tg_ws_proxy_direct_before_telegram_vpn_rule_except_strict()
    {
        var server = new ProxyServer
        {
            Protocol = ProxyProtocol.Vless,
            Tag = "DE",
            Host = "de.example.com",
            Port = 443,
            Credential = "11111111-1111-1111-1111-111111111111",
            Transport = "tcp",
            Security = "tls",
            Sni = "de.example.com",
        };

        var hybrid = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.telegram.org"
                mode: proxy
            """);

        var json = new SingBoxConfigCompiler().Compile(hybrid.RuleSet, [server], new SingBoxOptions
        {
            HealthInbound = true,
            TelegramProxy = true,
            Keys = EngineKeys.Generate(),
        }).Json;

        var rules = JsonDocument.Parse(json).RootElement
            .GetProperty("route").GetProperty("rules").EnumerateArray().ToList();

        int dnsIdx = rules.FindIndex(r => r.TryGetProperty("action", out var a) && a.GetString() == "hijack-dns");
        int tgProcIdx = rules.FindIndex(r => r.TryGetProperty("process_name", out var p)
            && p.EnumerateArray().Any(x => x.GetString() == "tg-ws-proxy.exe"));
        int tgDomIdx = rules.FindIndex(r => r.TryGetProperty("domain_suffix", out var d)
            && d.EnumerateArray().Any(x => x.GetString() == "telegram.org"));

        Assert.True(dnsIdx >= 0 && tgProcIdx > dnsIdx, "tg-ws-proxy.exe должен идти после hijack-dns");
        Assert.True(tgDomIdx > tgProcIdx, "tg-ws-proxy.exe должен идти раньше правила *.telegram.org → VPN");
        Assert.Equal("direct", rules[tgProcIdx].GetProperty("outbound").GetString());

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, $"sing-box check не пройден: {said}");

        // Без включённого TG Proxy правило process_name не пишется.
        var withoutTgJson = new SingBoxConfigCompiler().Compile(hybrid.RuleSet, [server], new SingBoxOptions
        {
            HealthInbound = true,
            Keys = EngineKeys.Generate(),
        }).Json;
        Assert.DoesNotContain("tg-ws-proxy.exe", withoutTgJson);

        // В режиме «Игнорировать исключения» исключений нет.
        var strict = hybrid.RuleSet with { Operating = OperatingMode.ProxyStrict };
        var strictJson = new SingBoxConfigCompiler().Compile(strict, [server], new SingBoxOptions
        {
            HealthInbound = true,
            TelegramProxy = true,
            Keys = EngineKeys.Generate(),
        }).Json;
        Assert.DoesNotContain("tg-ws-proxy.exe", strictJson);
    }
}
