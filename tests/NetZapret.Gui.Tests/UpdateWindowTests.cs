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

            window.Close();
        });
    }
}
