using System.Windows.Media;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Поиск по файлу hosts.
/// </summary>
/// <remarks>
/// Просьба владельца 21.09. На его машине в файле сто три наши записи
/// и семьсот восемьдесят две чужие; глазами такое не перебрать, а заглядывают
/// туда ровно тогда, когда что-то сломалось и времени нет.
/// </remarks>
public sealed class HostsFilterTests
{
    private static PinRow Row(string name, string address, string note = "не проверен") =>
        new(name, address, note, Brushes.Transparent);

    private static readonly PinRow[] Rows =
    [
        Row("instagram.com", "163.70.151.174"),
        Row("www.instagram.com", "163.70.151.174"),
        Row("chatgpt.com", "104.18.32.7"),
        Row("rutracker.org", "195.82.146.214", "чужая"),
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
        var found = HostsFilter.Apply(Rows, "163.70.151.174");

        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void Part_of_an_address_is_enough()
    {
        Assert.Equal(2, HostsFilter.Apply(Rows, "163.70").Count);
    }

    [Fact]
    public void Case_does_not_matter()
    {
        Assert.Equal(2, HostsFilter.Apply(Rows, "INSTAGRAM").Count);
        Assert.Equal(2, HostsFilter.Apply(Rows, "InStaGram").Count);
    }

    [Fact]
    public void Surrounding_spaces_are_ignored()
    {
        // Имя чаще всего вставляют из буфера, а туда оно попадает с пробелом.
        Assert.Equal(2, HostsFilter.Apply(Rows, "  instagram  ").Count);
    }

    [Fact]
    public void The_note_is_searchable()
    {
        // По слову «чужая» находится то, что ведёт кто-то ещё, — иначе
        // отделить своё от чужого можно только глазами по двум спискам.
        Assert.Single(HostsFilter.Apply(Rows, "чужая"));
    }

    [Fact]
    public void Nothing_matching_gives_nothing()
    {
        Assert.Empty(HostsFilter.Apply(Rows, "такого-имени-нет"));
    }

    private static NetZapret.Proxy.HostsEntry Entry(int line, string address, params string[] names) =>
        new() { Line = line, Address = address, Names = names, Enabled = true };

    private static readonly NetZapret.Proxy.HostsEntry[] Entries =
    [
        Entry(3, "72.56.93.144", "jetbrains.com", "academy.jetbrains.com"),
        Entry(4, "149.154.167.220", "t.me", "api.telegram.org"),
        Entry(5, "72.56.93.144", "notion.so"),
    ];

    /// <summary>Совпало имя — уходит только оно, сосед по строке остаётся.</summary>
    [Fact]
    public void Removal_by_name_takes_only_that_name()
    {
        Assert.Equal([(4, "t.me")], HostsFilter.Targets(Entries, "t.me"));
    }

    /// <summary>Совпал адрес — уходят все имена на нём.</summary>
    [Fact]
    public void Removal_by_address_takes_every_name_on_it()
    {
        var found = HostsFilter.Targets(Entries, "72.56.93.144");

        Assert.Equal(3, found.Count);
        Assert.Contains((5, "notion.so"), found);
    }

    /// <summary>
    /// Пустой запрос не отбирает ничего — иначе это «почистить всё».
    /// </summary>
    [Fact]
    public void An_empty_query_removes_nothing()
    {
        Assert.Empty(HostsFilter.Targets(Entries, null));
        Assert.Empty(HostsFilter.Targets(Entries, "   "));
    }
}
