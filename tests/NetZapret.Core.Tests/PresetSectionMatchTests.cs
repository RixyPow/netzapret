using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Какая секция пресета заберёт имя себе.
/// </summary>
/// <remarks>
/// <para>
/// Правило winws2: выигрывает первая совпавшая секция, дальше он не смотрит.
/// На этом обжигались трижды за двое суток — голос Discord чинился секцией,
/// до которой не доходила очередь; пять новых секций встали ниже «Моих
/// сайтов» и не работали ни дня; выбранный вручную рецепт перестал
/// находиться после перестановки секций.
/// </para>
/// <para>
/// Ответ на этот вопрос был в дереве дважды: PresetMatcher для консоли
/// и PresetZones для окна. Две реализации одного правила расходились
/// по существу — первая перечитывала списки с диска на каждое имя и не знала
/// обратного совпадения. Осталась одна, и эти проверки её и держат.
/// </para>
/// </remarks>
public sealed class PresetSectionMatchTests : IDisposable
{
    private readonly string _file = Path.Combine(
        Path.GetTempPath(), $"netzapret-preset-{Guid.NewGuid():N}.txt");

    public void Dispose()
    {
        if (File.Exists(_file))
            File.Delete(_file);
    }

    private PresetZones Zones(string text)
    {
        File.WriteAllText(_file, text);

        return PresetZones.Build(new PresetReader().Load(_file), zapretRoot: null);
    }

    /// <summary>Выигрывает первая совпавшая, а не самая точная.</summary>
    /// <remarks>
    /// Ровно та беда, ради которой строка и показывается: человек правит
    /// секцию, названную по имени сайта, а пакет забирает стоящая выше
    /// по большому списку.
    /// </remarks>
    [Fact]
    public void The_first_matching_section_wins()
    {
        var zones = Zones("""
            --lua-init=x

            --new
            --name=Discord
            --filter-tcp=443
            --hostlist-domains=discord.com
            --lua-desync=split

            --new
            --name=Обновления Discord
            --filter-tcp=443
            --hostlist-domains=updates.discord.com
            --lua-desync=fake
            """);

        var match = zones.MatchFor("updates.discord.com");

        Assert.NotNull(match);
        Assert.Equal(1, match!.Ordinal);
    }

    /// <summary>Запись означает зону: поддомен берёт та же секция.</summary>
    [Fact]
    public void An_entry_covers_its_subdomains()
    {
        var zones = Zones("""
            --lua-init=x

            --new
            --name=Discord
            --filter-tcp=443
            --hostlist-domains=discord.com
            --lua-desync=split
            """);

        Assert.NotNull(zones.MatchFor("cdn.discord.com"));
    }

    /// <summary>
    /// Обратное совпадение: запись секции лежит под проверяемым именем.
    /// </summary>
    /// <remarks>
    /// Этого прежняя консольная реализация не умела вовсе, и на одном
    /// и том же пресете две половины программы отвечали по-разному.
    /// </remarks>
    [Fact]
    public void A_section_entry_under_our_name_counts_too()
    {
        var zones = Zones("""
            --lua-init=x

            --new
            --name=Обновления Discord
            --filter-tcp=443
            --hostlist-domains=updates.discord.com
            --lua-desync=split
            """);

        Assert.NotNull(zones.MatchFor("discord.com"));
    }

    /// <summary>Чужая зона не совпадает, даже начинаясь теми же буквами.</summary>
    [Fact]
    public void A_different_zone_does_not_match()
    {
        var zones = Zones("""
            --lua-init=x

            --new
            --name=Discord
            --filter-tcp=443
            --hostlist-domains=discord.com
            --lua-desync=split
            """);

        Assert.Null(zones.MatchFor("notdiscord.com"));
        Assert.Null(zones.MatchFor("youtube.com"));
    }

    /// <summary>
    /// Секция, которая ничего не делает, так и называется.
    /// </summary>
    /// <remarks>
    /// «Не трогает» и «рецепта нет» — разные вещи, и путать их дорого:
    /// первое стоит там нарочно, второе означает недописанную секцию.
    /// </remarks>
    [Fact]
    public void A_pass_through_section_says_so()
    {
        var zones = Zones("""
            --lua-init=x

            --new
            --name=Ничего не делаем
            --filter-tcp=443
            --hostlist-domains=example.com
            --lua-desync=pass
            """);

        var match = zones.MatchFor("example.com");

        Assert.NotNull(match);
        Assert.True(match!.IsPassThrough);
        Assert.Contains("не трогает", match.Describe());
    }
}
