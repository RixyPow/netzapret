using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Правила по программе сняты, пока обход по программе на переработке (владелец, 04.10).
/// </summary>
public sealed class ProgramRulesOffTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("nz-programs-").FullName;

    private string SettingsPath => Path.Combine(_dir, "netzapret.json");

    private string RulesPath => Path.Combine(_dir, "rules.user.yaml");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteRules(params (MatchKind Match, string Value, RoutingMode Mode)[] rules)
    {
        var file = UserRulesFile.Load(RulesPath);

        foreach (var (match, value, mode) in rules)
            file.Set(match, value, mode);

        file.Save();
    }

    [Fact]
    public void Program_rules_are_removed_and_remembered()
    {
        AppSettings.Fresh.Save(SettingsPath);
        WriteRules(
            (MatchKind.Process, "Diablo IV.exe", RoutingMode.Proxy),
            (MatchKind.Domain, "*.example.com", RoutingMode.Direct));

        var removed = ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath);

        Assert.Equal(["Diablo IV.exe → VPN"], removed);

        var left = UserRulesFile.Load(RulesPath).Entries;
        Assert.DoesNotContain(left, e => e.Match == MatchKind.Process);
        Assert.Contains(left, e => e.Match == MatchKind.Domain);

        var settings = AppSettings.Load(SettingsPath);
        Assert.True(settings.ProgramRulesRemoved);
        Assert.Equal(["Diablo IV.exe → VPN"], settings.RemovedProgramRules);
    }

    /// <summary>Один раз: вписанное руками после перевода остаётся, и запомненное не затирается.</summary>
    [Fact]
    public void Removal_happens_once()
    {
        AppSettings.Fresh.Save(SettingsPath);
        WriteRules((MatchKind.Process, "Diablo IV.exe", RoutingMode.Proxy));
        ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath);

        WriteRules((MatchKind.Process, "game.exe", RoutingMode.Direct));

        Assert.Empty(ProgramRulesOff.RemoveOnce(SettingsPath, RulesPath));
        Assert.Contains(UserRulesFile.Load(RulesPath).Entries, e => e.Match == MatchKind.Process);
        Assert.Equal(["Diablo IV.exe → VPN"], AppSettings.Load(SettingsPath).RemovedProgramRules);
    }
}
