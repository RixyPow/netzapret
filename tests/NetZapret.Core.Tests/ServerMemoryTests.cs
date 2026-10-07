using NetZapret.Core;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Очистка памяти замеров серверов — кнопкой и по сроку (владелец 07.10).
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

    /// <summary>Пишет в память «мигающий» сервер: пять проверок, удачных две.</summary>
    private void Remember()
    {
        var health = ServerHealthCache.Load(HealthPath);

        foreach (var ok in new[] { true, false, true, false, false })
            health.Set(new ServerHealth { Tag = "🇪🇺 ОБС", Success = ok, CheckedAt = Monday });

        health.Save(HealthPath);
        Assert.Equal(["🇪🇺 ОБС"], ServerHealthCache.Load(HealthPath).Flaky());
    }

    [Fact]
    public void WeekIsTheDefault() => Assert.Equal(ServerMemory.DefaultDays, new AppSettings().ServerMemoryDays);

    /// <summary>
    /// Отсчёта не было — он начинается, а память цела: иначе первая же сборка
    /// с этой правкой стёрла бы её у всех разом, без всякого срока.
    /// </summary>
    [Fact]
    public void FirstLookOnlyStartsTheClock()
    {
        new AppSettings { OnboardingDone = true }.Save(SettingsPath);
        Remember();

        Assert.False(ServerMemory.ClearIfDue(Monday, SettingsPath, HealthPath));

        Assert.Equal(Monday, AppSettings.Load(SettingsPath).ServerMemoryClearedAt);
        Assert.NotEmpty(ServerHealthCache.Load(HealthPath).Entries);
    }

    [Fact]
    public void MemoryIsClearedWhenTheTermIsUp()
    {
        new AppSettings { OnboardingDone = true, ServerMemoryClearedAt = Monday }.Save(SettingsPath);
        Remember();

        // Шесть дней — рано.
        Assert.False(ServerMemory.ClearIfDue(Monday.AddDays(6), SettingsPath, HealthPath));
        Assert.NotEmpty(ServerHealthCache.Load(HealthPath).Entries);

        // Неделя — пора: «мигающий» сервер забыт, отсчёт пошёл заново.
        Assert.True(ServerMemory.ClearIfDue(Monday.AddDays(7), SettingsPath, HealthPath));
        Assert.Empty(ServerHealthCache.Load(HealthPath).Entries);
        Assert.Empty(ServerHealthCache.Load(HealthPath).Flaky());

        var settings = AppSettings.Load(SettingsPath);
        Assert.Equal(Monday.AddDays(7), settings.ServerMemoryClearedAt);
        Assert.True(settings.OnboardingDone);
    }

    [Fact]
    public void NeverMeansNever()
    {
        new AppSettings { ServerMemoryDays = 0, ServerMemoryClearedAt = Monday }.Save(SettingsPath);
        Remember();

        Assert.False(ServerMemory.ClearIfDue(Monday.AddDays(365), SettingsPath, HealthPath));
        Assert.NotEmpty(ServerHealthCache.Load(HealthPath).Entries);
    }

    [Fact]
    public void ButtonClearsAndRestartsTheClock()
    {
        new AppSettings { OnboardingDone = true, ServerMemoryClearedAt = Monday }.Save(SettingsPath);
        Remember();

        ServerMemory.ClearNow(Monday.AddDays(2), SettingsPath, HealthPath);

        Assert.Empty(ServerHealthCache.Load(HealthPath).Entries);
        Assert.Equal(Monday.AddDays(2), AppSettings.Load(SettingsPath).ServerMemoryClearedAt);
    }

    /// <summary>
    /// Нечитаемый файл настроек не перезаписывается: в руках были бы
    /// значения по умолчанию, и запись поверх стёрла бы настройки целиком.
    /// </summary>
    [Fact]
    public void UnreadableSettingsAreLeftAlone()
    {
        File.WriteAllText(SettingsPath, "{ это не json");

        Assert.False(ServerMemory.ClearIfDue(Monday, SettingsPath, HealthPath));
        Assert.Equal("{ это не json", File.ReadAllText(SettingsPath));
    }
}
