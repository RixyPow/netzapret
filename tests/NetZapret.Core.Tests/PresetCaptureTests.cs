using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ширина перехвата поверх пресета — настройка «Перехват» (владелец, 03.10).
/// </summary>
public sealed class PresetCaptureTests
{
    private static string? Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;
        }

        return null;
    }

    private static IEnumerable<ZapretPreset> ShippedPresets()
    {
        if (Repository() is not { } root)
            yield break;

        foreach (var file in Directory.GetFiles(Path.Combine(root, "presets"), "*.txt"))
            yield return new PresetReader().Load(file);
    }

    private static ZapretPreset Preset(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-capture-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllLines(path, lines);
            return new PresetReader().Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Кто настройку не трогал, у того строка запуска та же до последнего ключа.</summary>
    [Fact]
    public void As_in_the_preset_changes_nothing_on_any_shipped_preset()
    {
        int checkedPresets = 0;

        foreach (var preset in ShippedPresets())
        {
            Assert.Equal(WinwsCommandLine.Build(preset), WinwsCommandLine.Build(preset, capture: CaptureWidth.Preset));
            checkedPresets++;
        }

        Assert.True(checkedPresets > 0, "пресеты из поставки не нашлись");
    }

    [Theory]
    [InlineData(CaptureWidth.Sites, "80,443", "443")]
    [InlineData(CaptureWidth.All, "80,443-65535", "443-65535")]
    public void A_level_changes_exactly_the_two_port_keys(CaptureWidth width, string tcp, string udp)
    {
        foreach (var preset in ShippedPresets())
        {
            var plain = WinwsCommandLine.Build(preset).ToList();
            var leveled = WinwsCommandLine.Build(preset, capture: width).ToList();

            Assert.Equal(plain.Count, leveled.Count);
            Assert.Contains("--wf-tcp-out=" + tcp, leveled);
            Assert.Contains("--wf-udp-out=" + udp, leveled);

            // Всё прочее — на тех же местах: секции, части фильтра по содержимому, щит.
            for (int i = 0; i < plain.Count; i++)
            {
                if (plain[i].StartsWith("--wf-tcp-out=", StringComparison.Ordinal)
                    || plain[i].StartsWith("--wf-udp-out=", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.Equal(plain[i], leveled[i]);
            }

            Assert.Contains(leveled, a => a.StartsWith("--wf-raw-part=", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Game_filter_widens_on_top_of_sites_only()
    {
        foreach (var preset in ShippedPresets())
        {
            var line = WinwsCommandLine.Build(preset, gameFilter: true, capture: CaptureWidth.Sites);
            var udp = line.Single(a => a.StartsWith("--wf-udp-out=", StringComparison.Ordinal));

            Assert.Equal("--wf-udp-out=443," + GameFilter.Ports, udp);
        }
    }

    [Fact]
    public void A_preset_with_a_full_filter_is_left_alone()
    {
        var preset = Preset("--wf-raw=@windivert.filter/windivert.all.txt", "--filter-tcp=443", "--lua-desync=pass");

        Assert.False(PresetCapture.Applies(preset.GlobalArguments));
        Assert.Equal(WinwsCommandLine.Build(preset), WinwsCommandLine.Build(preset, capture: CaptureWidth.Sites));
        Assert.Null(PresetCapture.Effective(preset, CaptureWidth.Sites));
    }

    [Fact]
    public void A_missing_port_key_is_added()
    {
        var preset = Preset("--wf-tcp-out=80,443-65535", "--filter-tcp=443", "--lua-desync=pass");
        var line = WinwsCommandLine.Build(preset, capture: CaptureWidth.Sites);

        Assert.Contains("--wf-udp-out=443", line);
        Assert.Contains("--wf-tcp-out=80,443", line);
    }

    [Fact]
    public void Sites_only_names_what_the_sections_lose()
    {
        var preset = Preset(
            "--wf-tcp-out=80,443-65535",
            "--wf-udp-out=443-65535",
            "--new", "--name=discord.com", "--filter-tcp=80,443,1080,2053,8443", "--hostlist=lists/discord.txt", "--lua-desync=pass",
            "--new", "--name=Telegram", "--filter-tcp=80,443,5222", "--ipset=lists/ipset-telegram.txt", "--lua-desync=pass",
            "--new", "--name=Russia blacklist", "--filter-tcp=443-65535", "--hostlist=lists/russia-blacklist.txt", "--lua-desync=pass",
            "--new", "--name=Discord UDP", "--filter-udp=443-65535", "--ipset=lists/ipset-discord.txt", "--lua-desync=pass");

        var lost = PresetCapture.Unreached(preset, CaptureWidth.Sites);

        Assert.Contains("discord.com — TCP 1080, 2053, 8443", lost);
        Assert.Contains("Telegram — TCP 5222", lost);
        Assert.Contains("Discord UDP — UDP 444–65535", lost);

        // «TLS на любом порту» у секции по именам — не потеря, о которой стоит кричать.
        Assert.DoesNotContain(lost, l => l.StartsWith("Russia blacklist", StringComparison.Ordinal));
    }

    [Fact]
    public void As_in_the_preset_loses_nothing_the_preset_did_not_already_lose()
    {
        var preset = Preset(
            "--wf-tcp-out=80,443-65535",
            "--wf-udp-out=443-65535",
            "--new", "--name=discord.com", "--filter-tcp=80,443,1080,2053,8443", "--hostlist=lists/discord.txt", "--lua-desync=pass",
            "--new", "--name=Discord UDP", "--filter-udp=443-65535", "--ipset=lists/ipset-discord.txt", "--lua-desync=pass");

        Assert.Empty(PresetCapture.Unreached(preset, CaptureWidth.Preset));
        Assert.Empty(PresetCapture.Unreached(preset, CaptureWidth.All));
    }

    [Fact]
    public void The_level_survives_the_settings_file()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-settings-{Guid.NewGuid():N}.json");

        try
        {
            new AppSettings { Capture = CaptureWidth.Sites }.Save(path);

            Assert.Equal(CaptureWidth.Sites, AppSettings.Load(path).Capture);
            Assert.Equal(CaptureWidth.Preset, new AppSettings().Capture);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
