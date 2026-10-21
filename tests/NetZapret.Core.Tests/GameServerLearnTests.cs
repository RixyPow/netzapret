using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Серверы ARC Raiders — из соединений игры в список её части (21.10, обсуждение №32).
/// </summary>
public sealed class GameServerLearnTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-games-{Guid.NewGuid():N}");

    public GameServerLearnTests() => Directory.CreateDirectory(Path.Combine(_root, "config", "lists"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Settings => Path.Combine(_root, "config", "netzapret.json");

    private static LearnedGame Arc => Assert.Single(GameServerLearn.All, g => g.Program == "PioneerGame.exe");

    private static ConnectionEvent Connection(string program, string address, string? host = null) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Protocol = ProtocolKind.Udp,
        RemoteAddress = IPAddress.Parse(address),
        RemotePort = 7777,
        ExecutablePath = @"C:\Games\ARC Raiders\PioneerGame\Binaries\Win64\" + program,
        Hostname = host,
    };

    private ProgramRouteLearner Learner(params LearnedGame[] games) => new(Settings, _root, games: () => games);

    /// <summary>Папка из трёх частей, все выключены до выбора; голос — по-прежнему раньше Epic.</summary>
    [Fact]
    public void Arc_raiders_is_a_folder_of_three_parts_switched_off()
    {
        var arc = Assert.Single(ServiceCatalog.All, s => s.Name == "ARC Raiders");

        Assert.False(arc.Grouping);
        Assert.Equal(
            ["config/lists/arc-raiders.txt", "config/lists/arc-raiders-voice.txt", GameServerLearn.ArcRaidersList],
            arc.Parts.Select(p => p.List));
        Assert.All(arc.Parts, p => Assert.True(p.DefaultOff));

        var servers = Assert.Single(arc.Parts, p => p.List == Arc.List);
        Assert.True(servers.ByAddress);

        // Голос ушёл с полки «Игры» — в каталоге он один.
        Assert.Single(ServiceCatalog.All.SelectMany(s => s.Parts), p => p.List == "config/lists/arc-raiders-voice.txt");
    }

    [Fact]
    public void A_bare_server_address_goes_to_the_part_list_as_its_network()
    {
        var learner = Learner(Arc);

        var learned = learner.Observe(Connection("PioneerGame.exe", "34.88.12.200"));

        Assert.NotNull(learned);
        Assert.Equal(Arc.List, learned.ListPath);
        Assert.Equal("34.88.12.0/24", learned.Value);
        Assert.Null(learner.Observe(Connection("PioneerGame.exe", "34.88.12.7")));

        Assert.Equal(["34.88.12.0/24"], OwnLists.Names(Arc.List, _root));
    }

    /// <summary>Имена игры — в части по именам; на адресе CDN имя бывает чужим.</summary>
    [Fact]
    public void Named_connections_are_not_learned_for_a_game()
    {
        Assert.Null(Learner(Arc).Observe(Connection("PioneerGame.exe", "34.160.204.112", "api-gateway.europe.es-pio.net")));
        Assert.False(File.Exists(Path.Combine(_root, Arc.List)));
    }

    [Fact]
    public void Nothing_is_learned_while_the_game_is_not_active()
    {
        Assert.Null(Learner().Observe(Connection("PioneerGame.exe", "34.88.12.200")));
    }

    /// <summary>Маршрут программы с тем же именем — выбор человека, он и пополняется.</summary>
    [Fact]
    public void A_program_route_of_the_same_name_wins()
    {
        Assert.True(ProgramRoutes.Add("PioneerGame.exe", Settings, Path.Combine(_root, "config", "rules.user.yaml"), _root));

        var learned = Learner(Arc).Observe(Connection("PioneerGame.exe", "34.88.12.200"));

        Assert.Equal(ProgramRoutes.AddressesPath("PioneerGame.exe"), learned?.ListPath);
        Assert.False(File.Exists(Path.Combine(_root, Arc.List)));
    }

    [Fact]
    public void A_game_is_active_only_through_the_vpn_and_with_its_switch_on()
    {
        var rules = UserRulesFile.Load(Path.Combine(_root, "config", "rules.user.yaml"));
        var on = new AppSettings();

        Assert.Empty(GameServerLearn.Active(on, rules.Entries));

        rules.Set(MatchKind.IpSet, Arc.List, RoutingMode.Desync);
        Assert.Empty(GameServerLearn.Active(on, rules.Entries));

        rules.Set(MatchKind.IpSet, Arc.List, RoutingMode.Proxy);
        Assert.Equal([Arc], GameServerLearn.Active(on, rules.Entries));
        Assert.Empty(GameServerLearn.Active(on with { LearnArcRaidersServers = false }, rules.Entries));
    }
}
