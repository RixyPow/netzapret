using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Книга маршрутов: чтение, запись, противоречия.
/// </summary>
/// <remarks>
/// <para>
/// Задумка владельца 21.09. Сейчас судьба имени собирается из пяти
/// источников, и порядок между ними лежит только в коде. Отсюда все
/// загадки того дня: Instagram был прибит в hosts и потому выведен
/// из-под десинка, рецепт из каталога не находился, потому что искали
/// в пресете.
/// </para>
/// <para>
/// Главное свойство книги — переносимость, и потому формат проверяется
/// придирчиво: файл будут править руками и пересылать друг другу.
/// </para>
/// </remarks>
public sealed class RouteBookTests
{
    [Fact]
    public void A_plain_line_is_read()
    {
        var book = RouteBookFile.Parse("routes:\n  discord: desync\n");

        var entry = Assert.Single(book.Entries);

        Assert.Equal("discord", entry.Name);
        Assert.Equal(RouteChoice.Desync, entry.Choice);
    }

    [Fact]
    public void A_name_without_a_dot_is_a_group()
    {
        // Правило грубое, зато объяснимо в одну строку, а домена без точки
        // не бывает.
        var book = RouteBookFile.Parse("routes:\n  discord: desync\n  discord.com: vpn\n");

        Assert.True(book.Entries[0].IsGroup);
        Assert.False(book.Entries[1].IsGroup);
    }

    [Fact]
    public void A_recipe_rides_after_the_route()
    {
        var book = RouteBookFile.Parse("routes:\n  instagram: desync hostfakesplit-stealth\n");

        Assert.Equal("hostfakesplit-stealth", book.Entries[0].Recipe);
    }

    [Fact]
    public void A_recipe_is_kept_only_for_desync()
    {
        // У прочих маршрутов рецепт смысла не имеет: через туннель он
        // не применяется, а прибитое имя выводится из-под десинка вовсе.
        // Сохранённый там обещал бы несделанное.
        var book = RouteBookFile.Parse("routes:\n  tmdb: pin что-то\n  x: vpn что-то\n");

        Assert.All(book.Entries, e => Assert.Null(e.Recipe));
    }

    [Theory]
    [InlineData("direct", RouteChoice.Direct)]
    [InlineData("desync", RouteChoice.Desync)]
    [InlineData("vpn", RouteChoice.Vpn)]
    [InlineData("pin", RouteChoice.Pin)]
    [InlineData("напрямую", RouteChoice.Direct)]
    [InlineData("десинк", RouteChoice.Desync)]
    [InlineData("впн", RouteChoice.Vpn)]
    [InlineData("DESYNC", RouteChoice.Desync)]
    public void Both_spellings_are_understood(string word, RouteChoice choice)
    {
        // Русские написания — уступка руке: человек, правящий файл после
        // окна, напишет «десинк» раньше, чем вспомнит про латиницу.
        Assert.Equal(choice, RouteBookFile.ChoiceOf(word));
    }

    [Fact]
    public void An_unknown_word_is_not_guessed()
    {
        Assert.Null(RouteBookFile.ChoiceOf("куда-нибудь"));
    }

    [Fact]
    public void An_unreadable_line_does_not_sink_the_rest()
    {
        // Файл переносят между версиями, и запись, которой эта версия
        // ещё не знает, не повод отказаться от остальных.
        var book = RouteBookFile.Parse(
            "routes:\n  discord: desync\n  что-то: невнятное\n  github: direct\n");

        Assert.Equal(2, book.Entries.Count);
        Assert.Equal("github", book.Entries[1].Name);
    }

    [Fact]
    public void Comments_and_blank_lines_are_ignored()
    {
        var book = RouteBookFile.Parse(
            "# заголовок\n\nroutes:\n  discord: desync  # почему\n\n  github: direct\n");

        Assert.Equal(2, book.Entries.Count);
        Assert.Equal("discord", book.Entries[0].Name);
    }

    [Fact]
    public void Own_groups_are_read()
    {
        var book = RouteBookFile.Parse(
            "groups:\n  work:\n    - jira.example.com\n    - wiki.example.com\n\n"
            + "routes:\n  work: vpn\n");

        Assert.Equal(["jira.example.com", "wiki.example.com"], book.Groups["work"]);
        Assert.Single(book.Entries);
    }

    [Fact]
    public void Written_and_read_back_it_is_the_same()
    {
        // Самое важное свойство: файл ездит между машинами, и проехать
        // он должен без потерь.
        var before = RouteBookFile.Parse(
            "groups:\n  work:\n    - jira.example.com\n\n"
            + "routes:\n  discord: desync hostfakesplit\n  work: vpn\n  tmdb: pin\n");

        var after = RouteBookFile.Parse(RouteBookFile.Write(before));

        Assert.Equal(before.Entries, after.Entries);
        Assert.Equal(before.Groups["work"], after.Groups["work"]);
    }

    [Fact]
    public void It_is_always_written_in_latin()
    {
        // Как бы ни было записано прочитанное: файл для обмена, и отданный
        // другому он должен читаться одинаково.
        var book = RouteBookFile.Parse("routes:\n  discord: десинк\n");

        Assert.Contains("discord: desync", RouteBookFile.Write(book));
        Assert.DoesNotContain("десинк", RouteBookFile.Write(book));
    }

    [Fact]
    public void A_star_prefix_is_dropped()
    {
        // Правило хранится как «*.example.com», но человек пишет
        // «example.com», и файл должен принимать оба.
        var book = RouteBookFile.Parse("routes:\n  \"*.example.com\": vpn\n");

        Assert.Equal("example.com", book.Entries[0].Name);
    }
}
