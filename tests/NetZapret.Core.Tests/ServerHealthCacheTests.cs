using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

public sealed class ServerHealthCacheTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"netzapret-health-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private static ServerHealth Entry(string tag, bool success, double? ms, TimeSpan? age = null) => new()
    {
        Tag = tag,
        Success = success,
        LatencyMs = ms,
        CheckedAt = DateTimeOffset.Now - (age ?? TimeSpan.Zero),
    };

    [Fact]
    public void ResultsSurviveBetweenRuns()
    {
        // Ради этого кэш и заводился: проверка идёт десятки секунд, и
        // повторять её при каждом заходе в список ради тех же цифр — плата
        // ни за что.
        var cache = ServerHealthCache.Load(_path);
        cache.Set(Entry("🇷🇺 Россия", success: true, ms: 80));
        cache.Set(Entry("🇸🇪 Швеция", success: false, ms: null));
        cache.Save(_path);

        var reloaded = ServerHealthCache.Load(_path);

        Assert.Equal(80, reloaded.Find("🇷🇺 Россия")!.LatencyMs);
        Assert.False(reloaded.Find("🇸🇪 Швеция")!.Success);
    }

    [Fact]
    public void FreshnessIsJudgedByTheOldestEntry()
    {
        // По старейшей, а не по средней: пока хоть один сервер помнится
        // со вчера, показывать список как свежий нельзя.
        var cache = ServerHealthCache.Load(_path);
        cache.Set(Entry("свежий", true, 50));
        cache.Set(Entry("вчерашний", true, 50, age: TimeSpan.FromHours(20)));

        Assert.True(cache.OldestAge > TimeSpan.FromHours(19));
    }

    [Fact]
    public void EntriesForVanishedServersAreDropped()
    {
        // Подписка меняется, и без чистки по исчезнувшим серверам судили бы
        // о свежести всего списка.
        var cache = ServerHealthCache.Load(_path);
        cache.Set(Entry("остался", true, 50));
        cache.Set(Entry("исчез", true, 50, age: TimeSpan.FromDays(3)));

        cache.KeepOnly(["остался"]);

        Assert.Null(cache.Find("исчез"));
        Assert.True(cache.OldestAge < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void CorruptedFileIsNotAnError()
    {
        File.WriteAllText(_path, "это не json {{{");

        Assert.Equal(0, ServerHealthCache.Load(_path).Count);
    }

    [Fact]
    public void MissingFileIsNotAnError()
    {
        Assert.Equal(0, ServerHealthCache.Load(Path.Combine(Path.GetTempPath(), "нет-файла.json")).Count);
    }
}

/// <summary>
/// Какие движки нужны в каждом режиме.
/// </summary>
public sealed class OperatingModeNeedsTests
{
    private static AppSettings With(OperatingMode mode, string? preset = "Universal V6") =>
        new() { Mode = mode, PresetName = preset };

    [Theory]
    [InlineData(OperatingMode.Off, false)]
    [InlineData(OperatingMode.DesyncOnly, false)]
    [InlineData(OperatingMode.Selective, true)]
    [InlineData(OperatingMode.ProxyAll, true)]
    [InlineData(OperatingMode.ProxyStrict, true)]
    public void TunnelRisesOnlyWhereItLeadsSomewhere(OperatingMode mode, bool expected)
    {
        // Поднятый вхолостую TUN — адаптер есть, маршруты стоят, трафика нет —
        // удваивает время поиска первой же неисправности.
        Assert.Equal(expected, With(mode).NeedsProxy);
    }

    [Theory]
    [InlineData(OperatingMode.Off, false)]
    [InlineData(OperatingMode.DesyncOnly, true)]
    [InlineData(OperatingMode.Selective, true)]
    public void DesyncFollowsTheModeAndThePreset(OperatingMode mode, bool expected)
    {
        Assert.Equal(expected, With(mode).NeedsDesync);
    }

    [Fact]
    public void WithoutAPresetThereIsNoDesync()
    {
        Assert.False(With(OperatingMode.DesyncOnly, preset: null).NeedsDesync);
    }

    [Fact]
    public void DesyncOnlyModeHasNothingToStartWithoutAPreset()
    {
        // Такой набор запускать нечего, и меню обязано сказать об этом,
        // а не делать вид, что запустило.
        var settings = With(OperatingMode.DesyncOnly, preset: null);

        Assert.False(settings.NeedsProxy);
        Assert.False(settings.NeedsDesync);
    }
}
