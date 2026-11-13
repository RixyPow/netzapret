using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Шаг пересчёта окна (с --out-range=a и --payload=all) встаёт последним в каждую секцию TCP, когда пресет занижает множитель
/// окна (14.11): иначе имя, ушедшее в секцию без пересчёта, почти встаёт.
/// </summary>
public sealed class ScaleRampTests
{
    private static List<string> Line(string syn) =>
    [
        "--wf-tcp-out=80,443",
        "--lua-init=@lua/nz-video.lua",
        "--filter-tcp=*",
        "--hostlist-exclude=shield.txt",
        "--lua-desync=pass",
        "--new",
        "--name=googlevideo",
        "--filter-tcp=80,443",
        "--hostlist=lists/googlevideo.txt",
        "--out-range=-d8",
        "--lua-desync=nz_first2:reset:far=35",
        "--lua-desync=multidisorder:pos=1",
        "--new",
        "--name=QUIC",
        "--filter-udp=443",
        "--lua-desync=fake:blob=quic_google",
        "--new",
        "--name=Узлы видео",
        "--filter-tcp=443",
        "--ipset-ip=74.125.0.0/16",
        "--out-range=a",
        $"--lua-desync={syn}",
    ];

    [Fact]
    public void EveryTcpSectionButTheSynOneGetsTheRampLast()
    {
        var arguments = Line("nz_first2:syn:far=35:scale=1");

        ScaleRamp.Apply(arguments);

        var sections = string.Join(" ", arguments).Split(" --new ");

        // Голова со щитом, секция по имени — с пересчётом последним, под --out-range=a.
        Assert.EndsWith("--lua-desync=pass " + string.Join(" ", ScaleRamp.Tail), sections[0]);
        Assert.EndsWith("--lua-desync=multidisorder:pos=1 " + string.Join(" ", ScaleRamp.Tail), sections[1]);

        // UDP пересчёт не нужен, секция самого SYN пересчитывает окно сама.
        Assert.DoesNotContain(ScaleRamp.Step, sections[2]);
        Assert.DoesNotContain(ScaleRamp.Step, sections[3]);
    }

    [Fact]
    public void WithoutScaleNothingChanges()
    {
        var arguments = Line("nz_first2:syn:far=35");
        var before = arguments.ToList();

        ScaleRamp.Apply(arguments);

        Assert.Equal(before, arguments);
    }

    [Fact]
    public void ApplyingTwiceAddsNothing()
    {
        var arguments = Line("nz_first2:syn:scale=1");

        ScaleRamp.Apply(arguments);
        var once = arguments.ToList();
        ScaleRamp.Apply(arguments);

        Assert.Equal(once, arguments);
    }
}
