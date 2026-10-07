using NetZapret.Core;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Память замеров серверов: старые проверки забываются по одной, кнопка
/// стирает всё (владелец 07.10).
/// </summary>
public sealed class ServerMemoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nz-memory-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_root, "netzapret.json");

    private string HealthPath => Path.Combine(_root, "server-health.json");

    public ServerMemoryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private static readonly DateTimeOffset Monday = new(2026, 10, 5, 12, 0, 0, TimeSpan.FromHours(3));

    private void Check(string tag, bool ok, DateTimeOffset at)
    {
        var health = ServerHealthCache.Load(HealthPath);
        health.Set(new ServerHealth { Tag = tag, Success = ok, CheckedAt = at });
        health.Save(HealthPath);
    }

    [Fact]
    public void WeekIsTheDefault() => Assert.Equal(ServerMemory.DefaultDays, new AppSettings().ServerMemoryDays);

    /// <summary>
    /// Владелец 07.10: «подчищало именно старые замеры, а не всю память».
    /// Сервер мигал неделю назад и с тех пор отвечает — давние промахи
    /// забываются, свежие удачи остаются, и «нестабильным» он быть перестаёт.
    /// </summary>
    [Fact]
    public void OldMissesAreForgottenFreshChecksStay()
    {
        new AppSettings().Save(SettingsPath);

        foreach (var (ok, day) in new[] { (false, 0), (true, 0), (false, 1), (false, 1), (true, 8), (true, 9) })
            Check("🇪🇺 ОБС", ok, Monday.AddDays(day));

        Assert.Equal(["🇪🇺 ОБС"], ServerHealthCache.Load(HealthPath).Flaky());

        // На десятый день: неделя назад — третий день; всё до него забыто.
        int forgotten = ServerMemory.ForgetOld(Monday.AddDays(10), SettingsPath, HealthPath);

        Assert.Equal(4, forgotten);

        var memory = ServerHealthCache.Load(HealthPath);
        var server = memory.Find("🇪🇺 ОБС")!;

        Assert.Equal([true, true], server.Recent);
        Assert.Equal([Monday.AddDays(8), Monday.AddDays(9)], server.RecentAt);
        Assert.Equal(0, server.Failures);
        Assert.Empty(memory.Flaky());
    }

    /// <summary>Не осталось ни одной свежей проверки — сервер забывается целиком, а задержка с ним.</summary>
    [Fact]
    public void ServerWithOnlyOldChecksIsForgotten()
    {
        new AppSettings().Save(SettingsPath);
        Check("🇯🇵 Япония", true, Monday);
        Check("🇳🇱 Нидерланды", true, Monday.AddDays(9));

        ServerMemory.ForgetOld(Monday.AddDays(10), SettingsPath, HealthPath);

        var memory = ServerHealthCache.Load(HealthPath);
        Assert.Null(memory.Find("🇯🇵 Япония"));
        Assert.NotNull(memory.Find("🇳🇱 Нидерланды"));
    }

    /// <summary>
    /// Давний промах не держит «мёртвым»: счёт неудач подряд — по оставшимся.
    /// </summary>
    [Fact]
    public void FailureStreakIsRecountedFromWhatRemains()
    {
        new AppSettings().Save(SettingsPath);

        foreach (var (ok, day) in new[] { (true, 0), (false, 1), (false, 1), (false, 9) })
            Check("🇪🇸 Испания", ok, Monday.AddDays(day));

        Assert.Equal(["🇪🇸 Испания"], ServerHealthCache.Load(HealthPath).Dead(3));

        ServerMemory.ForgetOld(Monday.AddDays(10), SettingsPath, HealthPath);

        var server = ServerHealthCache.Load(HealthPath).Find("🇪🇸 Испания")!;
        Assert.Equal(1, server.Failures);
        Assert.Empty(ServerHealthCache.Load(HealthPath).Dead(3));
    }

    /// <summary>
    /// Записи до 07.10 времени у каждой проверки не знают — им достаётся время
    /// последней проверки: раньше неё они были наверняка и уйдут не позже срока.
    /// </summary>
    [Fact]
    public void ChecksWithoutTimeTakeTheLastCheckTime()
    {
        new AppSettings().Save(SettingsPath);
        File.WriteAllText(HealthPath, $$"""
            [ { "Tag": "🇧🇪 Бельгия", "Success": true, "LatencyMs": 164, "CheckedAt": "{{Monday:O}}",
                "Failures": 0, "Recent": [false, true, true, false, true] } ]
            """);

        Assert.Equal(0, ServerMemory.ForgetOld(Monday.AddDays(6), SettingsPath, HealthPath));
        Assert.Equal(5, ServerMemory.ForgetOld(Monday.AddDays(8), SettingsPath, HealthPath));
        Assert.Empty(ServerHealthCache.Load(HealthPath).Entries);
    }

    [Fact]
    public void NeverMeansNever()
    {
        new AppSettings { ServerMemoryDays = 0 }.Save(SettingsPath);
        Check("🇯🇵 Япония", false, Monday);

        Assert.Equal(0, ServerMemory.ForgetOld(Monday.AddDays(365), SettingsPath, HealthPath));
        Assert.NotNull(ServerHealthCache.Load(HealthPath).Find("🇯🇵 Япония"));
    }

    [Fact]
    public void ButtonClearsEverything()
    {
        Check("🇯🇵 Япония", true, Monday);
        Check("🇳🇱 Нидерланды", true, Monday.AddDays(9));

        ServerMemory.ClearAll(HealthPath);

        Assert.Empty(ServerHealthCache.Load(HealthPath).Entries);
    }

    /// <summary>Настройки только читаются: нечитаемый файл остаётся как был.</summary>
    [Fact]
    public void SettingsAreNeverWritten()
    {
        File.WriteAllText(SettingsPath, "{ это не json");
        Check("🇯🇵 Япония", true, Monday);

        Assert.Equal(0, ServerMemory.ForgetOld(Monday.AddDays(30), SettingsPath, HealthPath));
        Assert.Equal("{ это не json", File.ReadAllText(SettingsPath));
        Assert.NotNull(ServerHealthCache.Load(HealthPath).Find("🇯🇵 Япония"));
    }
}
