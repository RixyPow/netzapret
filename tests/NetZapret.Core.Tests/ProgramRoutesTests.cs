using System.Net;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Маршрут программы — папка из двух своих списков, пополняемых наблюдением (13.10).
/// </summary>
public sealed class ProgramRoutesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-programs-{Guid.NewGuid():N}");

    public ProgramRoutesTests() => Directory.CreateDirectory(Path.Combine(_root, "config"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Settings => Path.Combine(_root, "config", "netzapret.json");

    private string Rules => Path.Combine(_root, "config", "rules.user.yaml");

    private string Full(string value) => Path.Combine(_root, value);

    private void Add(string program)
    {
        Assert.True(ProgramRoutes.Add(program, Settings, Rules, _root));
    }

    [Theory]
    [InlineData("Diablo IV.exe", "Diablo IV.exe")]
    [InlineData(@"C:\Games\R.E.P.O\REPO.exe", "REPO.exe")]
    [InlineData("\"game.EXE\"", "game.EXE")]
    [InlineData("notepad", null)]
    [InlineData(".exe", null)]
    [InlineData("", null)]
    public void ProgramIsItsFileName(string input, string? expected) =>
        Assert.Equal(expected, ProgramRoutes.Normalize(input));

    [Fact]
    public void ANewProgramIsTwoEmptyListsSwitchedOffAtTheTop()
    {
        var rules = UserRulesFile.Load(Rules);
        rules.Set(MatchKind.Domain, "*.example.com", RoutingMode.Proxy);
        rules.Save();

        Add("Diablo IV.exe");

        Assert.Equal("config/lists/own/programs/diablo-iv.exe.domains.txt", ProgramRoutes.DomainsPath("Diablo IV.exe"));
        Assert.True(File.Exists(Full(ProgramRoutes.DomainsPath("Diablo IV.exe"))));
        Assert.True(File.Exists(Full(ProgramRoutes.AddressesPath("Diablo IV.exe"))));
        Assert.Empty(ProgramRoutes.Entries(ProgramRoutes.DomainsPath("Diablo IV.exe"), _root));

        var entries = UserRulesFile.Load(Rules).Entries;
        Assert.Equal(3, entries.Count);
        Assert.True(entries[0].Matches(ProgramRoutes.DomainsPath("Diablo IV.exe")));
        Assert.True(entries[1].Matches(ProgramRoutes.AddressesPath("Diablo IV.exe")));
        Assert.All(entries.Take(2), e => Assert.True(e.Off));
        Assert.All(entries.Take(2), e => Assert.Equal(MatchKind.HostList, e.Match));

        var route = Assert.Single(AppSettings.Load(Settings).ProgramRoutes);
        Assert.Equal("Diablo IV.exe", route.Program);
        Assert.True(route.AutoFill);
    }

    [Fact]
    public void TheSameProgramIsNotAddedTwice()
    {
        Add("game.exe");

        Assert.False(ProgramRoutes.Add("GAME.exe", Settings, Rules, _root));
        Assert.Single(AppSettings.Load(Settings).ProgramRoutes);
        Assert.Equal(2, UserRulesFile.Load(Rules).Entries.Count);
    }

    [Fact]
    public void RemovingTakesRulesListsAndTheEntry()
    {
        Add("game.exe");
        ProgramRoutes.Remove("game.exe", Settings, Rules, _root);

        Assert.Empty(UserRulesFile.Load(Rules).Entries);
        Assert.False(File.Exists(Full(ProgramRoutes.DomainsPath("game.exe"))));
        Assert.False(File.Exists(Full(ProgramRoutes.AddressesPath("game.exe"))));
        Assert.Empty(AppSettings.Load(Settings).ProgramRoutes);
    }

    [Fact]
    public void ProgramListsAreOwnButKnownApart()
    {
        Assert.True(OwnLists.IsOwn(ProgramRoutes.DomainsPath("game.exe")));
        Assert.True(ProgramRoutes.IsProgramList(ProgramRoutes.AddressesPath("game.exe")));
        Assert.False(ProgramRoutes.IsProgramList(OwnLists.PathFor("мои-сайты")));
    }

    private static ConnectionEvent Connection(string program, string address, string? host = null) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Protocol = ProtocolKind.Udp,
        RemoteAddress = IPAddress.Parse(address),
        RemotePort = 5055,
        ExecutablePath = @"C:\Games\" + program,
        Hostname = host,
    };

    private ProgramRouteLearner Learner() => new(Settings, _root);

    [Fact]
    public void ANamedConnectionGoesToDomainsOnce()
    {
        Add("game.exe");
        var learner = Learner();

        var learned = learner.Observe(Connection("game.exe", "5.188.1.10", "EU.Game.Example."));

        Assert.NotNull(learned);
        Assert.True(learned.IsDomain);
        Assert.Equal("eu.game.example", learned.Value);
        Assert.Null(learner.Observe(Connection("game.exe", "5.188.1.11", "eu.game.example")));
        Assert.Null(learner.Observe(Connection("game.exe", "5.188.1.11", "cdn.eu.game.example")));

        Assert.Equal(["eu.game.example"], ProgramRoutes.Entries(ProgramRoutes.DomainsPath("game.exe"), _root));
        Assert.Empty(ProgramRoutes.Entries(ProgramRoutes.AddressesPath("game.exe"), _root));
    }

    [Fact]
    public void ABareAddressGoesToAddressesAsItsNetwork()
    {
        Add("game.exe");
        var learner = Learner();

        Assert.Equal("45.82.160.0/24", learner.Observe(Connection("game.exe", "45.82.160.17"))?.Value);
        Assert.Null(learner.Observe(Connection("game.exe", "45.82.160.200")));
        Assert.Equal("45.82.161.0/24", learner.Observe(Connection("game.exe", "45.82.161.5"))?.Value);

        Assert.Equal(["45.82.160.0/24", "45.82.161.0/24"], ProgramRoutes.Entries(ProgramRoutes.AddressesPath("game.exe"), _root));
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("239.255.255.250")]
    [InlineData("198.18.0.42")]
    [InlineData("127.0.0.1")]
    public void HomeMulticastAndFakeAddressesAreNotLearned(string address)
    {
        Add("game.exe");

        Assert.Null(Learner().Observe(Connection("game.exe", address)));
    }

    [Fact]
    public void AFakeAddressWithANameTeachesTheName()
    {
        Add("game.exe");

        Assert.Equal("api.game.example", Learner().Observe(Connection("game.exe", "198.18.0.42", "api.game.example"))?.Value);
    }

    [Fact]
    public void OtherProgramsAndSwitchedOffOnesAreLeftAlone()
    {
        Add("game.exe");
        Add("other.exe");
        ProgramRoutes.SetAutoFill("other.exe", false, Settings);
        var learner = Learner();

        Assert.Null(learner.Observe(Connection("chrome.exe", "8.8.8.8")));
        Assert.Null(learner.Observe(Connection("other.exe", "8.8.8.8")));
        Assert.Empty(ProgramRoutes.Entries(ProgramRoutes.AddressesPath("other.exe"), _root));
    }

    [Fact]
    public void HandWrittenLinesAreRespected()
    {
        Add("game.exe");
        File.AppendAllText(Full(ProgramRoutes.DomainsPath("game.exe")), "game.example\r\n");
        File.AppendAllText(Full(ProgramRoutes.AddressesPath("game.exe")), "45.82.0.0/16\r\n");
        var learner = Learner();

        Assert.Null(learner.Observe(Connection("game.exe", "1.1.1.1", "login.game.example")));
        Assert.Null(learner.Observe(Connection("game.exe", "45.82.160.17")));
    }
}
