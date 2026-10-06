using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// «Выключено» в маршрутах: часть снимается целиком — и своё правило, и заводское.
/// </summary>
/// <remarks>
/// Владелец 06.10: правила частей пересекаются, и прежде развести их можно
/// было, только выбрав одной из них чужой режим. Выключенная запись
/// (<c>enabled: false</c>) тут не помогала: она снимает лишь выбор человека,
/// и заводское правило под ней продолжает действовать.
/// </remarks>
public sealed class RouteOffTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"netzapret-off-{Guid.NewGuid():N}");
    private readonly string _basePath;
    private readonly string _userPath;

    public RouteOffTests()
    {
        Directory.CreateDirectory(_directory);
        _basePath = Path.Combine(_directory, "rules.yaml");
        _userPath = Path.Combine(_directory, "rules.user.yaml");

        // Узкое правило внутри широкого: выключив узкое, имя должно уйти
        // к широкому, а не к умолчанию.
        File.WriteAllText(_basePath, """
            mode: selective
            rules:
              - match: domain
                value: "cdn.example.com"
                mode: direct
              - match: domain
                value: "*.example.com"
                mode: proxy
              - match: domain
                value: "*.youtube.com"
                mode: direct
            default:
              mode: desync
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ConnectionEvent Connection(string hostname) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Protocol = ProtocolKind.Tcp,
        RemoteAddress = null,
        RemotePort = 443,
        Hostname = hostname,
    };

    [Fact]
    public void OffSilencesTheFactoryRuleSoTheNextRuleDecides()
    {
        var file = UserRulesFile.Load(_userPath);
        file.SetOff(MatchKind.Domain, "cdn.example.com");
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("cdn.example.com")).Mode);
        Assert.DoesNotContain(engine.RuleSet.Rules, r => r.Value == "cdn.example.com");
    }

    [Fact]
    public void OffWithoutWiderRuleFallsToTheDefault()
    {
        var file = UserRulesFile.Load(_userPath);
        file.SetOff(MatchKind.Domain, "*.youtube.com");
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.Equal(RoutingMode.Desync, engine.Evaluate(Connection("www.youtube.com")).Mode);
    }

    [Fact]
    public void DisabledEntryStillLeavesTheFactoryRule()
    {
        // Различие, ради которого «выключено» заведено отдельно.
        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Domain, "*.youtube.com", RoutingMode.Proxy);
        file.Toggle(0);
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(Connection("www.youtube.com")).Mode);
    }

    [Fact]
    public void OffSurvivesSaveAndLoad()
    {
        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Domain, "*.youtube.com", RoutingMode.Desync, recipe: "multidisorder");
        file.SetOff(MatchKind.Domain, "*.youtube.com");
        file.Save();

        Assert.Contains("mode: off", File.ReadAllText(_userPath));

        var again = UserRulesFile.Load(_userPath);
        var entry = Assert.Single(again.Entries);

        Assert.True(entry.Off);
        Assert.Null(entry.Recipe);
        Assert.Equal("выключено", entry.DescribeMode());
    }

    [Fact]
    public void ChoosingAModeAgainTurnsThePartBackOn()
    {
        var file = UserRulesFile.Load(_userPath);
        file.SetOff(MatchKind.Domain, "*.youtube.com");
        file.Set(MatchKind.Domain, "*.youtube.com", RoutingMode.Proxy);
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.False(Assert.Single(UserRulesFile.Load(_userPath).Entries).Off);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("www.youtube.com")).Mode);
    }

    [Fact]
    public void SharedRoutesCarryOff()
    {
        var file = UserRulesFile.Load(_userPath);
        file.SetOff(MatchKind.Domain, "*.youtube.com");
        file.Save();

        Assert.True(Assert.Single(RulesShare.Read(_userPath)).Off);
    }
}
