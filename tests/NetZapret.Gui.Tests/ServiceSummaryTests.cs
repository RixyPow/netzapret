using System.Windows.Media;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Шапка папки в «Маршрутах» говорит, куда идут части, без приёмов десинка.
/// </summary>
/// <remarks>
/// Владелец, 01.10: «не надо писать в секциях рецепты, сократи просто
/// до десинк, уже по конкретным маршрутам пиши». Прежде шапка Discord
/// перечисляла три приёма и подпись «по имени секции нет, по адресам —
/// может сработать», не влезала в строку и наползала на имя сервиса.
/// </remarks>
public sealed class ServiceSummaryTests
{
    private static PartRow Part(int choice, string mode, bool pinned = false) => new()
    {
        Key = "hostlist|список",
        Title = "часть",
        Detail = "часть",
        Mode = mode,
        Color = Brushes.Transparent,
        Choice = choice,
        Applied = choice,
        CanRoute = true,
        CanPin = true,
        Letter = "Ч",
        HasPin = pinned,
    };

    /// <summary>Разные приёмы — одна дорога: шапка Discord на 01.10.</summary>
    [Fact]
    public void Different_recipes_are_one_desync()
    {
        var discord = new ServiceRow("Discord",
        [
            Part(1, "десинк: hostfakesplit_multi"),
            Part(1, "десинк: hostfakesplit_multi"),
            Part(1, "десинк: hostfakesplit_multi"),
            Part(1, "десинк: send + syndata"),
            Part(1, "десинк: по имени секции нет, по адресам — может сработать"),
            Part(1, "десинк"),
        ]);

        Assert.Equal("всё десинк", discord.Summary);
    }

    [Fact]
    public void Different_routes_are_counted_by_route()
    {
        var service = new ServiceRow("Сервис",
        [
            Part(1, "десинк: multidisorder"),
            Part(1, "десинк: fake"),
            Part(2, "VPN"),
        ]);

        Assert.Equal("2 десинк · 1 VPN", service.Summary);
    }

    /// <summary>
    /// Прибитое так и называется — как и в строке части: адрес выбран
    /// руками, и искать причину, когда он протухнет, надо в hosts.
    /// </summary>
    [Fact]
    public void A_pinned_direct_part_is_named_so()
    {
        var service = new ServiceRow("Сервис",
        [
            Part(0, "прибит в hosts", pinned: true),
            Part(0, "напрямую"),
        ]);

        Assert.Equal("1 прибит в hosts · 1 напрямую", service.Summary);
    }
}
