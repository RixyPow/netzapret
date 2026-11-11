using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Карточка «вышла новая версия»: когда показывать и что в ней писать.
/// </summary>
public sealed class UpdateNoticeTests
{
    private static ReleaseInfo Release(string version, string? notes = null) => new()
    {
        Version = version,
        Tag = "v" + version,
        ArchiveUrl = "https://example.com/NetZapret.zip",
        Notes = notes,
    };

    /// <summary>
    /// Сама при запуске ставится только выбранное в мастере, новее текущей
    /// и не пропущенное (12.11).
    /// </summary>
    [Fact]
    public void InstallsAtStartOnlyWhatWasChosen()
    {
        var newer = Release("999.0.0");
        var chosen = new AppSettings { AutoInstallUpdates = true, OnboardingDone = true };

        Assert.True(UpdateNotice.ShouldInstallAtStart(newer, chosen));

        Assert.False(UpdateNotice.ShouldInstallAtStart(newer, chosen with { AutoInstallUpdates = false }));
        Assert.False(UpdateNotice.ShouldInstallAtStart(newer, chosen with { CheckForUpdates = false }));
        Assert.False(UpdateNotice.ShouldInstallAtStart(newer, chosen with { OnboardingDone = false }));
        Assert.False(UpdateNotice.ShouldInstallAtStart(newer, chosen with { DismissedUpdate = "999.0.0" }));
        Assert.False(UpdateNotice.ShouldInstallAtStart(Release(UpdateCheck.Current), chosen));
        Assert.False(UpdateNotice.ShouldInstallAtStart(Release("0.0.1"), chosen));
        Assert.False(UpdateNotice.ShouldInstallAtStart(null, chosen));
    }

    /// <summary>
    /// У прежних пользователей поля в файле нет — автообновление выключено:
    /// их решение владелец велел не трогать (12.11).
    /// </summary>
    [Fact]
    public void OldSettingsFileReadsAutoInstallOff()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-autoinstall-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, """{ "OnboardingDone": true, "CheckForUpdates": true }""");
            Assert.False(AppSettings.Load(path).AutoInstallUpdates);

            (AppSettings.Load(path) with { AutoInstallUpdates = true }).Save(path);
            Assert.True(AppSettings.Load(path).AutoInstallUpdates);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>«Не сейчас» прячет эту версию, но не следующую.</summary>
    [Fact]
    public void DismissedVersionIsNotOfferedButTheNextIs()
    {
        var settings = new AppSettings { DismissedUpdate = "0.7.2" };

        Assert.False(UpdateNotice.ShouldOffer(Release("0.7.2"), settings));
        Assert.True(UpdateNotice.ShouldOffer(Release("0.7.3"), settings));
        Assert.False(UpdateNotice.ShouldOffer(null, settings));
    }

    /// <summary>
    /// В карточку — полужирные заголовки раздела «Новое», а не «Исправлений»:
    /// ради нового и обновляются.
    /// </summary>
    [Fact]
    public void HighlightsComeFromTheNewSection()
    {
        const string notes = """
            # Что нового

            ## Новое:

            **Автоподбор адреса для пина.** В окне пина…

            **Каталог адресов работает и без установленного Zapret.** С программой…

            **sing-box обновлён до 1.14.1-extended.** Бесплатный WARP…

            **«О программе»** на вкладке «Ещё»…

            ## Исправления:

            **Подписки на XHTTP заработали.** Серверы…
            """;

        Assert.Equal(
            ["Автоподбор адреса для пина", "Каталог адресов работает и без установленного Zapret", "sing-box обновлён до 1.14.1-extended"],
            UpdateNotice.Highlights(notes));
    }

    /// <summary>
    /// С 0.14.0 «Новое» есть у каждого раздела программы — главное берётся
    /// из всех по порядку, мимо их «Исправлений».
    /// </summary>
    [Fact]
    public void HighlightsComeFromEverySectionsNew()
    {
        const string notes = """
            # Что нового

            ## Десинк

            ### Исправления:

            **Щит узнаёт имя.** Текст.

            ### Новое:

            **Новый рецепт.** Текст.

            ## TG Proxy

            ### Новое:

            **Прокси для Telegram.** Текст.

            **Кнопка «Подключить».** Текст.
            """;

        Assert.Equal(
            ["Новый рецепт", "Прокси для Telegram", "Кнопка «Подключить»"],
            UpdateNotice.Highlights(notes));
    }

    [Fact]
    public void NoNotesNoHighlights()
    {
        Assert.Empty(UpdateNotice.Highlights(null));
        Assert.Empty(UpdateNotice.Highlights("просто текст без выделений"));
    }

    [Fact]
    public void PageLeadsToTheReleaseTag()
    {
        Assert.EndsWith("/releases/tag/v0.7.2", UpdateNotice.PageOf(Release("0.7.2")));
    }
}
