using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Заголовки поставщика сверх квоты и срока — те же, что читает Happ (владелец 15.10).
/// </summary>
public sealed class SubscriptionHeadersTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nz-headers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static SubscriptionInfo Build(params (string Name, string Value)[] headers) =>
        SubscriptionParser.BuildInfo([], [], null, null, null,
            name => headers.FirstOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Value);

    [Fact]
    public void ProviderLinksAndAnnounceAreRead()
    {
        var info = Build(
            ("support-url", "https://t.me/provider_support"),
            ("profile-web-page-url", "https://provider.example/cabinet"),
            ("announce", "base64:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("Плановые работы в 03:00"))),
            ("fallback-url", "https://mirror.provider.example/sub/TOKEN"),
            ("new-url", "https://new.provider.example/sub/TOKEN"));

        Assert.Equal("https://t.me/provider_support", info.SupportUrl);
        Assert.Equal("https://provider.example/cabinet", info.WebPageUrl);
        Assert.Equal("Плановые работы в 03:00", info.Announce);
        Assert.Equal("https://mirror.provider.example/sub/TOKEN", info.FallbackUrl);
        Assert.Equal("https://new.provider.example/sub/TOKEN", info.NewUrl);
    }

    /// <summary>
    /// Кнопки открывают ссылку системой — запускать по слову чужой панели что-то, кроме страницы, нельзя.
    /// </summary>
    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:network")]
    [InlineData("C:\\Windows\\System32\\cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://example.com/a b")]
    [InlineData("tg://resolve?domain=x")]
    public void OnlyWebLinksAreTaken(string link)
    {
        var info = Build(("support-url", link), ("profile-web-page-url", link));

        Assert.Null(info.SupportUrl);
        Assert.Null(info.WebPageUrl);
    }

    /// <summary>По адресу панели уйдёт пароль — открытым http его не пускаем.</summary>
    [Fact]
    public void PanelAddressesMustBeHttps()
    {
        var info = Build(("fallback-url", "http://mirror.example/sub/TOKEN"), ("new-url", "http://new.example/sub/TOKEN"));

        Assert.Null(info.FallbackUrl);
        Assert.Null(info.NewUrl);
        Assert.Equal("http://plain.example/help", Build(("support-url", "http://plain.example/help")).SupportUrl);
    }

    [Fact]
    public void AnnounceIsOneLineAndLimited()
    {
        var info = Build(("announce", "Первая строка\r\nвторая\u202Eстрока " + new string('я', 400)));

        Assert.NotNull(info.Announce);
        Assert.DoesNotContain('\n', info.Announce);
        Assert.DoesNotContain('\u202E', info.Announce);
        Assert.Equal(SubscriptionParser.AnnounceLimit, info.Announce!.Length);
        Assert.EndsWith("…", info.Announce);
        Assert.Null(Build(("announce", "  \r\n ")).Announce);
    }

    [Fact]
    public void ExpiryIsWarnedThreeDaysAhead()
    {
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        SubscriptionInfo Until(DateTimeOffset at) => SubscriptionInfo.Empty with { ExpiresAt = at };

        Assert.Null(Until(now.AddDays(4)).ExpiryWarningText(now));
        Assert.Equal("подписка кончается через 3 дн", Until(now.AddDays(2.5)).ExpiryWarningText(now));
        Assert.Equal("подписка кончается через 5 ч", Until(now.AddHours(4.2)).ExpiryWarningText(now));
        Assert.Equal("срок подписки истёк", Until(now.AddMinutes(-1)).ExpiryWarningText(now));
        Assert.Null(SubscriptionInfo.Empty.ExpiryWarningText(now));
    }

    /// <summary>Запасной адрес нужен ровно тогда, когда панель молчит, — значит, он обязан лежать в запасе.</summary>
    [Fact]
    public void ReserveKeepsProviderHeaders()
    {
        const string url = "https://panel.example/sub/TOKEN";
        var info = Build(
            ("support-url", "https://t.me/help"),
            ("profile-web-page-url", "https://panel.example/cabinet"),
            ("announce", "Привет"),
            ("fallback-url", "https://mirror.example/sub/TOKEN"));

        SubscriptionPool.SaveReserve(_folder, url, "trojan://secret@a.example:443#Германия\n", info);
        var kept = SubscriptionPool.Kept(url, _folder)!.Value.Info;

        Assert.Equal("https://t.me/help", kept.SupportUrl);
        Assert.Equal("https://panel.example/cabinet", kept.WebPageUrl);
        Assert.Equal("Привет", kept.Announce);
        Assert.Equal("https://mirror.example/sub/TOKEN", kept.FallbackUrl);
    }

    [Fact]
    public void MovedSubscriptionTakesItsReserveAlong()
    {
        const string old = "https://old.example/sub/TOKEN";
        const string moved = "https://new.example/sub/TOKEN";

        SubscriptionPool.SaveReserve(_folder, old, "trojan://secret@a.example:443#Германия\n",
            Build(("announce", "Переезжаем")));
        SubscriptionPool.MoveReserve(old, moved, _folder);

        var kept = SubscriptionPool.Kept(moved, _folder)!.Value.Info;

        Assert.Single(kept.Servers);
        Assert.Equal("Переезжаем", kept.Announce);
    }

    [Fact]
    public void ReminderOncePerDayAndWithoutTheLink()
    {
        var path = Path.Combine(_folder, "reminders.json");
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
        const string url = "https://panel.example/sub/PRIVATE_TOKEN";

        Assert.True(SubscriptionReminders.Due(url, now, path));
        Assert.False(SubscriptionReminders.Due(url, now.AddHours(5), path));
        Assert.True(SubscriptionReminders.Due("https://other.example/sub/X", now.AddHours(5), path));
        Assert.True(SubscriptionReminders.Due(url, now.AddHours(21), path));
        Assert.DoesNotContain("PRIVATE_TOKEN", File.ReadAllText(path));
        Assert.DoesNotContain("panel.example", File.ReadAllText(path));
    }
}
