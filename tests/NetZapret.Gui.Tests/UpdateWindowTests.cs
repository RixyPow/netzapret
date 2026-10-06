using System.IO;
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
        Assert.Equal(expected, UpdateWindow.Summary(added, fixedCount).Replace(' ', ' '));

    [Theory]
    [InlineData(2, 0, 1, "2 новых · 1 удаление")]
    [InlineData(0, 0, 3, "3 удаления")]
    [InlineData(1, 1, 5, "1 новое · 1 исправление · 5 удалений")]
    public void SummaryCountsRemovals(int added, int fixedCount, int removed, string expected) =>
        Assert.Equal(expected, UpdateWindow.Summary(added, fixedCount, removed).Replace(' ', ' '));

    /// <summary>
    /// Разделы чейнджлога — пункты меню окна, со значками меню (владелец 07.10).
    /// Окно обновления держит знаки у себя; здесь сверяется, что меню и список
    /// разделов не разошлись: новый пункт меню без раздела, переименованный
    /// или со сменённым значком — падение.
    /// </summary>
    [Fact]
    public void ChangelogSectionsMatchTheMenu()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);

        while (root is not null && !File.Exists(Path.Combine(root.FullName, "NetZapret.sln")))
            root = root.Parent;

        Assert.NotNull(root);

        var xaml = File.ReadAllText(Path.Combine(root!.FullName, "gui", "NetZapret.Gui", "MainWindow.xaml"));
        var items = System.Text.RegularExpressions.Regex.Matches(
            xaml, @"Style=""\{StaticResource RailItem\}"" Content=""([^""]+)"" Tag=""([^""]+)"" local:MenuIcon\.Glyph=""&#x([0-9A-Fa-f]+);""");

        Assert.True(items.Count >= 13, $"пунктов меню нашлось {items.Count}: разметка меню поменялась, тест её не узнаёт");

        foreach (System.Text.RegularExpressions.Match item in items)
        {
            var (name, key, code) = (item.Groups[1].Value, item.Groups[2].Value, item.Groups[3].Value);
            var category = NotesCategories.All.SingleOrDefault(c => c.Key == key);

            Assert.True(category is not null, $"у пункта меню «{name}» нет раздела чейнджлога");
            Assert.Equal(name, category!.Name);
            Assert.Equal(((char)Convert.ToInt32(code, 16)).ToString(), UpdateWindow.Glyphs[key]);
        }

        Assert.All(NotesCategories.All, c => Assert.True(UpdateWindow.Glyphs.ContainsKey(c.Key), $"у раздела «{c.Name}» нет значка"));
    }

    [Fact]
    public void ProgramSectionsBecomeCards()
    {
        Sta.Run(() =>
        {
            var window = new UpdateWindow(new ReleaseInfo
            {
                Version = "9.9.1",
                Tag = "v9.9.1",
                ArchiveUrl = "https://example/NetZapret-9.9.1.zip",
                Notes = "# Что нового\n\n## Десинк\n\n### Новое:\n\n**Первое.** Текст.\n\n## TG Proxy\n\n### Удаления:\n\n**Второе.** Текст.",
            });

            // Шапка версии и две карточки разделов.
            Assert.Equal(3, window.Notes.Children.Count);
            Assert.All(window.Notes.Children.OfType<System.Windows.UIElement>().Skip(1), c => Assert.IsType<System.Windows.Controls.Border>(c));

            window.Close();
        });
    }

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
