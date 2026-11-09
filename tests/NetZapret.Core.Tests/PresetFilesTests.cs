using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Копия и переименование пресета (03.11).</summary>
public sealed class PresetFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nz-presets-{Guid.NewGuid():N}");

    public PresetFilesTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllLines(Path.Combine(_dir, "Universal V12 Lite.txt"),
            ["# Preset: Universal V12 Lite", "# BuiltinVersion: 2.26", "# Description: x", "", "--lua-init=@lua/zapret-lib.lua", "--new", "--name=a"]);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Copy_is_own_preset_with_its_name_in_the_header()
    {
        var name = PresetFiles.CopyName(_dir, "Universal V12 Lite");
        Assert.Equal("Копия Universal V12 Lite", name);

        var file = PresetFiles.Duplicate(_dir, "Universal V12 Lite.txt", name);
        var lines = File.ReadAllLines(Path.Combine(_dir, file));

        Assert.Equal("# Preset: Копия Universal V12 Lite", lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("# BuiltinVersion:", StringComparison.Ordinal));
        Assert.Contains("--name=a", lines);
        Assert.False(PresetFiles.Shipped(Path.Combine(_dir, file)));
        Assert.True(PresetFiles.Shipped(Path.Combine(_dir, "Universal V12 Lite.txt")));

        Assert.Equal("Копия Universal V12 Lite (2)", PresetFiles.CopyName(_dir, "Universal V12 Lite"));
    }

    [Fact]
    public void Shipped_preset_is_not_renamed_but_own_one_is()
    {
        Assert.Throws<InvalidOperationException>(() => PresetFiles.Rename(_dir, "Universal V12 Lite.txt", "Мой"));

        PresetFiles.Duplicate(_dir, "Universal V12 Lite.txt", "мой пресет");
        var renamed = PresetFiles.Rename(_dir, "мой пресет.txt", "Мой пресет");

        Assert.Equal("Мой пресет.txt", renamed);
        Assert.Equal("Мой пресет.txt", Path.GetFileName(Directory.GetFiles(_dir, "*ой пресет.txt").Single()));
        Assert.Equal("# Preset: Мой пресет", File.ReadAllLines(Path.Combine(_dir, renamed))[0]);
    }

    [Theory]
    [InlineData("", "пустым")]
    [InlineData("a/b", "нельзя")]
    [InlineData("точка.", "нельзя")]
    [InlineData("Universal V12 Lite", "уже есть")]
    public void Bad_names_are_refused(string name, string word) =>
        Assert.Contains(word, PresetFiles.Problem(name, _dir));

    [Fact]
    public void Renamed_preset_keeps_choice_and_fakes()
    {
        var settings = new AppSettings
        {
            PresetName = "Мой",
            FakeSwaps = [new("Мой", "Голосовые звонки/чаты", 0, "stun2.bin"), new("Другой", "x", 0, "stun2.bin")],
        };

        var after = PresetFiles.Renamed(settings, "Мой", "Новый");

        Assert.Equal("Новый", after.PresetName);
        Assert.Equal(["Новый", "Другой"], after.FakeSwaps.Select(s => s.Preset));
        Assert.Equal(AppSettings.DefaultPresetName, PresetFiles.Renamed(new AppSettings(), "Мой", "Новый").PresetName);
    }
}
