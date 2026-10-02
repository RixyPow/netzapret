using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Дописанное человеком в наши списки переживает обновление (жалоба 02.10, EA для Apex; Fint 03.10).
/// </summary>
public sealed class ListCarryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nz-carry-{Guid.NewGuid():N}");

    private string Installed => Path.Combine(_root, "installed");
    private string Staged => Path.Combine(_root, "staged");

    public ListCarryTests()
    {
        Directory.CreateDirectory(Path.Combine(Installed, "config", "lists"));
        Directory.CreateDirectory(Path.Combine(Installed, "engines", "zapret", "lists"));
        Directory.CreateDirectory(Path.Combine(Staged, "config", "lists"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Установленная версия: что поставлено и что человек держит в рабочем списке.</summary>
    private void Install(string name, string[] shipped, string[] working)
    {
        File.WriteAllLines(Path.Combine(Installed, "engines", "zapret", "lists", ListCarry.PristinePrefix + name), shipped);
        File.WriteAllLines(Path.Combine(Installed, "config", "lists", name), working);
    }

    private void Ship(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(Staged, "config", "lists", name), lines);

    private string[] StagedLines(string name) =>
        File.ReadAllLines(Path.Combine(Staged, "config", "lists", name));

    [Fact]
    public void Added_names_reach_the_new_list()
    {
        Install("ea.txt", ["ea.com", "origin.com"], ["ea.com", "origin.com", "apex.ea.com", "r5sb.ea.com"]);
        Ship("ea.txt", "ea.com", "origin.com", "eaassets-a.akamaihd.net");

        var carried = UpdateInstaller.CarryListEdits(Installed, Staged);

        Assert.Equal(2, carried["ea.txt"]);
        Assert.Equal(
            ["ea.com", "origin.com", "eaassets-a.akamaihd.net", ListCarry.Marker, "apex.ea.com", "r5sb.ea.com"],
            StagedLines("ea.txt"));
    }

    [Fact]
    public void An_untouched_list_is_not_touched()
    {
        Install("ea.txt", ["ea.com"], ["ea.com"]);
        Ship("ea.txt", "ea.com", "new.ea.com");

        Assert.Empty(UpdateInstaller.CarryListEdits(Installed, Staged));
        Assert.Equal(["ea.com", "new.ea.com"], StagedLines("ea.txt"));
    }

    [Fact]
    public void What_the_new_version_brings_itself_is_not_written_twice()
    {
        Install("ea.txt", ["ea.com"], ["ea.com", "apex.ea.com"]);
        Ship("ea.txt", "ea.com", "apex.ea.com");

        Assert.Empty(UpdateInstaller.CarryListEdits(Installed, Staged));
        Assert.Equal(["ea.com", "apex.ea.com"], StagedLines("ea.txt"));
    }

    /// <summary>
    /// Второе обновление подряд: перенесённое уже стоит под пометкой, нетронутая копия — новая.
    /// Переезжает снова, а пометка не множится.
    /// </summary>
    [Fact]
    public void A_second_update_carries_again_with_one_marker()
    {
        Install("ea.txt", ["ea.com", "x.ea.com"], ["ea.com", "x.ea.com", ListCarry.Marker, "apex.ea.com"]);
        Ship("ea.txt", "ea.com", "x.ea.com", "y.ea.com");

        UpdateInstaller.CarryListEdits(Installed, Staged);

        var lines = StagedLines("ea.txt");
        Assert.Single(lines, l => l == ListCarry.Marker);
        Assert.Equal("apex.ea.com", lines[^1]);
    }

    [Fact]
    public void Removed_names_are_not_carried_back()
    {
        // Человек вычеркнул имя — новая версия его вернёт: переносим только дописанное.
        Install("ea.txt", ["ea.com", "origin.com"], ["ea.com"]);
        Ship("ea.txt", "ea.com", "origin.com");

        Assert.Empty(UpdateInstaller.CarryListEdits(Installed, Staged));
        Assert.Equal(["ea.com", "origin.com"], StagedLines("ea.txt"));
    }

    [Fact]
    public void Without_pristine_copies_nothing_is_guessed()
    {
        // Рабочая копия разработчика: движка рядом нет — сравнивать не с чем.
        Directory.Delete(Path.Combine(Installed, "engines"), recursive: true);
        File.WriteAllLines(Path.Combine(Installed, "config", "lists", "ea.txt"), ["ea.com", "apex.ea.com"]);
        Ship("ea.txt", "ea.com");

        Assert.Empty(UpdateInstaller.CarryListEdits(Installed, Staged));
        Assert.Equal(["ea.com"], StagedLines("ea.txt"));
    }

    [Fact]
    public void A_list_the_new_version_does_not_have_is_not_created()
    {
        Install("old.txt", ["a.com"], ["a.com", "b.com"]);

        Assert.Empty(UpdateInstaller.CarryListEdits(Installed, Staged));
        Assert.False(File.Exists(Path.Combine(Staged, "config", "lists", "old.txt")));
    }

    [Fact]
    public void Own_lists_folder_is_not_looked_at()
    {
        // config\lists\own\ обновление не трогает вовсе — переносить оттуда нечего.
        Directory.CreateDirectory(Path.Combine(Installed, "config", "lists", "own"));
        File.WriteAllLines(Path.Combine(Installed, "config", "lists", "own", "ea.txt"), ["mine.com"]);
        Install("ea.txt", ["ea.com"], ["ea.com"]);
        Ship("ea.txt", "ea.com");

        Assert.Empty(UpdateInstaller.CarryListEdits(Installed, Staged));
    }

    [Fact]
    public void A_list_without_a_trailing_newline_gets_one_before_the_marker()
    {
        Install("ea.txt", ["ea.com"], ["ea.com", "apex.ea.com"]);
        File.WriteAllText(Path.Combine(Staged, "config", "lists", "ea.txt"), "ea.com");

        UpdateInstaller.CarryListEdits(Installed, Staged);

        Assert.Equal(["ea.com", ListCarry.Marker, "apex.ea.com"], StagedLines("ea.txt"));
    }
}
