using NetZapret.Core.Rules;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Выключенная в «Маршрутах» часть по адресам гасит и секцию пресета (№52, 03.11):
/// «Запасной путь по адресам» Discord выключен по умолчанию, а «discord.com
/// (IP fallback)» по тому же списку прежде работала всегда.
/// </summary>
public sealed class AddressSectionsOffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-ipsetoff-{Guid.NewGuid():N}");

    public AddressSectionsOffTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private UserRulesFile Rules(string body)
    {
        var path = Path.Combine(_root, "rules.user.yaml");
        File.WriteAllText(path, "rules:\n" + body);
        return UserRulesFile.Load(path);
    }

    private static ZapretPreset? DefaultPreset()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
            directory = directory.Parent;

        var file = directory is null ? null : Path.Combine(directory.FullName, "presets", AppSettings.DefaultPresetName + ".txt");

        return file is not null && File.Exists(file) ? new PresetReader().Load(file) : null;
    }

    [Fact]
    public void OutOfTheBoxTheDiscordFallbackIsOff()
    {
        Assert.Contains("ipset-discord.txt", AddressSectionsOff.Choose(Rules("")));
    }

    [Fact]
    public void PartsOnByDefaultKeepTheirSections()
    {
        // Telegram, YouTube, Twitter по адресам включены по умолчанию — их секции не трогаются.
        var off = AddressSectionsOff.Choose(Rules(""));

        Assert.DoesNotContain("ipset-telegram.txt", off);
        Assert.DoesNotContain("ipset-youtube.txt", off);
        Assert.DoesNotContain("ipset-twitter.txt", off);
    }

    [Theory]
    [InlineData("desync")]
    [InlineData("direct")]
    public void OwnModeTurnsItBackOn(string mode)
    {
        var off = AddressSectionsOff.Choose(Rules($"""
              - match: ipset
                value: "config/lists/ipset-discord.txt"
                mode: {mode}

            """));

        Assert.DoesNotContain("ipset-discord.txt", off);
    }

    [Fact]
    public void OwnOffTurnsAPartOff()
    {
        var off = AddressSectionsOff.Choose(Rules("""
              - match: ipset
                value: "config/lists/ipset-telegram.txt"
                mode: off

            """));

        Assert.Contains("ipset-telegram.txt", off);
    }

    [Fact]
    public void DefaultPresetLosesOnlyTheDiscordAddressSections()
    {
        if (DefaultPreset() is not { } preset)
            return;

        var stripped = AddressSectionsOff.Strip(preset, ["ipset-discord.txt"], out var removed);

        Assert.Contains("discord.com (IP fallback)", removed);
        Assert.All(removed, name => Assert.Contains("Discord", name, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(preset.Sections.Count - removed.Count, stripped.Sections.Count);

        // Секции по именам с тем же списком адресов, если есть, остаются.
        Assert.All(stripped.Sections, s => Assert.False(
            s.HostListPaths.Count == 0 && s.InlineDomains.Count == 0
            && s.IpSetPaths.Count > 0 && s.IpSetPaths.All(p => p.EndsWith("ipset-discord.txt", StringComparison.OrdinalIgnoreCase))));
    }

    [Fact]
    public void NothingOffLeavesThePresetAlone()
    {
        if (DefaultPreset() is not { } preset)
            return;

        Assert.Same(preset, AddressSectionsOff.Strip(preset, [], out var removed));
        Assert.Empty(removed);
    }

    [Fact]
    public void OurPrefixedCopyIsTheSameList()
    {
        if (DefaultPreset() is not { } preset)
            return;

        // nz-ipset-cloudflare-8.txt в пресете — наш config/lists/ipset-cloudflare-8.txt.
        AddressSectionsOff.Strip(preset, ["ipset-cloudflare-8.txt"], out var removed);

        Assert.Contains("Cloudflare 8.6 (рецепт AnyDesk TCP)", removed);
    }

    [Fact]
    public void WrittenAndReadBackIncludingEmpty()
    {
        var path = Path.Combine(_root, "off.txt");

        AddressSectionsOff.Write(["ipset-discord.txt"], path);
        Assert.Equal(["ipset-discord.txt"], AddressSectionsOff.Read(path));

        AddressSectionsOff.Write([], path);
        Assert.Empty(AddressSectionsOff.Read(path));
    }
}
