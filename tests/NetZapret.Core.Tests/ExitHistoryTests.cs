using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Журнал выходов для «Недавних серверов» (01.10).</summary>
public sealed class ExitHistoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nz-exits-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void OnlyChangesAreWrittenAndRecentAreDistinctNewestFirst()
    {
        var at = DateTimeOffset.Now;

        ExitHistory.Note("EE", at, _path);
        ExitHistory.Note("EE", at.AddSeconds(3), _path);
        ExitHistory.Note("FI", at.AddMinutes(1), _path);
        ExitHistory.Note("EE", at.AddMinutes(2), _path);

        Assert.Equal(["EE", "FI", "EE"], ExitHistory.Read(_path).Select(e => e.Tag));
        Assert.Equal(["EE", "FI"], ExitHistory.Recent(5, _path).Select(e => e.Tag));
        Assert.Equal(["EE"], ExitHistory.Recent(1, _path).Select(e => e.Tag));
    }

    [Fact]
    public void TheLogIsCapped()
    {
        var at = DateTimeOffset.Now;

        for (int i = 0; i < ExitHistory.Keep + 10; i++)
            ExitHistory.Note($"S{i}", at.AddSeconds(i), _path);

        var seen = ExitHistory.Read(_path);

        Assert.Equal(ExitHistory.Keep, seen.Count);
        Assert.Equal($"S{ExitHistory.Keep + 9}", seen[^1].Tag);
    }

    [Fact]
    public void ABrokenLogStartsOver()
    {
        File.WriteAllText(_path, "{ не json");

        Assert.Empty(ExitHistory.Read(_path));

        ExitHistory.Note("EE", DateTimeOffset.Now, _path);
        Assert.Single(ExitHistory.Read(_path));
    }
}

/// <summary>WARP или подписки (владелец, 01.10).</summary>
public sealed class WarpExclusiveTests
{
    private static readonly IReadOnlyList<ProxyServer> Subscription =
    [
        new ProxyServer
        {
            Protocol = ProxyProtocol.Vless,
            Tag = "NL",
            Host = "77.1.1.1",
            Port = 443,
            Credential = "PLACEHOLDER",
        },
    ];

    [Fact]
    public void WarpOnMeansOnlyWarp()
    {
        var on = new AppSettings { WarpEnabled = true, PreferredServer = "NL" };

        Assert.Equal([Warp.MasqueTag], Warp.TunnelExits(on, Subscription).Select(s => s.Tag));
        Assert.Equal(Warp.MasqueTag, Warp.PreferredExit(on));
    }

    /// <summary>Выключили WARP — туннель возвращается к выбранному серверу подписки.</summary>
    [Fact]
    public void WarpOffMeansTheSubscriptionAndTheChosenServer()
    {
        var off = new AppSettings { WarpEnabled = false, PreferredServer = "NL" };

        Assert.Equal(["NL"], Warp.TunnelExits(off, Subscription).Select(s => s.Tag));
        Assert.Equal("NL", Warp.PreferredExit(off));
        Assert.Null(Warp.PreferredExit(new AppSettings()));
    }
}
