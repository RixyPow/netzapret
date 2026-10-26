using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// «Мигающий» сервер (29.09): отвечает через раз, счёт промахов подряд его не ловит.
/// </summary>
public sealed class FlakyServerTests
{
    private static ServerHealthCache With(params bool[] outcomes)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-health-{Guid.NewGuid():N}.json");
        var cache = ServerHealthCache.Load(path);

        foreach (var ok in outcomes)
        {
            cache.Set(new ServerHealth
            {
                Tag = "ОБС",
                Success = ok,
                LatencyMs = ok ? 200 : null,
                CheckedAt = DateTimeOffset.Now,
            });
        }

        return cache;
    }

    [Fact]
    public void Answering_every_other_time_is_flaky_but_never_dead()
    {
        // Как ОБС у SecureWay: 3 из 5 вперемешку.
        var cache = With(true, false, true, false, true);

        Assert.True(cache.Find("ОБС")!.Flaky);
        Assert.Contains("ОБС", cache.Flaky());
        Assert.DoesNotContain("ОБС", cache.Dead(3));
    }

    [Fact]
    public void A_single_miss_among_many_answers_is_not_flaky()
    {
        var cache = With(true, true, true, true, false, true, true, true, true, true);

        Assert.False(cache.Find("ОБС")!.Flaky);
    }

    /// <summary>Надёжность справа в строке сервера (владелец 12.10) — доля ответов из последних проверок.</summary>
    [Fact]
    public void Reliability_is_the_share_of_answers()
    {
        Assert.Equal(60, With(true, false, true, false, true).Find("ОБС")!.Reliability);
        Assert.Equal(90, With(true, true, true, true, false, true, true, true, true, true).Find("ОБС")!.Reliability);
        Assert.Equal(100, With([.. Enumerable.Repeat(false, 10), .. Enumerable.Repeat(true, 10)]).Find("ОБС")!.Reliability);
        Assert.Equal(33, With(true, false, false).Find("ОБС")!.Reliability);

        // По одной-двум проверкам это было бы везение, а не надёжность.
        Assert.Null(With(true, false).Find("ОБС")!.Reliability);
    }

    /// <summary>Ни одного ответа — мёртвый, а не «через раз» (12.10: «почему 0 % жёлтый»).</summary>
    [Fact]
    public void Never_answering_is_dead_not_flaky()
    {
        var cache = With(false, false, false, false, false);

        Assert.False(cache.Find("ОБС")!.Flaky);
        Assert.Equal(0, cache.Find("ОБС")!.Reliability);
        Assert.Contains("ОБС", cache.Dead(3));
    }

    [Fact]
    public void Too_few_checks_say_nothing()
    {
        Assert.False(With(true, false, false).Find("ОБС")!.Flaky);
    }

    /// <summary>
    /// Надзор пробует зелёных, за ними жёлтых, затем незамеренных и молчащих
    /// (владелец 27.10: мигающие «должны идти после зеленых», а не выпадать).
    /// </summary>
    [Fact]
    public void Flaky_servers_are_tried_after_the_stable_ones()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-health-{Guid.NewGuid():N}.json");
        var cache = ServerHealthCache.Load(path);

        void Check(string tag, int ms, params bool[] outcomes)
        {
            foreach (var ok in outcomes)
                cache.Set(new ServerHealth { Tag = tag, Success = ok, LatencyMs = ok ? ms : null, CheckedAt = DateTimeOffset.Now });
        }

        Check("быстрый мигающий", 50, true, false, true, false, true);
        Check("медленный стабильный", 400, true, true, true, true, true);
        Check("стабильный", 200, true, true, true, true, true);
        Check("молчит", 0, true, true, true, true, false);

        string[] members = ["молчит", "не замерян", "быстрый мигающий", "медленный стабильный", "стабильный"];

        Assert.Equal(
            ["стабильный", "медленный стабильный", "быстрый мигающий", "не замерян", "молчит"],
            cache.PickOrder(members, stableFirst: true));

        // «Только по задержке» — мигающий наравне со всеми.
        Assert.Equal(
            ["быстрый мигающий", "стабильный", "медленный стабильный", "не замерян", "молчит"],
            cache.PickOrder(members, stableFirst: false));
    }

    [Fact]
    public void Only_the_last_ten_are_remembered()
    {
        // Старые провалы забываются: сервер, починившийся давно, не мигает.
        var cache = With([.. Enumerable.Repeat(false, 10), .. Enumerable.Repeat(true, 10)]);

        Assert.Equal(ServerHealthCache.RecentSize, cache.Find("ОБС")!.Recent.Count);
        Assert.False(cache.Find("ОБС")!.Flaky);
    }
}
