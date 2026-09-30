using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Прошлые замеры скорости и вывод из них (30.09).
/// </summary>
public sealed class SpeedHistoryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 9, 30, 12, 54, 0, TimeSpan.FromHours(3));

    private static SpeedEntry Direct(DateTimeOffset at, double down = 354.7, double up = 341.6, double ping = 8) =>
        new() { At = at, Tunnel = false, DownMbps = down, UpMbps = up, PingMs = ping, Country = "RU", Node = "DME" };

    private static SpeedEntry Tunnel(DateTimeOffset at, double down = 156.2, double up = 48.2, double ping = 37) =>
        new() { At = at, Tunnel = true, Exit = "Эстония — TLS XHTTP", DownMbps = down, UpMbps = up, PingMs = ping, Country = "EE" };

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), $"nz-speed-{Guid.NewGuid():N}", "speed-history.json");

    [Fact]
    public void EntriesComeBackNewestFirst()
    {
        var path = TempFile();

        try
        {
            SpeedHistory.Add(Tunnel(Noon.AddMinutes(-1)), path);
            SpeedHistory.Add(Direct(Noon), path);

            var entries = SpeedHistory.Load(path);

            Assert.Equal(2, entries.Count);
            Assert.False(entries[0].Tunnel);
            Assert.Equal("Эстония — TLS XHTTP", entries[1].Exit);
            Assert.Equal(156.2, entries[1].DownMbps);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheLastThirtyAreKept()
    {
        var path = TempFile();

        try
        {
            for (int i = 0; i < SpeedHistory.Keep + 5; i++)
                SpeedHistory.Add(Direct(Noon.AddMinutes(i), down: i), path);

            var entries = SpeedHistory.Load(path);

            Assert.Equal(SpeedHistory.Keep, entries.Count);
            Assert.Equal(SpeedHistory.Keep + 4, entries[0].DownMbps);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ABrokenFileIsAnEmptyHistory()
    {
        var path = TempFile();

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{ не json");

            Assert.Empty(SpeedHistory.Load(path));

            // И не мешает писать дальше.
            SpeedHistory.Add(Direct(Noon), path);
            Assert.Single(SpeedHistory.Load(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>Адрес, с которого нас видел сервер, в файл не попадает: файл уходит в отчёты.</summary>
    [Fact]
    public void TheAddressIsNotStored()
    {
        var path = TempFile();

        try
        {
            var result = new SpeedResult { DownMbps = 10, Address = "203.0.113.7", Country = "EE", Node = "TLL" };

            SpeedHistory.Add(SpeedEntry.From(result, tunnel: true, "Эстония", Noon), path);

            var text = File.ReadAllText(path);

            Assert.DoesNotContain("203.0.113.7", text);
            Assert.DoesNotContain("Address", text);
            Assert.Contains("TLL", text);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    /// <summary>Сервер туннеля у замера напрямую не пишется: он к нему не относится.</summary>
    [Fact]
    public void ADirectEntryHasNoExit()
    {
        var entry = SpeedEntry.From(new SpeedResult { DownMbps = 10, DownBytes = 5, UpBytes = 7 }, tunnel: false, "Эстония", Noon);

        Assert.Null(entry.Exit);
        Assert.Equal(12, entry.Bytes);
    }

    [Fact]
    public void TheOtherPathIsComparedOnlyWhileFresh()
    {
        var latest = Tunnel(Noon);

        Assert.NotNull(SpeedVerdict.Pair(latest, [Direct(Noon.AddHours(-2))]));
        Assert.Null(SpeedVerdict.Pair(latest, [Direct(Noon.AddHours(-4))]));
        Assert.Null(SpeedVerdict.Pair(latest, [Tunnel(Noon.AddMinutes(-5))]));

        // Неудавшийся замер — не мерило.
        Assert.Null(SpeedVerdict.Pair(latest, [Direct(Noon.AddMinutes(-5)) with { DownMbps = null }]));
    }

    /// <summary>Цифры владельца 30.09: туннель 156 и 48, напрямую 355 и 342.</summary>
    [Fact]
    public void WithBothPathsTheVerdictIsTheirRatio()
    {
        var (title, text) = SpeedVerdict.Describe(Tunnel(Noon.AddMinutes(-1)), Direct(Noon));

        Assert.Equal("Туннель даёт 44 % прямой скорости", title);
        Assert.Contains("Скачивание 156 из 355 Мбит/с.", text);
        Assert.Contains("14 %", text);
        Assert.Contains("Задержка 37 мс против 8.", text);

        // Тот же вывод, с какой стороны ни смотри.
        Assert.Equal(title, SpeedVerdict.Describe(Direct(Noon), Tunnel(Noon.AddMinutes(-1))).Title);
    }

    [Fact]
    public void WithOnePathTheVerdictAsksForTheOther()
    {
        var (title, text) = SpeedVerdict.Describe(Direct(Noon), null);

        Assert.Equal("355 вниз, 342 вверх", title);
        Assert.Contains("через туннель", text);

        Assert.Contains("напрямую", SpeedVerdict.Describe(Tunnel(Noon), null).Text);
    }

    [Fact]
    public void AFailedMeasurementSaysWhy()
    {
        var failed = new SpeedEntry { At = Noon, Tunnel = true, Problem = "сервер замера недоступен: отказ" };
        var (title, text) = SpeedVerdict.Describe(failed, Direct(Noon));

        Assert.Equal("Замер не удался", title);
        Assert.Equal("Сервер замера недоступен: отказ.", text);
    }

    [Fact]
    public void TheCopiedTextHasNoAddress()
    {
        var text = SpeedVerdict.Copy(Tunnel(Noon.AddMinutes(-1)), Direct(Noon), "0.10.0");

        Assert.StartsWith("NetZapret 0.10.0, замер скорости", text);
        Assert.Contains("через туннель: скачивание 156 Мбит/с, отдача 48.2 Мбит/с, задержка 37 мс; сервер Эстония — TLS XHTTP; страна выхода EE", text);
        Assert.Contains("напрямую: скачивание 355 Мбит/с", text);
        Assert.Contains("узел Cloudflare DME", text);
    }

    [Fact]
    public void UnevennessIgnoresTheRampUp()
    {
        // Разгон с нуля, дальше ровно: первая четверть не в счёт.
        Assert.Equal(0, SpeedVerdict.Unevenness([0, 50, 100, 100, 100, 100, 100, 100])!.Value, 9);

        Assert.True(SpeedVerdict.Unevenness([0, 0, 100, 20, 100, 20, 100, 20]) > SpeedVerdict.Steady);
        Assert.Null(SpeedVerdict.Unevenness([10, 10, 10]));
        Assert.Null(SpeedVerdict.Unevenness([0, 0, 0, 0, 0, 0, 0, 0]));
    }
}
