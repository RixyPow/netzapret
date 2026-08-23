using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Два слоя правил: выверенный базовый набор и выбор пользователя поверх него.
/// </summary>
public sealed class LayeredRulesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"netzapret-layers-{Guid.NewGuid():N}");
    private readonly string _basePath;
    private readonly string _userPath;

    public LayeredRulesTests()
    {
        Directory.CreateDirectory(_directory);
        _basePath = Path.Combine(_directory, "rules.yaml");
        _userPath = Path.Combine(_directory, "rules.user.yaml");

        File.WriteAllText(_basePath, """
            mode: selective
            capture:
              - lists/ipset-telegram.txt
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
              - match: domain
                value: "*.youtube.com"
                mode: desync
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
        RemoteAddress = System.Net.IPAddress.Parse("203.0.113.7"),
        RemotePort = 443,
        Hostname = hostname,
    };

    [Fact]
    public void WithoutUserFileOnlyBaseRulesApply()
    {
        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("rutracker.org")).Mode);
        Assert.Equal(RoutingMode.Desync, engine.Evaluate(Connection("www.youtube.com")).Mode);
    }

    [Fact]
    public void UserChoiceOverridesTheBaseSetting()
    {
        // Ради этого разделение и делалось: человек может решить иначе,
        // не правя выверенный набор.
        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Domain, "www.youtube.com", RoutingMode.Proxy);
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);
        var decision = engine.Evaluate(Connection("www.youtube.com"));

        Assert.Equal(RoutingMode.Proxy, decision.Mode);
        Assert.Equal(RuleSource.User, decision.Rule!.Source);
    }

    [Fact]
    public void DisabledUserRuleFallsBackToTheBase()
    {
        // Выключение должно возвращать заводское поведение, а не удалять выбор.
        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Domain, "www.youtube.com", RoutingMode.Proxy);
        file.Toggle(0);
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.Equal(RoutingMode.Desync, engine.Evaluate(Connection("www.youtube.com")).Mode);

        // Но сама запись никуда не делась.
        Assert.Single(UserRulesFile.Load(_userPath).Entries);
    }

    [Fact]
    public void BaseModeAndDefaultAreKept()
    {
        // Пользовательский файл описывает только отдельные маршруты;
        // режим и умолчание живут в базовом наборе.
        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Domain, "example.com", RoutingMode.Proxy);
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.Equal(OperatingMode.Selective, engine.RuleSet.Operating);
        Assert.Equal(RoutingMode.Desync, engine.RuleSet.DefaultMode);
    }

    [Fact]
    public void CaptureSectionsFromBothLayersAreCombined()
    {
        File.WriteAllText(_userPath, """
            rules: []
            capture:
              - 91.108.4.0/22
            """);

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        Assert.Equal(2, engine.RuleSet.CaptureEntries.Count);
    }

    [Fact]
    public void ProcessRuleStillBeatsUserDomainRule()
    {
        // Слой меняет приоритет только внутри своего класса совпадения.
        // Программа по-прежнему важнее домена — иначе правило «эта игра
        // напрямую» перестало бы работать.
        File.WriteAllText(_basePath, """
            mode: selective
            rules:
              - match: process
                value: "game.exe"
                mode: direct
            default:
              mode: desync
            """);

        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Domain, "example.com", RoutingMode.Proxy);
        file.Save();

        var engine = RuleSetLoader.LoadLayered(_basePath, _userPath);

        var decision = engine.Evaluate(Connection("example.com") with { ExecutablePath = "game.exe" });

        Assert.Equal(RoutingMode.Direct, decision.Mode);
    }

    [Fact]
    public void CorruptedUserFileDoesNotBreakLoading()
    {
        File.WriteAllText(_userPath, "это не yaml: [[[");

        var file = UserRulesFile.Load(_userPath);

        Assert.Empty(file.Entries);
    }

    [Fact]
    public void RepeatedChoiceReplacesRatherThanDuplicates()
    {
        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Domain, "example.com", RoutingMode.Proxy);
        file.Set(MatchKind.Domain, "example.com", RoutingMode.Direct);
        file.Save();

        var reloaded = UserRulesFile.Load(_userPath);

        Assert.Single(reloaded.Entries);
        Assert.Equal(RoutingMode.Direct, reloaded.Entries[0].Mode);
    }

    [Fact]
    public void SavedFileSurvivesRoundTripWithNonAsciiValues()
    {
        var file = UserRulesFile.Load(_userPath);
        file.Set(MatchKind.Process, "Телеграм.exe", RoutingMode.Proxy);
        file.Save();

        var reloaded = UserRulesFile.Load(_userPath);

        Assert.Equal("Телеграм.exe", reloaded.Entries[0].Value);
    }
}
