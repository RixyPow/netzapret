using System.Text.Json.Nodes;
using NetZapret.Core;
using Xunit;
namespace NetZapret.Core.Tests;

public sealed class RecoverySettingsFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nz-recovery-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "config", "subscription-recovery.json");
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
    [Theory]
    [InlineData(null, 300)] [InlineData("", 300)] [InlineData("   ", 300)]
    [InlineData("1", 1)] [InlineData("300", 300)] [InlineData(" 450 ", 450)] [InlineData("60000", 60000)]
    public void ValidInput(string? text, int expected)
    { Assert.True(RecoverySettingsFile.TryParseLatency(text, out var ms)); Assert.Equal(expected, ms); }
    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("60001")] [InlineData("abc")]
    [InlineData("3.5")] [InlineData("NaN")] [InlineData("99999999999999999999")]
    public void InvalidInput(string text) => Assert.False(RecoverySettingsFile.TryParseLatency(text, out _));
    [Fact] public void MissingFileIsDisabledWith300()
    { var s = RecoverySettingsFile.Load(FilePath); Assert.False(s.Enabled); Assert.Equal(300, s.MaxLatencyMs); }
    [Fact] public void ToggleAndThresholdRoundTrip()
    {
        RecoverySettingsFile.SaveUi(true, 450, FilePath);
        Assert.True(RecoverySettingsFile.Load(FilePath).Enabled);
        Assert.Equal(450, RecoverySettingsFile.Load(FilePath).MaxLatencyMs);
        RecoverySettingsFile.SaveUi(false, 300, FilePath);
        Assert.False(RecoverySettingsFile.Load(FilePath).Enabled);
        Assert.Equal(300, RecoverySettingsFile.Load(FilePath).MaxLatencyMs);
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(FilePath)!));
    }
    [Fact] public void AdvancedAndUnknownSettingsSurvive()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{"enabled":false,"maxlatencyms":500,"CandidatesPerRound":5,"AutoRefreshMinutes":90,"FutureField":{"keep":true}}""");
        RecoverySettingsFile.SaveUi(true, 300, FilePath);
        var s = RecoverySettingsFile.Load(FilePath);
        Assert.True(s.Enabled); Assert.Equal(300, s.MaxLatencyMs);
        Assert.Equal(5, s.CandidatesPerRound); Assert.Equal(90, s.AutoRefreshMinutes);
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject();
        Assert.True(json["FutureField"]!["keep"]!.GetValue<bool>());
        Assert.False(json.ContainsKey("enabled")); Assert.False(json.ContainsKey("maxlatencyms"));
    }
    [Theory] [InlineData("{")] [InlineData("null")] [InlineData("[]")] [InlineData("{\"MaxLatencyMs\":0}")]
    public void InvalidFileIsNotOverwritten(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, content);
        Assert.ThrowsAny<Exception>(() => RecoverySettingsFile.SaveUi(true, 300, FilePath));
        Assert.Equal(content, File.ReadAllText(FilePath));
    }
    [Fact] public void InvalidSaveDoesNotCreateFile()
    { Assert.Throws<ArgumentOutOfRangeException>(() => RecoverySettingsFile.SaveUi(true, 0, FilePath)); Assert.False(File.Exists(FilePath)); }
}
