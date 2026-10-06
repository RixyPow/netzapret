using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Данные окна «Доступно обновление»: выпуски между версиями и разбор чейнджлога.
/// </summary>
/// <remarks>
/// Владелец 06.10, по образцу Zapret GUI: окно показывает «Что нового»
/// за каждую версию между установленной и новой, а не одну последнюю.
/// </remarks>
public sealed class UpdateWindowDataTests
{
    private const string Releases = """
        [
          { "tag_name": "v0.13.2", "draft": true, "prerelease": false, "body": "черновик", "assets": [] },
          { "tag_name": "v0.13.1", "draft": false, "prerelease": false, "body": "## Новое:\n\n**Пины напрямую.** Раздел.",
            "published_at": "2026-10-06T18:00:00Z",
            "assets": [ { "name": "NetZapret-0.13.1.zip", "browser_download_url": "https://example/a.zip", "size": 110000000 } ] },
          { "tag_name": "v0.13.0", "draft": false, "prerelease": false, "body": "## Исправления:\n\n**Мастер.**",
            "assets": [ { "name": "NetZapret-0.13.0.zip", "browser_download_url": "https://example/b.zip", "size": 1 } ] },
          { "tag_name": "v0.12.3", "draft": false, "prerelease": true, "body": "предварительный", "assets": [] },
          { "tag_name": "v0.12.2", "draft": false, "prerelease": false, "body": "старое", "assets": [] }
        ]
        """;

    [Fact]
    public void DraftsAndPrereleasesAreSkipped()
    {
        var all = UpdateCheck.FromReleases(Releases);

        Assert.Equal(["0.13.1", "0.13.0", "0.12.2"], all.Select(r => r.Version));
        Assert.Equal("https://example/a.zip", all[0].ArchiveUrl);
        Assert.Equal(110000000, all[0].ArchiveSize);
        Assert.Equal(string.Empty, all[2].ArchiveUrl);
    }

    [Fact]
    public void BetweenTakesNewerThanCurrentUpToLatest()
    {
        var all = UpdateCheck.FromReleases(Releases);

        var between = UpdateCheck.Between(all, current: "0.12.2", latest: "0.13.1");

        Assert.Equal(["0.13.1", "0.13.0"], between.Select(r => r.Version));
        Assert.Empty(UpdateCheck.Between(all, current: "0.13.1", latest: "0.13.1"));
    }

    [Fact]
    public void BetweenOrdersByNumberNotByText()
    {
        // «0.10.0» новее «0.9.2», хотя как строка меньше.
        var all = new[] { "0.9.2", "0.10.0", "0.9.10" }
            .Select(v => new ReleaseInfo { Version = v, Tag = "v" + v, ArchiveUrl = "" })
            .ToList();

        Assert.Equal(["0.10.0", "0.9.10", "0.9.2"], UpdateCheck.Between(all, "0.9.0", "0.10.0").Select(r => r.Version));
    }

    [Fact]
    public void NotesAreSplitIntoHeadingsAndParagraphs()
    {
        var blocks = ReleaseNotesText.Parse("""
            # Что нового

            ## Новое:

            **Пины напрямую.** Имя прибивается к адресу
            самого сервиса.

            - пункт со **словом**

            ## Исправления:

            **Карточки видны.** Прежде [не были](https://example).
            """);

        Assert.Equal(
            [NotesBlockKind.Heading, NotesBlockKind.Paragraph, NotesBlockKind.Bullet, NotesBlockKind.Heading, NotesBlockKind.Paragraph],
            blocks.Select(b => b.Kind));

        Assert.Equal("Новое", blocks[0].Plain);
        Assert.Equal("Пины напрямую. Имя прибивается к адресу самого сервиса.", blocks[1].Plain);
        Assert.True(blocks[1].Spans[0].Bold);
        Assert.False(blocks[1].Spans[1].Bold);
        Assert.Equal(["пункт со ", "словом"], blocks[2].Spans.Select(s => s.Text));
        Assert.Equal("Карточки видны. Прежде не были.", blocks[4].Plain);
    }

    [Fact]
    public void UnclosedStarsStayPlain()
    {
        var block = Assert.Single(ReleaseNotesText.Parse("**Начато и не закрыто"));

        Assert.All(block.Spans, s => Assert.False(s.Bold));
    }

    [Fact]
    public void CodeBlocksKeepTheirLines()
    {
        // Так release.cmd дописывает к чейнджлогу хэши: прежде строки блока
        // склеивались в абзац вместе с обратными кавычками (снимок владельца 06.10).
        var blocks = ReleaseNotesText.Parse("""
            ## Чем это собрано и как сверить

            ```
            NetZapret-0.13.0.zip   3C467F94
            NetZapret.exe   26A7F2FE
            ```

            Совпасть должен `NetZapret.exe`. Архив — нет.

            ```Get-FileHash a.zip```
            """);

        Assert.Equal(
            [NotesBlockKind.Heading, NotesBlockKind.Code, NotesBlockKind.Paragraph, NotesBlockKind.Code],
            blocks.Select(b => b.Kind));

        Assert.Equal("NetZapret-0.13.0.zip   3C467F94\nNetZapret.exe   26A7F2FE", blocks[1].Plain);
        Assert.Equal("Get-FileHash a.zip", blocks[3].Plain);

        var inline = blocks[2].Spans;
        Assert.Equal(["Совпасть должен ", "NetZapret.exe", ". Архив — нет."], inline.Select(s => s.Text));
        Assert.True(inline[1].Code);
        Assert.False(inline[1].Bold);
    }

    [Fact]
    public void StarsInsideCodeAreNotBold()
    {
        var spans = Assert.Single(ReleaseNotesText.Parse("Маска `**/*.cs` и всё.")).Spans;

        Assert.Equal("**/*.cs", spans[1].Text);
        Assert.All(spans, s => Assert.False(s.Bold));
    }

    [Fact]
    public void BoldLeadIsSeparatedFromTheBody()
    {
        var block = Assert.Single(ReleaseNotesText.Parse("**Пины напрямую.** Имя прибивается к адресу."));

        Assert.Equal("Пины напрямую.", block.Lead);
        Assert.Equal(" Имя прибивается к адресу.", block.Body.Single().Text);

        Assert.Null(Assert.Single(ReleaseNotesText.Parse("Просто текст.")).Lead);
    }

    [Fact]
    public void ItemsAreCountedPerSection()
    {
        var (added, fixedCount, _) = ReleaseNotesText.Count("""
            # Что нового

            ## Новое:

            **Первое.** Текст.

            **Второе.** Текст.

            - третье пунктом

            ## Исправления:

            **Починено.** Текст.

            ## Чем это собрано и как сверить

            **Не пункт чейнджлога.** Хэши.
            """);

        Assert.Equal(3, added);
        Assert.Equal(1, fixedCount);
        Assert.Equal(new NotesTally(0, 0, 0), ReleaseNotesText.Count(null));
    }

    /// <summary>
    /// С 0.14.0 чейнджлог делится по разделам программы, а внутри — «Новое»,
    /// «Исправления», «Удаления» (владелец 07.10).
    /// </summary>
    private const string ByCategory = """
        # Что нового

        ## Десинк

        ### Новое:

        **Новый рецепт.** Текст.

        ### Исправления:

        **Щит узнаёт имя.** Текст.

        ## ВПН

        ### Новое:

        **Замер выхода.** Текст.

        ### Исправления:

        - мелочь пунктом

        ### Удаления:

        **Старый выключатель.** Текст.

        ## Чем это собрано и как сверить

        ```
        NetZapret-0.14.0.zip   ABC
        ```
        """;

    [Fact]
    public void ChangelogIsSplitByProgramSections()
    {
        var sections = ReleaseNotesText.Sections(ByCategory);

        Assert.Equal(["desync", "vpn", null], sections.Select(s => s.Category?.Key));

        // Заголовок раздела в блоки не входит: окно рисует его карточкой.
        Assert.Equal(
            ["Новое", "Новый рецепт. Текст.", "Исправления", "Щит узнаёт имя. Текст."],
            sections[0].Blocks.Select(b => b.Plain));

        // «ВПН» владелец пишет по-русски, в меню — VPN.
        Assert.Equal("VPN", sections[1].Category!.Name);
        Assert.Equal(6, sections[1].Blocks.Count);

        // Подвал с хэшами — не часть раздела VPN.
        Assert.Equal([NotesBlockKind.Heading, NotesBlockKind.Code], sections[2].Blocks.Select(b => b.Kind));
    }

    [Fact]
    public void ItemsAreCountedAcrossSections()
    {
        Assert.Equal(new NotesTally(2, 2, 1), ReleaseNotesText.Count(ByCategory));
    }

    [Fact]
    public void OldChangelogsStayOnePieceWithoutSection()
    {
        // Чейнджлоги до 0.14.0 разделов не знают — окно показывает их как прежде.
        var section = Assert.Single(ReleaseNotesText.Sections("# Что нового\n\n## Новое:\n\n**Пункт.** Текст.\n\n## Исправления:\n\n**Починено.** Текст."));

        Assert.Null(section.Category);
        Assert.Equal(4, section.Blocks.Count);
    }

    [Theory]
    [InlineData("1. Десинк:", "desync")]
    [InlineData("Файл hosts", "hosts")]
    [InlineData("Прокси Telegram", "tgproxy")]
    [InlineData("TG Proxy", "tgproxy")]
    [InlineData("vpn", "vpn")]
    [InlineData("Новое", null)]
    [InlineData("Чем это собрано и как сверить", null)]
    public void SectionsAreFoundByMenuNames(string heading, string? key) =>
        Assert.Equal(key, NotesCategories.Find(heading)?.Key);

    [Theory]
    [InlineData("1.1 Новое:", NotesChange.Added)]
    [InlineData("Исправления", NotesChange.Fixed)]
    [InlineData("2.3 Удаления:", NotesChange.Removed)]
    [InlineData("Десинк", NotesChange.None)]
    public void ChangeKindsAreFoundEvenNumbered(string heading, NotesChange change) =>
        Assert.Equal(change, NotesCategories.ChangeOf(heading));

    [Fact]
    public void EmptyNotesGiveNothing()
    {
        Assert.Empty(ReleaseNotesText.Parse(null));
        Assert.Empty(ReleaseNotesText.Parse("# Что нового\n\n"));
    }
}
