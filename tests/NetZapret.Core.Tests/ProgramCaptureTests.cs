using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Правило по программе и сети Riot (30.09): полный перехват ради программы
/// не уводит Valorant через движок туннеля.
/// </summary>
public sealed class ProgramCaptureTests
{
    private static RuleSet Rules(string yaml) => RuleSetLoader.Load(yaml).RuleSet;

    private const string Program = """
          - match: process
            value: "Fallout76.exe"
            mode: proxy
        """;

    /// <summary>Часть «Игровой UDP»: по ней с 30.09 и решается, выводить ли сети.</summary>
    private static string Riot(string mode) => $"""
          - match: ipset
            value: "config/lists/riot-network.txt"
            mode: {mode}
        """;

    private static string RiotNames(string mode) => $"""
          - match: hostlist
            value: "config/lists/riot-valorant.txt"
            mode: {mode}
        """;

    /// <summary>
    /// Полный перехват ради программы — только без десинка (владелец, 01.10).
    /// </summary>
    [Fact]
    public void AProgramTakesTheWholeTunnelOnlyWithoutDesync()
    {
        var withProgram = Rules("mode: selective\nrules:\n" + Program);
        var without = Rules("mode: selective\nrules:\n" + Riot("desync"));

        Assert.False(ProgramCapture.ForcesFullCapture(withProgram, desyncRuns: true));
        Assert.True(ProgramCapture.ForcesFullCapture(withProgram, desyncRuns: false));
        Assert.False(ProgramCapture.ForcesFullCapture(without, desyncRuns: false));
    }

    [Fact]
    public void WithoutAProgramRouteNothingIsKeptOut()
    {
        Assert.Empty(ProgramCapture.KeepOut(Rules("mode: selective\nrules:\n" + Riot("desync"))));
    }

    [Theory]
    [InlineData("desync")]
    [InlineData("direct")]
    public void RiotOffTheVpnStaysOutOfTheTunnel(string mode)
    {
        var keptOut = ProgramCapture.KeepOut(Rules("mode: selective\nrules:\n" + Program + "\n" + Riot(mode)));

        Assert.Equal([ProgramCapture.RiotNetwork], keptOut);
    }

    /// <summary>Riot, отправленную в VPN, из туннеля выводить нельзя.</summary>
    [Fact]
    public void RiotRoutedToTheVpnStaysIn()
    {
        Assert.Empty(ProgramCapture.KeepOut(Rules("mode: selective\nrules:\n" + Program + "\n" + Riot("proxy"))));
    }

    /// <summary>
    /// Вход через VPN, игра напрямую — совет игрокам из обсуждения №11: сети
    /// выводятся, маршрут имён на это не влияет.
    /// </summary>
    [Fact]
    public void LoginThroughTheVpnDoesNotPullTheGameIn()
    {
        var keptOut = ProgramCapture.KeepOut(
            Rules("mode: selective\nrules:\n" + Program + "\n" + RiotNames("proxy") + "\n" + Riot("direct")));

        Assert.Equal([ProgramCapture.RiotNetwork], keptOut);
    }

    /// <summary>Правила про Riot нет вовсе — она идёт мимо VPN, и её сети тоже.</summary>
    [Fact]
    public void RiotWithoutARuleIsKeptOut()
    {
        Assert.Equal([ProgramCapture.RiotNetwork], ProgramCapture.KeepOut(Rules("mode: selective\nrules:\n" + Program)));
    }

    /// <summary>Список сетей лежит там, где его ищет сборка конфига, и в нём адреса Riot.</summary>
    [Fact]
    public void TheRiotNetworkListShips()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
            directory = directory.Parent;

        if (directory is null)
            return;

        var lines = File.ReadAllLines(Path.Combine(directory.FullName, ProgramCapture.RiotNetwork))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();

        Assert.Contains("104.160.128.0/19", lines);
        Assert.All(lines, l => Assert.Contains('/', l));
    }
}
