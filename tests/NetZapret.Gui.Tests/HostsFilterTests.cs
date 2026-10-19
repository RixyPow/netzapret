using NetZapret.Gui.Views;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Поиск и отбор по файлу hosts.
/// </summary>
/// <remarks>
/// Просьба владельца 21.09. На его машине в файле сто три наши записи
/// и семьсот восемьдесят две чужие; глазами такое не перебрать, а заглядывают
/// туда ровно тогда, когда что-то сломалось и времени нет.
/// </remarks>
public sealed class HostsFilterTests
{
    private static HostsLine Line(
        string name,
        string address,
        string? source = HostsTable.OurSource,
        bool enabled = true,
        bool repeat = false,
        HostsHealth health = HostsHealth.Unknown) =>
        new(new HostsRow
        {
            Line = 0,
            Name = name,
            Address = address,
            Enabled = enabled,
            Source = source,
            Ours = source == HostsTable.OurSource,
            Kind = HostsPinKind.Proxy,
            Repeat = repeat,
        })
        { Health = health };

    private static readonly HostsLine[] Rows =
    [
        Line("instagram.com", "163.70.151.174"),
        Line("www.instagram.com", "163.70.151.174", health: HostsHealth.Dead),
        Line("chatgpt.com", "104.18.32.7", source: "Zapret GUI", repeat: true),
        Line("rutracker.org", "195.82.146.214", source: null, enabled: false),
    ];

    [Fact]
    public void An_empty_query_hides_nothing()
    {
        // Пустой запрос — это «не ищем», а не «ничего не найдено».
        Assert.Equal(Rows.Length, HostsFilter.Apply(Rows, null).Count);
        Assert.Equal(Rows.Length, HostsFilter.Apply(Rows, string.Empty).Count);
        Assert.False(HostsFilter.Searching(null));
    }

    [Fact]
    public void Spaces_alone_are_not_a_query()
    {
        // Случайный пробел в поле иначе спрятал бы весь список,
        // и вкладка выглядела бы сломанной.
        Assert.False(HostsFilter.Searching("   "));
        Assert.Equal(Rows.Length, HostsFilter.Apply(Rows, "   ").Count);
    }

    [Fact]
    public void A_name_is_found()
    {
        var found = HostsFilter.Apply(Rows, "instagram");

        Assert.Equal(2, found.Count);
        Assert.All(found, r => Assert.Contains("instagram", r.Name));
    }

    [Fact]
    public void An_address_is_found_too()
    {
        // «Кто у меня прибит на этот адрес» — вопрос не реже обратного:
        // так ловятся адреса, оставшиеся от давно сменившейся сети доставки.
        Assert.Equal(2, HostsFilter.Apply(Rows, "163.70.151.174").Count);
        Assert.Equal(2, HostsFilter.Apply(Rows, "163.70").Count);
    }

    [Fact]
    public void Case_and_surrounding_spaces_do_not_matter()
    {
        Assert.Equal(2, HostsFilter.Apply(Rows, "InStaGram").Count);
        Assert.Equal(2, HostsFilter.Apply(Rows, "  instagram  ").Count);
    }

    [Fact]
    public void The_source_is_searchable()
    {
        Assert.Single(HostsFilter.Apply(Rows, "zapret gui"));
        Assert.Single(HostsFilter.Apply(Rows, "неизвестно"));
    }

    [Fact]
    public void Nothing_matching_gives_nothing()
    {
        Assert.Empty(HostsFilter.Apply(Rows, "такого-имени-нет"));
    }

    [Theory]
    [InlineData(HostsShow.All, 4)]
    [InlineData(HostsShow.Ours, 2)]
    [InlineData(HostsShow.Foreign, 2)]
    [InlineData(HostsShow.Off, 1)]
    [InlineData(HostsShow.Dead, 1)]
    [InlineData(HostsShow.Repeats, 1)]
    public void The_menu_picks_its_rows(HostsShow show, int count)
    {
        Assert.Equal(count, HostsFilter.Apply(Rows, null, show).Count);
    }

    [Fact]
    public void A_disabled_dead_address_is_not_dead()
    {
        // Выключенную запись Windows не читает: молчит ли её адрес — неважно.
        var off = Line("x.example", "1.2.3.4", enabled: false, health: HostsHealth.Dead);

        Assert.False(HostsFilter.Shows(off, HostsShow.Dead));
        Assert.Equal("Off", off.State);
    }

    [Fact]
    public void The_source_card_narrows_with_the_search()
    {
        Assert.Equal(2, HostsFilter.Apply(Rows, "instagram", HostsShow.All, HostsTable.OurSource).Count);
        Assert.Empty(HostsFilter.Apply(Rows, "instagram", HostsShow.All, "Zapret GUI"));
    }
}
