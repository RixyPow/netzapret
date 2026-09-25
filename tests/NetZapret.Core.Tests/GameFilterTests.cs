using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Game filter (25.09): выключенный не меняет строку запуска ничем,
/// включённый — дописывает две секции последними и расширяет перехват.
/// </summary>
public sealed class GameFilterTests
{
    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;

            directory = directory.Parent;
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

    /// <summary>
    /// Обещание владельцу: выключенный game filter не трогает запуск.
    /// На всех пресетах из поставки строка без него и с выключенным —
    /// одна и та же до последнего ключа, и следов его в ней нет.
    /// </summary>
    [Fact]
    public void OffChangesNothingOnAnyShippedPreset()
    {
        foreach (var preset in ShippedPresets())
        {
            var plain = WinwsCommandLine.Build(preset).ToList();
            var off = WinwsCommandLine.Build(preset, gameFilter: false).ToList();

            Assert.Equal(plain, off);
            Assert.DoesNotContain(off, a => a.Contains("nz_game", StringComparison.Ordinal));
            Assert.DoesNotContain($"--name={GameFilter.TcpName}", off);
            Assert.DoesNotContain($"--name={GameFilter.UdpName}", off);
        }
    }

    [Fact]
    public void OnAddsTwoSectionsLastAndLosesNothing()
    {
        int checkedPresets = 0;

        foreach (var preset in ShippedPresets())
        {
            var plain = WinwsCommandLine.Build(preset).ToList();
            var on = WinwsCommandLine.Build(preset, gameFilter: true).ToList();

            // Последние две секции — игры, и именно в этом порядке.
            int tcp = on.IndexOf($"--name={GameFilter.TcpName}");
            int udp = on.IndexOf($"--name={GameFilter.UdpName}");

            Assert.True(tcp > 0 && udp > tcp, $"{preset.Name}: секций игр нет или порядок не тот");
            Assert.Equal("--new", on[tcp - 1]);
            Assert.DoesNotContain(on.Skip(udp + 1), a => a == "--new");

            // Всё прежнее на месте: каждый ключ пресета остался, секции пресета —
            // в прежнем порядке и выше игр.
            var presetNames = plain.Where(a => a.StartsWith("--name=", StringComparison.Ordinal)).ToList();
            var onNames = on.Where(a => a.StartsWith("--name=", StringComparison.Ordinal)).ToList();

            Assert.Equal(presetNames, onNames.Take(presetNames.Count));

            foreach (var argument in plain.Where(a => !a.StartsWith("--wf-", StringComparison.Ordinal)))
                Assert.Contains(argument, on);

            // Перехват покрывает порты игр — иначе WinDivert их движку не отдаст.
            foreach (var key in new[] { "--wf-tcp-out=", "--wf-udp-out=" })
            {
                var wf = on.Where(a => a.StartsWith(key, StringComparison.Ordinal)).ToList();

                Assert.True(
                    wf.Any(a => GameFilter.Covers(a[key.Length..])),
                    $"{preset.Name}: {key} не покрывает {GameFilter.Ports}");
            }

            // Образцы игр объявлены, модули приёмов подключены — и ровно по разу.
            foreach (var blob in new[] { "nz_game_stun", "nz_game_tls", "nz_game_udp" })
                Assert.Single(on, a => a.StartsWith($"--blob={blob}:", StringComparison.Ordinal));

            foreach (var module in new[] { "zapret-lib.lua", "zapret-antidpi.lua" })
                Assert.Single(on, a => a.StartsWith("--lua-init=", StringComparison.Ordinal) && a.EndsWith(module, StringComparison.Ordinal));

            checkedPresets++;
        }

        if (Repository() is not null)
            Assert.True(checkedPresets > 5);
    }

    [Theory]
    [InlineData("80,443-65535", true)]
    [InlineData("443-65535", true)]
    [InlineData("80,443,1024-65535", true)]
    [InlineData("80,443,2053,2083,8443", false)]
    [InlineData("443,19294-19344", false)]
    [InlineData("", false)]
    public void CoverageOfGamePortsIsRead(string ports, bool covers) =>
        Assert.Equal(covers, GameFilter.Covers(ports));
}
