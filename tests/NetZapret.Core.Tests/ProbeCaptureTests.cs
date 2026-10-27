using System.Net;
using System.Net.Sockets;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Порты пробы рецептов (27.10): основной winws2 их не перехватывает, пробный —
/// только их, и проба рецепта ходит строго с них.
/// </summary>
public sealed class ProbeCaptureTests
{
    /// <summary>Порты пробы — ниже динамического диапазона Windows: обычные соединения их не получат.</summary>
    [Fact]
    public void Probe_ports_stay_below_the_dynamic_range_and_are_bound_only_on_request()
    {
        Assert.True(ProbePorts.Last < 49152);
        Assert.True(ProbePorts.First <= ProbePorts.Last);

        using (var plain = ProbePorts.Client(AddressFamily.InterNetwork))
            Assert.Null(plain.Client.LocalEndPoint);

        using (ProbePorts.Use())
        using (var probe = ProbePorts.Client(AddressFamily.InterNetwork))
        {
            var port = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;

            Assert.InRange(port, ProbePorts.First, ProbePorts.Last);
        }

        Assert.False(ProbePorts.Active);
    }

    private static ZapretPreset Preset(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-preset-{Guid.NewGuid():N}.txt");

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

    /// <summary>
    /// Строка пробного winws2: перехват только портов пробы, один профиль рецепта —
    /// без щита, без секций и без первого профиля пресета.
    /// </summary>
    [Fact]
    public void The_probe_line_captures_only_probe_ports_with_one_recipe()
    {
        var preset = Preset(
            "--wf-tcp-out=80,443",
            "--lua-init=@lua/zapret-lib.lua",
            "--filter-tcp=443",
            "--hostlist=lists/head.txt",
            "--lua-desync=fake",
            "--new",
            "--name=Секция",
            "--filter-tcp=443",
            "--lua-desync=multisplit");

        var recipe = new OwnDesyncProfile
        {
            Name = "проба",
            Steps = ["multidisorder:pos=1"],
            HostListPath = @"C:\nz\runtime\probe\recipe.txt",
            Ports = "443",
        };

        var line = WinwsCommandLine.ProbeLine(preset, recipe)!;

        Assert.Equal($"--wf-raw-filter=({ProbePorts.Packets})", line[0]);
        Assert.Contains("--wf-tcp-out=80,443", line);
        Assert.Contains("--lua-desync=multidisorder:pos=1", line);
        Assert.Contains("--hostlist=C:/nz/runtime/probe/recipe.txt", line);
        Assert.DoesNotContain("--name=Секция", line);
        Assert.DoesNotContain("--hostlist=lists/head.txt", line);
        Assert.False(TunnelCapture.KeepsProbesOut(line));

        // Без рецепта — пресет целиком, тоже только на портах пробы.
        var whole = WinwsCommandLine.ProbeLine(preset, null)!;

        Assert.Contains("--name=Секция", whole);
        Assert.Equal($"--wf-raw-filter=({ProbePorts.Packets})", whole[0]);

        // Свой фильтр пресета файлом — сузить нечем.
        Assert.Null(WinwsCommandLine.ProbeLine(Preset("--wf-raw-filter=@own.txt", "--filter-tcp=443", "--lua-desync=pass"), recipe));
    }
}
