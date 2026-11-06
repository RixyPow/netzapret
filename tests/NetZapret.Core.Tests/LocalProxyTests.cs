using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Локальный прокси для программ человека (обсуждение №42).</summary>
public class LocalProxyTests
{
    private static readonly ProxyServer Vless = new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 443,
        Credential = "PLACEHOLDER",
        Transport = "tcp",
        Security = "reality",
        Sni = "nl.example.com",
        RealityPublicKey = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0",
        RealityShortId = "abcd",
    };

    private static (string Json, JsonElement Root) Compile(int? port, params ProxyServer[] servers)
    {
        // «Гибрид» с правилом «напрямую» на всё: прокси обязан идти в туннель
        // мимо него, иначе программа получила бы домашний адрес.
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.example.org"
                mode: direct
            """);

        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions
        {
            HealthInbound = true,
            Keys = EngineKeys.Generate(),
            LocalProxyPort = port,
        }).Json;

        return (json, JsonDocument.Parse(json).RootElement.Clone());
    }

    private static List<JsonElement> RulesOf(JsonElement root) =>
        root.GetProperty("route").GetProperty("rules").EnumerateArray().ToList();

    private static bool IsLocalProxyRule(JsonElement rule) =>
        rule.TryGetProperty("inbound", out var inbound)
        && inbound.EnumerateArray().Any(i => i.GetString() == SingBoxOptions.LocalProxyTag);

    [Fact]
    public void Off_by_default_there_is_no_inbound()
    {
        var (json, _) = Compile(null, Vless);

        Assert.DoesNotContain(SingBoxOptions.LocalProxyTag, json);
    }

    [Fact]
    public void Inbound_listens_on_loopback_only_and_without_password()
    {
        var (_, root) = Compile(2080, Vless);

        var inbound = root.GetProperty("inbounds").EnumerateArray()
            .Single(i => i.GetProperty("tag").GetString() == SingBoxOptions.LocalProxyTag);

        Assert.Equal("mixed", inbound.GetProperty("type").GetString());
        Assert.Equal("127.0.0.1", inbound.GetProperty("listen").GetString());
        Assert.Equal(2080, inbound.GetProperty("listen_port").GetInt32());

        // Браузеры SOCKS с паролем не умеют.
        Assert.False(inbound.TryGetProperty("users", out _));
    }

    [Fact]
    public void Goes_to_the_tunnel_before_user_rules_but_after_the_home_network()
    {
        var (_, root) = Compile(2080, Vless);
        var rules = RulesOf(root);

        int own = rules.FindIndex(IsLocalProxyRule);
        int home = rules.FindIndex(r => r.TryGetProperty("ip_is_private", out _) && !r.TryGetProperty("inbound", out _));
        int user = rules.FindIndex(r => r.ToString().Contains("example.org", StringComparison.Ordinal));

        Assert.True(own >= 0);
        Assert.True(home >= 0 && home < own, "домашняя сеть через прокси — как есть");
        Assert.True(user < 0 || own < user, "правила маршрутов прокси не касаются");

        Assert.Equal(SingBoxOptions.ProxyExitTag, rules[own].GetProperty("outbound").GetString());
    }

    private static JsonElement Outbound(JsonElement root, string tag) =>
        root.GetProperty("outbounds").EnumerateArray().Single(o => o.GetProperty("tag").GetString() == tag);

    private static List<string> Members(JsonElement group) =>
        group.GetProperty("outbounds").EnumerateArray().Select(m => m.GetString()!).ToList();

    [Fact]
    public void Proxy_group_has_no_direct_exit_so_tunnel_bypass_cannot_leak_home_address()
    {
        // Обход туннеля ставит основную группу на «direct»; прокси идёт своей.
        var (_, root) = Compile(2080, Vless);

        var main = Members(Outbound(root, new SingBoxOptions().SelectorTag));
        var proxy = Members(Outbound(root, SingBoxOptions.ProxyExitTag));

        Assert.Contains("direct", main);
        Assert.DoesNotContain("direct", proxy);
        Assert.Equal(main.Where(m => m != "direct"), proxy);
    }

    [Fact]
    public void Proxy_pool_is_built_even_without_routes_and_the_proxy_follows_it()
    {
        var second = Vless with { Tag = "DE", Host = "de.example.com" };
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");
        var pool = new ServerPool("fast", "Скоростной", ["DE"]);

        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Vless, second], new SingBoxOptions
        {
            LocalProxyPort = 2080,
            LocalProxyServer = ServerPools.RuleServer("fast"),
            Pools = [pool],
        }).Json;
        var root = JsonDocument.Parse(json).RootElement;

        var proxy = Members(Outbound(root, SingBoxOptions.ProxyExitTag));

        Assert.Equal(ServerPools.LatencyTag("fast"), proxy[0]);
        Assert.DoesNotContain("direct", proxy);
        Assert.DoesNotContain("auto", proxy);
        Assert.Single(proxy, m => m.Contains("DE", StringComparison.Ordinal));
        Assert.DoesNotContain(proxy, m => m.Contains("NL", StringComparison.Ordinal));
        _ = Outbound(root, ServerPools.SelectorTag("fast"));

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, $"sing-box check не пройден: {said}");
    }

    [Fact]
    public void Proxy_only_hybrid_acts_only_with_the_proxy_and_without_full_capture()
    {
        var on = new AppSettings { LocalProxyOn = true, LocalProxyCard = true, HybridProxyOnly = true };

        Assert.True(on.HybridProxyOnlyActive);
        Assert.False(on.TunnelCarriesRoutes);

        Assert.False((on with { LocalProxyOn = false }).HybridProxyOnlyActive);
        Assert.False(on.With(on.Engines with { IgnoreExclusions = true }).HybridProxyOnlyActive);
        Assert.False((on with { HybridProxyOnly = false }).HybridProxyOnlyActive);
    }

    [Fact]
    public void Proxy_pool_needs_selective_routing()
    {
        var pool = new ServerPool("fast", "Скоростной", ["DE"]);
        var settings = new AppSettings { LocalProxyPool = "fast", Pools = [pool], SelectiveRouting = true };

        Assert.Equal(ServerPools.RuleServer("fast"), settings.LocalProxyServer);
        Assert.Null((settings with { SelectiveRouting = false }).LocalProxyServer);
        Assert.Null((settings with { LocalProxyPool = "нет" }).LocalProxyServer);
    }

    [Fact]
    public void Without_servers_the_proxy_refuses_instead_of_going_home()
    {
        var (_, root) = Compile(2080);

        var rule = RulesOf(root).Single(IsLocalProxyRule);

        Assert.Equal("reject", rule.GetProperty("action").GetString());
    }

    [Fact]
    public void Config_with_the_proxy_passes_sing_box_check()
    {
        var (json, _) = Compile(2080, Vless);

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, $"sing-box check не пройден: {said}");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(21090)]
    [InlineData(21100)]
    [InlineData(9090)]
    [InlineData(39150)]
    [InlineData(1443)]
    public void Ports_of_the_program_itself_are_refused(int port)
    {
        Assert.NotNull(LocalProxy.Problem(port, new AppSettings(), ownNow: true));
    }

    [Fact]
    public void Card_follows_the_proxy_until_the_setting_is_made()
    {
        // До настройки показа прокси включался в окне настроек (e0e51d9):
        // включённый не должен пропасть с вкладки и перестать подниматься.
        Assert.False(new AppSettings().LocalProxyCardShown);
        Assert.True(new AppSettings { LocalProxyOn = true }.LocalProxyActive);

        // Спрятанная карточка гасит и прокси: выключить его было бы негде.
        Assert.False(new AppSettings { LocalProxyOn = true, LocalProxyCard = false }.LocalProxyActive);
    }

    [Fact]
    public void Warp_card_is_shown_by_default_and_always_while_warp_is_on()
    {
        Assert.True(new AppSettings().WarpCardShown);
        Assert.False(new AppSettings { WarpCard = false }.WarpCardShown);
        Assert.True(new AppSettings { WarpCard = false, WarpEnabled = true }.WarpCardShown);
    }

    [Fact]
    public void Card_settings_survive_save_and_load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-settings-{Guid.NewGuid():N}.json");

        try
        {
            new AppSettings { LocalProxyCard = true, WarpCard = false }.Save(path);
            var back = AppSettings.Load(path);

            Assert.True(back.LocalProxyCard);
            Assert.False(back.WarpCard);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Port_held_by_our_running_engine_is_not_called_busy()
    {
        // 06.11: прокси поднят, карточку спрятали и включили снова — свой же
        // порт окно назвало «занят другой программой».
        var (json, _) = Compile(47123, Vless);
        var path = Path.Combine(Path.GetTempPath(), $"nz-engine-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);

        try
        {
            Assert.True(LocalProxy.HeldByEngine(47123, path));
            Assert.False(LocalProxy.HeldByEngine(47124, path));
            Assert.False(LocalProxy.HeldByEngine(47123, path + ".нет"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Default_port_is_acceptable()
    {
        Assert.Null(LocalProxy.Problem(LocalProxy.DefaultPort, new AppSettings(), ownNow: true));
    }
}
