using NetZapret.Core;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Кеш значков Windows перечитывается один раз на смену значка программы (21.10),
/// а не на каждом запуске.
/// </summary>
public sealed class IconCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"netzapret-icon-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void TheCacheIsRefreshedOncePerIcon()
    {
        int refreshed = 0;
        void Count() => Interlocked.Increment(ref refreshed);

        new AppSettings { OnboardingDone = true }.Save(_path);

        Assert.True(IconCache.RefreshIfChanged("a1", _path, Count));
        Assert.False(IconCache.RefreshIfChanged("a1", _path, Count));
        Assert.True(IconCache.RefreshIfChanged("b2", _path, Count));

        var settings = AppSettings.Load(_path);
        Assert.Equal("b2", settings.IconStamp);
        Assert.True(settings.OnboardingDone);

        // Перечитывание идёт в фоне — дождаться обоих.
        SpinWait.SpinUntil(() => Volatile.Read(ref refreshed) == 2, TimeSpan.FromSeconds(5));
        Assert.Equal(2, Volatile.Read(ref refreshed));
    }
}
