using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Полоска расхода подписки и прогноз темпа (идея №5, 19.10).</summary>
public sealed class SubscriptionUsageTests
{
    private const long Gb = 1L << 30;

    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.FromHours(3));

    private static SubscriptionInfo Info(long used, long total, DateTimeOffset? expires = null) => new()
    {
        Servers = [],
        DownloadBytes = used,
        TotalBytes = total,
        ExpiresAt = expires,
    };

    [Fact]
    public void Unlimited_shows_used_over_infinity_without_a_bar_or_warning()
    {
        var forecast = SubscriptionUsage.Forecast(Info(233 * Gb, 0, Now.AddDays(69)), [], Now);

        Assert.Equal("233 ГБ / ∞", forecast.Label);
        Assert.Null(forecast.Fraction);
        Assert.Equal("Истекает: 27.12.2026", forecast.Expires);
        Assert.Null(forecast.Warning);
    }

    [Fact]
    public void Pace_comes_from_the_last_week_and_needs_half_a_day()
    {
        UsageSample[] history =
        [
            new(Now.AddDays(-10), 10 * Gb, 500 * Gb),
            new(Now.AddDays(-4), 100 * Gb, 500 * Gb),
            new(Now, 180 * Gb, 500 * Gb),
        ];

        Assert.Equal(20 * Gb, SubscriptionUsage.PerDay(history, Now));
        Assert.Null(SubscriptionUsage.PerDay([new(Now.AddHours(-6), 1 * Gb, 0), new(Now, 2 * Gb, 0)], Now));
    }

    [Fact]
    public void Running_out_before_expiry_within_two_weeks_warns()
    {
        // 20 ГБ в сутки, осталось 200 ГБ — десять дней, а срок через месяц.
        UsageSample[] history = [new(Now.AddDays(-5), 200 * Gb, 500 * Gb), new(Now, 300 * Gb, 500 * Gb)];

        var forecast = SubscriptionUsage.Forecast(Info(300 * Gb, 500 * Gb, Now.AddDays(30)), history, Now);

        Assert.Equal(0.6, forecast.Fraction!.Value, 3);
        Assert.Contains("через 10 дн", forecast.Warning);
        Assert.Contains("раньше срока подписки", forecast.Warning);
    }

    [Theory]
    [InlineData(5)]   // срок раньше, чем кончится трафик
    [InlineData(null)] // без срока, но кончится через 25 дней — дальше горизонта
    public void A_calm_pace_does_not_warn(int? expiresInDays)
    {
        // 10 ГБ в сутки, осталось 250 ГБ.
        UsageSample[] history = [new(Now.AddDays(-2), 230 * Gb, 500 * Gb), new(Now, 250 * Gb, 500 * Gb)];
        DateTimeOffset? expires = expiresInDays is { } days ? Now.AddDays(days) : null;

        Assert.Null(SubscriptionUsage.Forecast(Info(250 * Gb, 500 * Gb, expires), history, Now).Warning);
    }

    [Fact]
    public void An_exhausted_quota_warns_at_once()
    {
        var forecast = SubscriptionUsage.Forecast(Info(500 * Gb, 500 * Gb), [], Now);

        Assert.Equal(1.0, forecast.Fraction!.Value);
        Assert.Contains("кончился", forecast.Warning);
    }

    [Fact]
    public void A_quota_reset_starts_the_history_over_and_close_samples_replace_each_other()
    {
        IReadOnlyList<UsageSample> history = [new(Now.AddDays(-3), 400 * Gb, 500 * Gb)];

        history = SubscriptionUsage.Add(history, new(Now.AddDays(-1), 5 * Gb, 500 * Gb));
        Assert.Single(history);

        history = SubscriptionUsage.Add(history, new(Now.AddDays(-1).AddMinutes(20), 6 * Gb, 500 * Gb));
        Assert.Single(history);
        Assert.Equal(6 * Gb, history[0].Used);

        history = SubscriptionUsage.Add(history, new(Now, 9 * Gb, 500 * Gb));
        Assert.Equal(2, history.Count);
    }

    [Fact]
    public void Samples_are_kept_by_a_fingerprint_not_by_the_link()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-usage-{Guid.NewGuid():N}.json");
        const string url = "https://panel.example/sub/secret-token";

        try
        {
            SubscriptionUsage.Record(url, Info(5 * Gb, 100 * Gb), Now.AddDays(-1), path);
            SubscriptionUsage.Record(url, Info(7 * Gb, 100 * Gb), Now, path);

            Assert.Equal(2, SubscriptionUsage.Samples(url, path).Count);
            Assert.DoesNotContain("secret-token", File.ReadAllText(path));
            Assert.DoesNotContain("panel.example", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
