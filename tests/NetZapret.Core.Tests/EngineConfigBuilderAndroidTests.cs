using System.IO;
using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;
using static NetZapret.Core.Tests.EngineConfigBuilderFixtures;

namespace NetZapret.Core.Tests;

/// <summary>
/// Конфиг движка для телефона (27.10, android/DESIGN.md): без слушателей на петле,
/// пины своим файлом, «десинк» — в ByeDPI или нарезкой.
/// </summary>
/// <remarks>
/// На телефоне не проверялось ничего — только то, что конфиг таков, каким
/// его задумали. Что его принимает сам движок, сверено <c>sing-box check</c>
/// Windows-сборкой того же форка (27.10).
/// </remarks>
public sealed class EngineConfigBuilderAndroidTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nz-android-{Guid.NewGuid():N}");

    public EngineConfigBuilderAndroidTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string HostsPath => Path.Combine(_root, "config", "hosts");

    private AndroidEngine Engine(DesyncSocks? socks = null, bool root = false, TlsFragment fragment = TlsFragment.Records,
        IReadOnlyList<string>? pass = null, bool socksDefault = true, bool fragmentDefault = false) => new()
    {
        HostsFilePath = HostsPath,
        Root = root,
        ExcludePackages = ["org.netzapret.android", "ru.sberbankmobile"],
        Socks = socks,
        DesyncFragment = fragment,
        DesyncPassHosts = pass ?? [],
        DesyncSocksDefault = socksDefault,
        DesyncFragmentDefault = fragmentDefault,
    };

    private static readonly DesyncSocks Socks = new(41234, "nz", "secret-for-test");

    private JsonObject Compile(AndroidEngine engine, bool narrow = false, bool withServers = true, RuleSet? rules = null,
        IReadOnlyList<string>? shieldPins = null)
    {
        var settings = Settings();
        var host = AndroidHost(_root, engine);

        var options = EngineConfigBuilder.TunnelOptions(settings, host, new TunnelInputs(
            RouterSelection(settings), narrow, false, ["91.108.4.0/22"], [], Pinned(), Overrides(),
            [], Health(_root), null, ["https://panel.example.invalid/sub/token"])
        {
            ShieldPins = shieldPins ?? [],
        });

        var json = new SingBoxConfigCompiler().Compile(rules ?? RuleFixture(), withServers ? Servers() : [], options).Json;

        return JsonNode.Parse(json)!.AsObject();
    }

    private static IEnumerable<JsonObject> RouteRules(JsonObject config) =>
        config["route"]!["rules"]!.AsArray().Select(r => r!.AsObject());

    private static bool Has(JsonObject rule, string key, string value) =>
        rule[key] is JsonArray array && array.Any(v => v!.GetValue<string>() == value);

    // ── Без слушателей на петле и без Windows-полей

    [Fact]
    public void No_loopback_listener_no_clash_api_no_health_in_no_interface_name()
    {
        var config = Compile(Engine(Socks));
        var text = config.ToJsonString();

        var inbounds = config["inbounds"]!.AsArray();
        Assert.Single(inbounds);
        Assert.Equal("tun", inbounds[0]!["type"]!.GetValue<string>());
        Assert.Null(inbounds[0]!["listen"]);
        Assert.Null(inbounds[0]!["interface_name"]);

        Assert.Null(config["experimental"]!["clash_api"]);
        Assert.DoesNotContain("health-in", text);
        Assert.DoesNotContain("local-in", text);
        Assert.DoesNotContain("external_controller", text);
        Assert.DoesNotContain("listen_port", text);

        // 127.0.0.1 — только адрес ByeDPI, куда движок сам ходит; слушает там ByeDPI с паролем.
        var loopback = config["outbounds"]!.AsArray()
            .Where(o => o!["server"]?.GetValue<string>() == "127.0.0.1")
            .Select(o => o!["tag"]!.GetValue<string>())
            .ToList();
        Assert.Equal(new[] { SingBoxOptions.DesyncTag }, loopback);
    }

    [Fact]
    public void Without_tun_the_phone_build_refuses()
    {
        var options = new SingBoxOptions { Platform = SingBoxPlatform.Android, UseTun = false };

        Assert.Throws<ArgumentException>(() => new SingBoxConfigCompiler().Compile(RuleFixture(), [], options));
    }

    [Fact]
    public void Cache_file_is_always_written_with_a_full_path()
    {
        var engine = Engine(Socks);
        var settings = Settings() with { FakeIpMemory = false };

        var options = EngineConfigBuilder.TunnelOptions(settings, AndroidHost(_root, engine), new TunnelInputs(
            RouterSelection(settings), false, false, [], [], new PinnedProxy([], []),
            new Dictionary<string, string>(), [], Health(_root), null, []));

        var config = JsonNode.Parse(new SingBoxConfigCompiler().Compile(RuleFixture(), Servers(), options).Json)!;
        var cache = config["experimental"]!["cache_file"]!;

        Assert.True(cache["enabled"]!.GetValue<bool>());
        Assert.True(Path.IsPathRooted(cache["path"]!.GetValue<string>()));
        Assert.Null(cache["store_fakeip"]);
    }

    [Fact]
    public void Packages_go_to_the_tun_and_never_both_lists()
    {
        var tun = Compile(Engine(Socks))["inbounds"]![0]!.AsObject();

        Assert.True(Has(tun, "exclude_package", "org.netzapret.android"));
        Assert.True(Has(tun, "exclude_package", "ru.sberbankmobile"));
        Assert.Null(tun["include_package"]);

        var both = new SingBoxOptions
        {
            Platform = SingBoxPlatform.Android,
            ExcludePackages = ["a.b"],
            IncludePackages = ["c.d"],
        };

        Assert.Throws<ArgumentException>(() => new SingBoxConfigCompiler().Compile(RuleFixture(), [], both));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_root_the_tun_takes_everything(bool root)
    {
        // Охват «выборочный» в настройках. Без root десинк — в движке, и до
        // него доходит только вошедшее в TUN; с root nfqws2 рядом — как Windows.
        // WARP — чтобы сборка не ходила за подпиской в сеть.
        var settings = Settings() with
        {
            ProxyOnly = true,
            WarpEnabled = true,
            ProxyConfigPath = Path.Combine(_root, "runtime", "singbox.json"),
        };

        var built = await EngineConfigBuilder.BuildTunnelAsync(settings, AndroidHost(_root, Engine(Socks, root: root)), CancellationToken.None);
        Assert.True(built.Ok, built.Message);

        var tun = JsonNode.Parse(built.Compiled!.Json)!["inbounds"]![0]!.AsObject();

        if (root)
            Assert.NotNull(tun["route_address"]);
        else
            Assert.Null(tun["route_address"]);
    }

    // ── Пины своим файлом

    [Fact]
    public void Pins_are_asked_first_from_the_app_hosts_file()
    {
        var dns = Compile(Engine(Socks))["dns"]!.AsObject();

        var server = dns["servers"]!.AsArray().Single(s => s!["tag"]!.GetValue<string>() == "nz-hosts")!.AsObject();
        Assert.Equal("hosts", server["type"]!.GetValue<string>());
        Assert.Equal(HostsPath, server["path"]![0]!.GetValue<string>());

        // Сразу за сигналом Firefox — раньше подставленных адресов и fakeip,
        // как системный hosts на Windows.
        var rules = dns["rules"]!.AsArray();
        Assert.Equal(SingBoxConfigCompiler.FirefoxCanary, rules[0]!["domain"]![0]!.GetValue<string>());

        var pins = rules[1]!.AsObject();
        Assert.Equal("nz-hosts", pins["preferred_by"]![0]!.GetValue<string>());
        Assert.Equal("nz-hosts", pins["server"]!.GetValue<string>());
        Assert.Equal(new[] { "A", "AAAA" }, pins["query_type"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.True(pins["disable_cache"]!.GetValue<bool>());

        Assert.Equal("pinned", rules[2]!["server"]!.GetValue<string>());
    }

    /// <summary>
    /// «Гибрид» на телефоне: fakeip только именам «через VPN», прочим — настоящий
    /// адрес, иначе правила по адресам не совпадали ни с одним соединением по имени.
    /// </summary>
    [Fact]
    public void Phone_hybrid_gives_fakeip_only_to_vpn_names()
    {
        var config = Compile(Engine(Socks));
        var dns = config["dns"]!.AsObject();
        var rules = dns["rules"]!.AsArray().Select(r => r!.AsObject()).ToList();

        // Нет правила «fakeip каждому имени» — без условия по имени.
        Assert.DoesNotContain(rules, r => r["server"]?.GetValue<string>() == "fake"
            && r["domain"] is null && r["domain_suffix"] is null && r["domain_regex"] is null);

        // Имена «через VPN» — fakeip, остальное — final remote.
        Assert.Contains(rules, r => r["server"]?.GetValue<string>() == "fake" && Has(r, "domain", "chatgpt.com"));
        Assert.Contains(rules, r => r["server"]?.GetValue<string>() == "fake" && Has(r, "domain_suffix", "discord.com"));
        Assert.DoesNotContain(rules, r => Has(r, "domain", "rutracker.org") || Has(r, "domain_suffix", "gosuslugi.ru"));
        Assert.Equal("remote", dns["final"]!.GetValue<string>());
        Assert.True(dns["reverse_mapping"]!.GetValue<bool>());

        // Умолчание «через VPN» — fakeip каждому, как прежде.
        var proxyDefault = RuleFixture() with { DefaultMode = RoutingMode.Proxy };
        var all = Compile(Engine(Socks), rules: proxyDefault)["dns"]!["rules"]!.AsArray();
        Assert.Contains(all, r => r!["server"]?.GetValue<string>() == "fake" && r["domain_suffix"] is null && r["domain"] is null);
    }

    /// <summary>
    /// «Напрямую» и нарезка соединяются самим движком: имя без адреса — правилами
    /// DNS (resolve), а не резолвером по умолчанию, при «авто» — DNS оператора.
    /// </summary>
    [Fact]
    public void Direct_and_fragmented_names_are_resolved_by_dns_rules()
    {
        var rules = RouteRules(Compile(Engine(socks: null))).ToList();

        // Перед «напрямую» по имени — resolve с тем же условием, вплотную.
        int direct = rules.FindIndex(r => Has(r, "domain_suffix", "gosuslugi.ru") && r["outbound"] is not null);
        Assert.True(direct > 0);
        Assert.Equal("resolve", rules[direct - 1]["action"]!.GetValue<string>());
        Assert.True(Has(rules[direct - 1], "domain_suffix", "gosuslugi.ru"));

        // Перед нарезкой — тоже.
        int cut = rules.FindIndex(r => Has(r, "domain", "rutracker.org") && r["tls_record_fragment"] is not null);
        Assert.True(cut > 0);
        Assert.Equal("resolve", rules[cut - 1]["action"]!.GetValue<string>());
        Assert.True(Has(rules[cut - 1], "domain", "rutracker.org"));

        // Правилам VPN и по одним адресам — нет: в туннель уходит имя, а у адреса
        // разрешать нечего.
        Assert.DoesNotContain(rules, r => Has(r, "domain", "chatgpt.com") && r["action"]?.GetValue<string>() == "resolve");
        Assert.DoesNotContain(rules, r => Has(r, "ip_cidr", "149.154.160.0/20") && r["action"]?.GetValue<string>() == "resolve");

        // Невыведенное уходит в direct — последним правилом общий resolve.
        Assert.Equal("resolve", rules[^1]["action"]!.GetValue<string>());
        Assert.Equal(2, rules[^1].Count);
    }

    /// <summary>
    /// Пин ведёт на посредника: на телефоне без root его имя — напрямую, мимо
    /// ByeDPI и нарезки, как щитом winws2 (замер 06.10: со щитом 20 из 20, под рецептом 0 из 10).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Pinned_names_bypass_desync_inside_the_engine(bool byeDpi)
    {
        var engine = byeDpi ? Engine(Socks) : Engine(socks: null, fragmentDefault: true);
        var rules = RouteRules(Compile(engine, shieldPins: ["notion.so", "rutracker.org"])).ToList();

        int shield = rules.FindIndex(r => Has(r, "domain", "notion.so"));
        Assert.True(shield >= 0);
        Assert.Equal("direct", rules[shield]["outbound"]!.GetValue<string>());
        Assert.True(Has(rules[shield], "domain", "rutracker.org"));

        // Раньше правил человека («десинк» rutracker.org) и после правила пинов «через VPN».
        int desync = rules.FindIndex(r => Has(r, "domain", "rutracker.org") && r["action"] is not null);
        int pinnedVpn = rules.FindIndex(r => r["type"]?.GetValue<string>() == "logical" && r["outbound"]?.GetValue<string>() == "auto");
        Assert.True(pinnedVpn >= 0 && pinnedVpn < shield && shield < desync, $"VPN {pinnedVpn}, щит {shield}, десинк {desync}");

        // С root щит делает nfqws2 — в движке правила нет.
        var rooted = RouteRules(Compile(Engine(Socks, root: true), narrow: true, shieldPins: ["notion.so"])).ToList();
        Assert.DoesNotContain(rooted, r => Has(r, "domain", "notion.so"));
    }

    [Fact]
    public void Pins_off_desync_skip_vpn_names_and_marked_pins()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HostsPath)!);
        File.WriteAllText(HostsPath,
            HostsEditor.BlockBegin + "\n"
            + $"157.240.0.35 instagram.com # {HostsEditor.DesyncMark}\n"
            + "193.233.112.67 notion.so\n"
            + HostsEditor.BlockEnd + "\n"
            + "87.228.47.200 chatgpt.com\n"
            + "0.0.0.0 ads.example\n");

        var pins = HostsFile.PinsOffDesync(RuleFixture(), HostsPath);

        // chatgpt.com — «через VPN»: его ведёт правило пинов в туннель.
        Assert.DoesNotContain("chatgpt.com", pins);

        // Пин на адрес самого сервиса десинку открыт (замер 06.10).
        Assert.DoesNotContain("instagram.com", pins);

        Assert.Contains("notion.so", pins);
        Assert.Contains("ads.example", pins);

        Assert.Empty(HostsFile.PinsOffDesync(RuleFixture(), Path.Combine(_root, "нет-файла")));
    }

    [Fact]
    public void Router_dns_on_the_phone_has_no_adapter_binding()
    {
        var settings = Settings() with { UseRouterDns = true, RouterDnsAddress = "192.168.1.1" };
        var engine = Engine(Socks);

        var options = EngineConfigBuilder.TunnelOptions(settings, AndroidHost(_root, engine), new TunnelInputs(
            new RouterDnsConnection.Selection(settings, null, null), false, false, [], [],
            new PinnedProxy([], []), new Dictionary<string, string>(), [], Health(_root), null, []));

        var remote = JsonNode.Parse(new SingBoxConfigCompiler().Compile(RuleFixture(), Servers(), options).Json)!
            ["dns"]!["servers"]![0]!.AsObject();

        Assert.Equal("udp", remote["type"]!.GetValue<string>());
        Assert.Equal(53, remote["server_port"]!.GetValue<int>());
        Assert.Null(remote["bind_interface"]);
    }

    // ── «Десинк» в ByeDPI

    [Fact]
    public void Desync_goes_to_ByeDPI_after_resolving_and_QUIC_is_refused()
    {
        var config = Compile(Engine(Socks));

        var socks = config["outbounds"]!.AsArray().Single(o => o!["tag"]!.GetValue<string>() == "desync")!.AsObject();
        Assert.Equal("socks", socks["type"]!.GetValue<string>());
        Assert.Equal("127.0.0.1", socks["server"]!.GetValue<string>());
        Assert.Equal(41234, socks["server_port"]!.GetValue<int>());
        Assert.Equal("5", socks["version"]!.GetValue<string>());
        Assert.Equal("nz", socks["username"]!.GetValue<string>());
        Assert.Equal("secret-for-test", socks["password"]!.GetValue<string>());
        Assert.Equal("tcp", socks["network"]!.GetValue<string>());

        var rules = RouteRules(config).ToList();
        int at = rules.FindIndex(r => Has(r, "domain", "rutracker.org") && Has(r, "network", "udp"));

        Assert.True(at > 0);
        Assert.Equal("reject", rules[at]["action"]!.GetValue<string>());
        Assert.Equal(443, rules[at]["port"]![0]!.GetValue<int>());

        Assert.True(Has(rules[at + 1], "domain", "rutracker.org"));
        Assert.True(Has(rules[at + 1], "network", "tcp"));
        Assert.Equal("resolve", rules[at + 1]["action"]!.GetValue<string>());
        Assert.Equal("prefer_ipv4", rules[at + 1]["strategy"]!.GetValue<string>());

        Assert.True(Has(rules[at + 2], "domain", "rutracker.org"));
        Assert.Equal("desync", rules[at + 2]["outbound"]!.GetValue<string>());

        // Остальное (UDP) — напрямую, как было: ByeDPI его не берёт.
        Assert.True(Has(rules[at + 3], "domain", "rutracker.org"));
        Assert.Null(rules[at + 3]["network"]);
        Assert.Equal("direct", rules[at + 3]["outbound"]!.GetValue<string>());
    }

    [Fact]
    public void Address_rules_go_to_ByeDPI_without_resolving()
    {
        var rules = RouteRules(Compile(Engine(Socks))).ToList();
        var games = rules.Where(r => Has(r, "ip_cidr", "185.40.64.0/22")).ToList();

        Assert.DoesNotContain(games, r => r["action"]?.GetValue<string>() == "resolve");
        Assert.Contains(games, r => r["outbound"]?.GetValue<string>() == "desync");
        Assert.Contains(games, r => r["action"]?.GetValue<string>() == "reject");
    }

    [Fact]
    public void Default_desync_goes_to_ByeDPI_and_the_registry_is_left_to_it()
    {
        var config = Compile(Engine(Socks));
        var rules = RouteRules(config).ToList();
        var tail = rules.TakeLast(3).ToList();

        Assert.Equal("reject", tail[0]["action"]!.GetValue<string>());
        Assert.True(Has(tail[0], "network", "udp"));
        Assert.Equal("resolve", tail[1]["action"]!.GetValue<string>());
        Assert.Equal("desync", tail[2]["outbound"]!.GetValue<string>());
        Assert.Equal("direct", config["route"]!["final"]!.GetValue<string>());

        // Имена реестра, которых ниже никто не перехватит, ведёт туда же
        // умолчание — в конфиге они лишние, как и на Windows.
        Assert.DoesNotContain("kinozal.tv", config.ToJsonString());
    }

    /// <summary>
    /// Реестр над российскими зонами: его имена в .ru не отдаются «*.ru — напрямую»
    /// (разбор 27.10: tvrain.ru шёл бы мимо ByeDPI).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registry_names_a_lower_direct_zone_would_take_stay_in_desync(bool byeDpi)
    {
        var engine = byeDpi ? Engine(Socks) : Engine(socks: null, fragmentDefault: true);
        var config = Compile(engine);
        var rules = RouteRules(config).ToList();

        // Один набор правил, и в нём — только имена, которые ниже забрала бы зона.
        var set = Assert.Single(config["route"]!["rule_set"]!.AsArray())!.AsObject();
        Assert.Equal("inline", set["type"]!.GetValue<string>());

        var names = set["rules"]![0]!["domain_suffix"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "tvrain.ru" }, names);

        var tag = set["tag"]!.GetValue<string>();
        int zone = rules.FindIndex(r => Has(r, "domain_suffix", "ru") && r["outbound"]?.GetValue<string>() == "direct");

        int desync = byeDpi
            ? rules.FindIndex(r => Has(r, "rule_set", tag) && r["outbound"]?.GetValue<string>() == "desync")
            : rules.FindIndex(r => Has(r, "rule_set", tag) && r["tls_record_fragment"] is not null);

        Assert.True(desync >= 0 && zone > desync, $"десинк {desync}, зона {zone}");

        // Перед ByeDPI и нарезкой — разрешение по правилам DNS.
        Assert.Contains(rules.Take(desync), r => Has(r, "rule_set", tag) && r["action"]?.GetValue<string>() == "resolve");
    }

    /// <summary>С root реестр, как на Windows, держат дыры щита nfqws2, и в конфиге он лишний.</summary>
    [Fact]
    public void Root_still_drops_the_registry_whole()
    {
        var root = Compile(Engine(Socks, root: true), narrow: true);

        Assert.Null(root["route"]!["rule_set"]);
        Assert.DoesNotContain("tvrain.ru", root["route"]!.ToJsonString());
    }

    [Fact]
    public void Without_default_desync_the_registry_is_written_out()
    {
        var config = Compile(Engine(Socks, socksDefault: false));
        var rules = RouteRules(config).ToList();

        // Целиком, одним набором правил: четыре правила «десинка» ссылаются
        // на него, а не несут 117 тысяч имён каждое.
        var set = Assert.Single(config["route"]!["rule_set"]!.AsArray())!.AsObject();
        var names = set["rules"]![0]!["domain_suffix"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "rutracker.org", "kinozal.tv", "linkedin.com", "nnmclub.to", "tvrain.ru" }, names);

        var tag = set["tag"]!.GetValue<string>();
        Assert.Contains(rules, r => Has(r, "rule_set", tag) && r["outbound"]?.GetValue<string>() == "desync");
        Assert.DoesNotContain(rules, r => Has(r, "domain_suffix", "kinozal.tv"));
        Assert.DoesNotContain(rules, r => r["action"] is null && r["outbound"]?.GetValue<string>() == "desync"
            && r.Count == 2 && Has(r, "network", "tcp"));
    }

    [Fact]
    public void Pass_names_of_the_preset_never_reach_ByeDPI()
    {
        var rules = RouteRules(Compile(Engine(Socks, pass: ["github.com"]))).ToList();

        var toByeDpi = rules.Where(r => r["outbound"]?.GetValue<string>() == "desync").ToList();
        Assert.NotEmpty(toByeDpi);

        // Правила «десинк»: логическое «и» с обращённым списком pass.
        foreach (var rule in toByeDpi.Where(r => r["type"]?.GetValue<string>() == "logical"))
        {
            var guard = rule["rules"]![1]!.AsObject();
            Assert.True(guard["invert"]!.GetValue<bool>());
            Assert.True(Has(guard, "domain_suffix", "github.com"));
        }

        Assert.Contains(toByeDpi, r => r["type"]?.GetValue<string>() == "logical");

        // Хвост умолчания: pass — напрямую, раньше ByeDPI.
        int passAt = rules.FindIndex(r => Has(r, "domain_suffix", "github.com") && r["outbound"]?.GetValue<string>() == "direct");
        int lastDesync = rules.FindLastIndex(r => r["outbound"]?.GetValue<string>() == "desync");
        Assert.True(passAt >= 0 && passAt < lastDesync);
    }

    [Fact]
    public void Proxy_rule_above_desync_keeps_its_name()
    {
        // Первое совпадение решает: «через VPN» выше «десинка» отдаёт имя туннелю.
        var rules = RouteRules(Compile(Engine(Socks))).ToList();

        int vpn = rules.FindIndex(r => Has(r, "domain", "chatgpt.com"));
        int desync = rules.FindIndex(r => Has(r, "domain", "rutracker.org"));

        Assert.True(vpn >= 0 && vpn < desync);
        Assert.Equal("auto", rules[vpn]["outbound"]!.GetValue<string>());
    }

    [Fact]
    public void Server_named_desync_does_not_take_the_ByeDPI_tag()
    {
        var servers = new[]
        {
            new Subscriptions.ProxyServer
            {
                Protocol = Subscriptions.ProxyProtocol.Trojan,
                Tag = "desync",
                Host = "x.example.net",
                Port = 443,
                Credential = "PLACEHOLDER",
                Security = "tls",
            },
        };

        var options = new SingBoxOptions
        {
            Platform = SingBoxPlatform.Android,
            DesyncSocks = Socks,
        };

        var config = JsonNode.Parse(new SingBoxConfigCompiler().Compile(RuleFixture(), servers, options).Json)!;
        var tags = config["outbounds"]!.AsArray().Select(o => o!["tag"]!.GetValue<string>()).ToList();

        Assert.Single(tags, t => t == "desync");
        Assert.Contains("desync #2", tags);
    }

    // ── «Десинк» нарезкой (без ByeDPI) и с root

    [Fact]
    public void Without_ByeDPI_desync_rules_get_record_fragmenting_and_only_they()
    {
        var rules = RouteRules(Compile(Engine(socks: null))).ToList();

        var fragmented = rules.Where(r => r["tls_record_fragment"] is not null).ToList();
        Assert.Contains(fragmented, r => Has(r, "domain", "rutracker.org"));
        Assert.All(fragmented, r => Assert.Equal("direct", r["outbound"]!.GetValue<string>()));
        Assert.DoesNotContain(rules, r => r["tls_fragment"] is not null);
        Assert.DoesNotContain(fragmented, r => Has(r, "domain", "chatgpt.com") || Has(r, "domain_suffix", "gosuslugi.ru"));
        Assert.DoesNotContain(rules, r => r["outbound"]?.GetValue<string>() == "desync");

        // Умолчание нарезкой — только по выключателю: без него хвоста нет.
        Assert.DoesNotContain(rules, r => r["action"]?.GetValue<string>() == "route-options");
    }

    [Fact]
    public void Packet_fragmenting_carries_its_delay_and_never_both_kinds()
    {
        var engine = Engine(socks: null, fragment: TlsFragment.Packets, fragmentDefault: true) with
        {
            DesyncFragmentFallbackDelay = "300ms",
        };

        var rules = RouteRules(Compile(engine)).ToList();

        var rule = rules.Single(r => Has(r, "domain", "rutracker.org") && r["outbound"] is not null);
        Assert.True(rule["tls_fragment"]!.GetValue<bool>());
        Assert.Equal("300ms", rule["tls_fragment_fallback_delay"]!.GetValue<string>());
        Assert.DoesNotContain(rules, r => r["tls_fragment"] is not null && r["tls_record_fragment"] is not null);

        var last = rules[^1];
        Assert.Equal("route-options", last["action"]!.GetValue<string>());
        Assert.True(last["tls_fragment"]!.GetValue<bool>());
    }

    [Fact]
    public void With_root_desync_stays_plain_direct()
    {
        var config = Compile(Engine(Socks, root: true), narrow: true);
        var rules = RouteRules(config).ToList();

        Assert.DoesNotContain(config["outbounds"]!.AsArray(), o => o!["tag"]!.GetValue<string>() == "desync");
        Assert.Equal("direct", rules.Single(r => Has(r, "domain", "rutracker.org"))["outbound"]!.GetValue<string>());
        Assert.DoesNotContain(rules, r => r["action"]?.GetValue<string>() is "resolve" or "reject" && r["inbound"] is null);

        // Выборочный перехват с root — как на Windows, и DNS сети VPN в нём явно.
        var tun = config["inbounds"]![0]!.AsObject();
        Assert.True(Has(tun, "route_address", "172.19.0.2/32"));
    }

    // ── Программы

    [Fact]
    public void Process_rules_become_package_rules_or_drop()
    {
        var rules = RouteRules(Compile(Engine(Socks))).ToList();

        Assert.Contains(rules, r => Has(r, "package_name", "org.telegram.messenger"));
        Assert.DoesNotContain(rules, r => r["process_name"] is not null || r["process_path"] is not null);

        // Правило по пакету ловит и соединения с именем (fakeip): перед
        // ByeDPI их тоже разрешает движок.
        Assert.Contains(rules, r => Has(r, "package_name", "org.telegram.messenger")
            && r["action"]?.GetValue<string>() == "resolve");
    }

    [Theory]
    [InlineData("org.telegram.messenger", true)]
    [InlineData("com.discord", true)]
    [InlineData("telegram.exe", false)]
    [InlineData("Telegram", false)]
    [InlineData(@"C:\Games\game.exe", false)]
    [InlineData("/usr/bin/curl", false)]
    public void What_looks_like_a_package(string value, bool package) =>
        Assert.Equal(package, SingBoxConfigCompiler.LooksLikePackage(value));

    // ── Что поднимать

    [Fact]
    public void Plan_on_the_phone_without_root_raises_the_engine_for_desync_alone()
    {
        var settings = Settings().With(new EngineChoice { Desync = true, Tunnel = false });

        var phone = EngineConfigBuilder.Plan(settings, AndroidHost(_root, Engine(Socks)));
        Assert.Equal(new EnginePlan(false, false, true), phone);

        var rooted = EngineConfigBuilder.Plan(settings with { DnsVia = DnsRoute.Tunnel },
            AndroidHost(_root, Engine(Socks, root: true)));
        Assert.Equal(new EnginePlan(false, true, false), rooted);

        var windows = EngineConfigBuilder.Plan(settings, WindowsHost(true, []));
        Assert.Equal(new EnginePlan(false, false, false), windows);
    }

    /// <summary>
    /// С root и без туннеля движок ради DNS поднимается при любом пути DNS: иначе
    /// nfqws2 не запускался вовсе — «запускать нечего» (разбор 27.10).
    /// </summary>
    [Fact]
    public void Plan_on_a_rooted_phone_raises_the_dns_engine_for_desync_alone()
    {
        var host = AndroidHost(_root, Engine(Socks, root: true));
        var alone = Settings().With(new EngineChoice { Desync = true, Tunnel = false });

        Assert.Equal(DnsRoute.Auto, alone.DnsVia);
        Assert.Equal(new EnginePlan(false, true, false), EngineConfigBuilder.Plan(alone, host));
        Assert.Equal(new EnginePlan(false, true, false), EngineConfigBuilder.Plan(alone with { DnsVia = DnsRoute.Direct }, host));

        // «Гибрид» без подписки — выхода нет, туннель не нужен.
        var noExit = Settings() with { SubscriptionUrl = null };
        Assert.False(noExit.NeedsProxy);
        Assert.Equal(new EnginePlan(false, true, false), EngineConfigBuilder.Plan(noExit, host));

        // С выходом — туннель, как и прежде; без десинка — ничего.
        Assert.Equal(new EnginePlan(true, false, false), EngineConfigBuilder.Plan(Settings(), host));

        var nothing = Settings().With(new EngineChoice { Desync = false, Tunnel = false });
        Assert.Equal(new EnginePlan(false, false, false), EngineConfigBuilder.Plan(nothing, host));

        // На Windows при одном десинке и «авто» движок ради DNS не нужен — как было.
        Assert.Equal(new EnginePlan(false, false, false), EngineConfigBuilder.Plan(alone, WindowsHost(true, [])));
    }

    [Fact]
    public void Phone_host_without_its_part_is_refused()
    {
        var host = AndroidHost(_root) with { Android = null };

        Assert.Throws<ArgumentException>(() => EngineConfigBuilder.Plan(Settings(), host));
    }

    [Fact]
    public async Task Desync_alone_on_the_phone_builds_a_config_without_exits()
    {
        var settings = Settings().With(new EngineChoice { Desync = true, Tunnel = false }) with
        {
            ProxyConfigPath = Path.Combine(_root, "runtime", "singbox.json"),
            WarpEnabled = true,
        };

        var built = await EngineConfigBuilder.BuildTunnelAsync(settings, AndroidHost(_root, Engine(Socks)), CancellationToken.None);

        Assert.True(built.Ok, built.Message);
        Assert.Empty(built.Compiled!.UsedServers);

        var config = JsonNode.Parse(File.ReadAllText(settings.ProxyConfigPath))!.AsObject();
        var rules = RouteRules(config).ToList();

        // «Через VPN» без выхода — напрямую (перед ним — разрешение по правилам
        // DNS, а не резолвером по умолчанию); «десинк» — в ByeDPI.
        Assert.Equal("direct", rules.Single(r => Has(r, "domain", "chatgpt.com") && r["outbound"] is not null)["outbound"]!.GetValue<string>());
        Assert.Contains(rules, r => Has(r, "domain", "chatgpt.com") && r["action"]?.GetValue<string>() == "resolve");
        Assert.Contains(rules, r => Has(r, "domain", "rutracker.org") && r["outbound"]?.GetValue<string>() == "desync");
        Assert.DoesNotContain("secret-for-test", built.Message);
    }

    [Fact]
    public void Dns_engine_on_the_phone_has_the_pins_file_and_no_keys()
    {
        var settings = Settings();
        var json = DnsEngine.Compile(settings, ["172.19.0.2/32"], new Dictionary<string, string>(), null,
            android: Engine(socks: null, root: true)).Json;

        var config = JsonNode.Parse(json)!.AsObject();

        Assert.Null(config["experimental"]!["clash_api"]);
        Assert.Contains(config["dns"]!["servers"]!.AsArray(), s => s!["tag"]!.GetValue<string>() == "nz-hosts");
        Assert.Single(config["inbounds"]!.AsArray());
    }

    /// <summary>
    /// Каждый вид конфига телефона принимает сам движок — <c>sing-box check</c>
    /// того же форка, Windows-сборкой.
    /// </summary>
    /// <remarks>
    /// Утверждения выше проверяют, что конфиг таков, каким задуман, но не то,
    /// что его примет sing-box: 27.10 в него добавились resolve перед каждым
    /// «напрямую», reverse_mapping и выборочный fakeip — всё это до проверки
    /// движком было только прочитано в исходниках форка.
    /// </remarks>
    [Theory]
    [InlineData("byedpi")]
    [InlineData("byedpi-no-default")]
    [InlineData("records")]
    [InlineData("packets")]
    [InlineData("root")]
    [InlineData("narrow-root")]
    public void Every_phone_config_passes_sing_box_check(string kind)
    {
        var engine = kind switch
        {
            "byedpi" => Engine(Socks, pass: ["riotgames.com"]),
            "byedpi-no-default" => Engine(Socks, socksDefault: false),
            "records" => Engine(fragmentDefault: true),
            "packets" => Engine(fragment: TlsFragment.Packets),
            "root" or "narrow-root" => Engine(root: true),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var config = Compile(engine, narrow: kind == "narrow-root", shieldPins: kind == "root" ? [] : ["pinned.example"]);
        var (ok, said) = SingBoxCheck.Run(config.ToJsonString());

        Assert.True(ok, said);
    }

    [Fact]
    public void Socks_password_is_not_printed()
    {
        Assert.DoesNotContain("secret-for-test", Socks.ToString());
        Assert.DoesNotContain("secret-for-test", Engine(Socks).ToString());
    }
}
