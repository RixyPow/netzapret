using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Пулы серверов (владелец 19.10, идеи №6): маршрут «через VPN» ведёт в свою
/// группу выбора, а не в общий автоподбор.
/// </summary>
public sealed class ServerPoolsTests
{
    private static ProxyServer Server(string tag, string host = "") => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = tag,
        Host = host.Length > 0 ? host : tag.ToLowerInvariant() + ".example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Transport = "udp",
        Security = "tls",
        Sni = tag.ToLowerInvariant() + ".example.com",
    };

    private static readonly ServerPool Russia = new("ru000001", "Россия", ["RU", "RU2"]);

    private static string Rules(string server) => $"""
        mode: selective
        rules:
          - match: domain
            value: "*.gosuslugi.ru"
            mode: proxy
            server: "{server}"
          - match: domain
            value: "*.rutracker.org"
            mode: proxy
        """;

    private static (JsonElement Root, string Json) Compile(string yaml, SingBoxOptions options, params ProxyServer[] servers)
    {
        var engine = RuleSetLoader.Load(yaml);
        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            servers.Length > 0 ? servers : [Server("NL"), Server("RU"), Server("RU2")],
            options);

        return (JsonDocument.Parse(result.Json).RootElement.Clone(), result.Json);
    }

    private static JsonElement? Outbound(JsonElement root, string tag) =>
        root.GetProperty("outbounds").EnumerateArray()
            .Where(o => o.GetProperty("tag").GetString() == tag)
            .Cast<JsonElement?>()
            .FirstOrDefault();

    private static List<string> Members(JsonElement group) =>
        [.. group.GetProperty("outbounds").EnumerateArray().Select(m => m.GetString()!)];

    private static string OutboundOf(JsonElement root, string domain) =>
        root.GetProperty("route").GetProperty("rules").EnumerateArray()
            .Single(r => r.TryGetProperty("domain_suffix", out var d)
                && d.EnumerateArray().Any(e => e.GetString() == domain))
            .GetProperty("outbound").GetString()!;

    [Fact]
    public void A_route_into_a_pool_goes_to_its_selector()
    {
        var (root, _) = Compile(Rules("pool:ru000001"), new SingBoxOptions { Pools = [Russia] });

        Assert.Equal("pool-ru000001", OutboundOf(root, "gosuslugi.ru"));
        Assert.Equal("auto", OutboundOf(root, "rutracker.org"));
    }

    [Fact]
    public void The_pool_picks_only_among_its_servers_and_can_fall_back()
    {
        var (root, _) = Compile(Rules("pool:ru000001"), new SingBoxOptions { Pools = [Russia] });

        Assert.Equal(["RU", "RU2"], Members(Outbound(root, "pool-ru000001-latency")!.Value));

        // Пул по умолчанию — куда надзор уводит молчащий пул; прямой — обход.
        var selector = Outbound(root, "pool-ru000001")!.Value;
        Assert.Equal(["pool-ru000001-latency", "RU", "RU2", "auto", "direct"], Members(selector));
        Assert.Equal("pool-ru000001-latency", selector.GetProperty("default").GetString());
    }

    [Fact]
    public void A_pinned_server_of_the_pool_is_its_default()
    {
        var (root, _) = Compile(Rules("pool:ru000001"), new SingBoxOptions { Pools = [Russia with { Preferred = "RU2" }] });

        Assert.Equal("RU2", Outbound(root, "pool-ru000001")!.Value.GetProperty("default").GetString());
    }

    [Fact]
    public void A_pool_without_routes_costs_nothing()
    {
        // Группа без маршрутов мерила бы серверы зря.
        var (root, _) = Compile(Rules("auto"), new SingBoxOptions { Pools = [Russia] });

        Assert.Null(Outbound(root, "pool-ru000001"));
        Assert.Null(Outbound(root, "pool-ru000001-latency"));
    }

    [Fact]
    public void A_pool_whose_servers_left_the_subscriptions_falls_back_to_default()
    {
        var (root, _) = Compile(Rules("pool:ru000001"), new SingBoxOptions { Pools = [Russia] }, Server("NL"));

        Assert.Null(Outbound(root, "pool-ru000001"));
        Assert.Equal("auto", OutboundOf(root, "gosuslugi.ru"));
    }

    [Fact]
    public void An_unknown_pool_falls_back_to_default()
    {
        var (root, _) = Compile(Rules("pool:deleted1"), new SingBoxOptions { Pools = [Russia] });

        Assert.Equal("auto", OutboundOf(root, "gosuslugi.ru"));
    }

    [Fact]
    public void Dead_servers_leave_the_pool_autopick_but_not_to_emptiness()
    {
        var (some, _) = Compile(Rules("pool:ru000001"), new SingBoxOptions
        {
            Pools = [Russia],
            DeadServerTags = new HashSet<string> { "RU" },
        });

        Assert.Equal(["RU2"], Members(Outbound(some, "pool-ru000001-latency")!.Value));

        var (all, _) = Compile(Rules("pool:ru000001"), new SingBoxOptions
        {
            Pools = [Russia],
            DeadServerTags = new HashSet<string> { "RU", "RU2" },
        });

        Assert.Equal(["RU", "RU2"], Members(Outbound(all, "pool-ru000001-latency")!.Value));
    }

    [Fact]
    public void Foreign_only_does_not_empty_a_domestic_pool()
    {
        // «Только зарубежные» — про пул по умолчанию; пул «Россия» заводят ради своих.
        var (root, _) = Compile(Rules("pool:ru000001"), new SingBoxOptions
        {
            Pools = [new ServerPool("ru000001", "Россия", ["🇷🇺 Москва"])],
            ForeignExitsOnly = true,
        }, Server("🇳🇱 Амстердам"), Server("🇷🇺 Москва"));

        Assert.Equal(["🇷🇺 Москва"], Members(Outbound(root, "pool-ru000001-latency")!.Value));
    }

    [Fact]
    public void The_default_route_can_lead_into_a_pool()
    {
        var yaml = """
            mode: selective
            default:
              mode: proxy
              server: "pool:ru000001"
            rules: []
            """;

        var (root, _) = Compile(yaml, new SingBoxOptions { Pools = [Russia] });

        Assert.Equal("pool-ru000001", root.GetProperty("route").GetProperty("final").GetString());
    }

    [Fact]
    public void The_engine_accepts_the_pool_groups()
    {
        var (_, json) = Compile(Rules("pool:ru000001"), new SingBoxOptions { Pools = [Russia with { Preferred = "RU" }] });

        var (ok, said) = SingBoxCheck.Run(json);
        Assert.True(ok, said);
    }

    [Fact]
    public void Rule_server_round_trips()
    {
        Assert.Equal("ab12cd34", ServerPools.IdOf(ServerPools.RuleServer("ab12cd34")));
        Assert.Null(ServerPools.IdOf("auto"));
        Assert.Null(ServerPools.IdOf("pool:"));
        Assert.Null(ServerPools.IdOf(null));
    }

    [Fact]
    public void Taking_the_pinned_server_out_unpins_it()
    {
        IReadOnlyList<ServerPool> pools = [Russia with { Preferred = "RU" }];

        var after = ServerPools.Take(pools, "ru000001", "RU");

        Assert.Equal(["RU2"], after[0].Servers);
        Assert.Null(after[0].Preferred);
    }

    [Fact]
    public void A_server_goes_into_a_pool_once()
    {
        IReadOnlyList<ServerPool> pools = [Russia];

        Assert.Equal(["RU", "RU2"], ServerPools.Put(pools, "ru000001", "RU")[0].Servers);
        Assert.Equal(["RU", "RU2", "NL"], ServerPools.Put(pools, "ru000001", "NL")[0].Servers);
    }

    [Fact]
    public void A_wholly_dead_pool_and_pinned_servers_stay_in_the_config()
    {
        ServerPool[] pools =
        [
            Russia,
            new("fast0001", "Скоростной", ["NL", "FI"], Preferred: "FI"),
        ];

        var keep = ServerPools.KeptDespiteDeath(pools, ["NL", "FI", "RU", "RU2"], ["RU", "RU2", "FI"]);

        Assert.Equal(["FI", "RU", "RU2"], keep.Order());
    }

    [Fact]
    public void The_own_routes_file_keeps_the_pool()
    {
        // До пулов файл поля сервера не хранил: пул пропал бы при первой записи.
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-pool-rules-{Guid.NewGuid():N}.yaml");

        try
        {
            var file = UserRulesFile.Load(path);
            file.Set(MatchKind.Domain, "*.gosuslugi.ru", RoutingMode.Proxy, server: "pool:ru000001");
            file.Set(MatchKind.Domain, "*.example.com", RoutingMode.Desync, server: "pool:ru000001");
            file.Save();

            var read = UserRulesFile.Load(path);

            Assert.Equal("pool:ru000001", read.Entries.Single(e => e.Value == "*.gosuslugi.ru").Server);
            Assert.Null(read.Entries.Single(e => e.Value == "*.example.com").Server);

            var engine = RuleSetLoader.Load(File.ReadAllText(path));
            Assert.Equal("pool:ru000001", engine.RuleSet.Rules.Single(r => r.Value == "*.gosuslugi.ru").Server);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Pools_survive_the_settings_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-pools-{Guid.NewGuid():N}.json");

        try
        {
            (new AppSettings { Pools = [Russia with { Preferred = "RU" }], DeadPoolToDefault = false }).Save(path);

            var read = AppSettings.Load(path);

            Assert.Equal("Россия", read.Pools.Single().Name);
            Assert.Equal(["RU", "RU2"], read.Pools.Single().Servers);
            Assert.Equal("RU", read.Pools.Single().Preferred);
            Assert.False(read.DeadPoolToDefault);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
