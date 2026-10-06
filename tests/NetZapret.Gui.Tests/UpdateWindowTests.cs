using NetZapret.Core.Updates;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Окно «Доступно обновление» (владелец, 06.10, по образцу Zapret GUI).
/// </summary>
public sealed class UpdateWindowTests
{
    [Fact]
    public void TheWindowIsCreatedAndClosingMeansLater()
    {
        // Разметка разбирается, «Что нового» строится из примечаний.
        // Крестик — это «Позже»: о версии напомнят при следующем запуске.
        Sta.Run(() =>
        {
            var window = new UpdateWindow(new ReleaseInfo
            {
                Version = "9.9.9",
                Tag = "v9.9.9",
                ArchiveUrl = "https://example/NetZapret-9.9.9.zip",
                ArchiveSize = 1_000_000,
                Notes = "# Что нового\n\n## Новое:\n\n**Проверка.** Текст.",
            });

            Assert.Equal(UpdateChoice.Later, window.Choice);
            Assert.NotEmpty(window.Notes.Children);
            Assert.NotEmpty(window.Details.Children);
            Assert.True(window.OffersInstall);
            Assert.Equal(System.Windows.Visibility.Visible, window.SkipButton.Visibility);
            Assert.Equal("Позже", window.LaterButton.Content);

            window.Close();
        });
    }

    [Theory]
    [InlineData("0.13.0", true)]
    [InlineData("v0.12.0", true)]
    [InlineData("1.0.0", true)]
    [InlineData("0.13.1", false)]
    [InlineData("0.10", false)]
    public void RoundVersionsAreMilestones(string version, bool round)
    {
        // Владелец 06.10: «круглые версии типа 0.12.0 и 0.13.0 — большими кружками».
        Assert.Equal(round, UpdateWindow.IsRound(version));
    }

    [Theory]
    [InlineData(0, 0, "")]
    [InlineData(1, 0, "1 новое")]
    [InlineData(3, 2, "3 новых · 2 исправления")]
    [InlineData(0, 5, "5 исправлений")]
    [InlineData(21, 11, "21 новое · 11 исправлений")]
    public void SummaryCountsInRussian(int added, int fixedCount, string expected) =>
        Assert.Equal(expected, UpdateWindow.Summary(added, fixedCount));

    [Fact]
    public void TheFoundVersionIsSelectedInTheTimeline()
    {
        Sta.Run(() =>
        {
            var window = new UpdateWindow(new ReleaseInfo
            {
                Version = "9.9.0",
                Tag = "v9.9.0",
                ArchiveUrl = "https://example/NetZapret-9.9.0.zip",
                Notes = "## Новое:\n\n**Первое.** Текст.",
            });

            Assert.Equal("9.9.0", window.Selected);
            Assert.Single(window.Timeline.Children);

            window.Close();
        });
    }

    [Fact]
    public void AtTheLatestVersionInstallIsOff()
    {
        // «Проверить» при последней версии открывает то же окно с тем же
        // чейнджлогом, но «Обновить» погашена (владелец, 06.10).
        Sta.Run(() =>
        {
            var window = new UpdateWindow(new ReleaseInfo
            {
                Version = UpdateCheck.Current,
                Tag = "v" + UpdateCheck.Current,
                ArchiveUrl = "https://example/NetZapret.zip",
                Notes = "## Новое:\n\n**Проверка.** Текст.",
            });

            Assert.False(window.OffersInstall);
            Assert.Equal("Установлена последняя версия", window.Heading.Text);
            Assert.NotEmpty(window.Notes.Children);

            // Пропускать нечего, напоминать не о чем (владелец, 06.10).
            Assert.Equal(System.Windows.Visibility.Collapsed, window.SkipButton.Visibility);
            Assert.Equal("Закрыть", window.LaterButton.Content);

            window.Close();
        });
    }
}
