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
