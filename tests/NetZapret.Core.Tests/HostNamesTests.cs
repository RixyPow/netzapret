using NetZapret.Core.Services;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Цепочка зон для пробы рецептов.
/// </summary>
/// <remarks>
/// Заведена ради голоса Discord. Проверка брала для замера первую запись
/// списка, а ею оказался апекс зоны — единственное её имя, которое из-под
/// фильтра выпадает: замер с одного адреса, меняя только SNI, дал у апекса
/// 133 мс, у любого поддомена шесть секунд тишины, а у трёх контрольных
/// несуществующих имён в чужих зонах — 55 мс. Проверка отвечала «рецепт
/// не нужен» и не начиналась вовсе.
/// </remarks>
public class HostNamesTests
{
    /// <summary>
    /// Сперва само имя: если оно разрешается, предки не нужны.
    /// </summary>
    [Fact]
    public void ChainStartsWithTheNameItself()
    {
        Assert.Equal(
            ["probe.discord.media", "discord.media"],
            HostNames.ZoneChain("probe.discord.media"));
    }

    /// <summary>
    /// На двух метках обход кончается. Дальше пошли бы <c>media</c> и <c>com</c>:
    /// за ними либо не стоит никто, либо стоит реестр зоны, и его адрес дал бы
    /// замер о постороннем узле — хуже, чем никакого.
    /// </summary>
    [Fact]
    public void ChainStopsAtTheTwoLabelZone()
    {
        Assert.Equal(
            ["a.b.c.example.com", "b.c.example.com", "c.example.com", "example.com"],
            HostNames.ZoneChain("a.b.c.example.com"));

        Assert.Equal(["example.com"], HostNames.ZoneChain("example.com"));
    }

    /// <summary>Общий суффикс сам по себе — не зона, спрашивать про него нечего.</summary>
    [Fact]
    public void BareSuffixYieldsItselfAndNothingMore()
    {
        Assert.Equal(["com"], HostNames.ZoneChain("com"));
        Assert.Empty(HostNames.ZoneChain(string.Empty));
        Assert.Empty(HostNames.ZoneChain("   "));
    }

    /// <summary>Точки по краям — от записи вида <c>.discord.media</c> в списках.</summary>
    [Fact]
    public void EdgeDotsAreTrimmed()
    {
        Assert.Equal(
            ["probe.discord.media", "discord.media"],
            HostNames.ZoneChain(" .probe.discord.media. "));
    }

    /// <summary>
    /// У голоса Discord имя для пробы задано каталогом, а не взято из списка.
    /// Иначе проверка достаётся апексу, который не закрыт.
    /// </summary>
    [Fact]
    public void DiscordVoiceIsProbedBySubdomain()
    {
        var voice = ServiceCatalog.All
            .Single(s => s.Name == "Discord")
            .Parts
            .Single(p => p.List.EndsWith("discord-media.txt", StringComparison.Ordinal));

        Assert.NotNull(voice.Probe);
        Assert.EndsWith(".discord.media", voice.Probe);
        Assert.NotEqual("discord.media", voice.Probe);

        // И оно обязано вести к разрешимому предку, иначе соединяться не с кем.
        Assert.Contains("discord.media", HostNames.ZoneChain(voice.Probe!));
    }
}
