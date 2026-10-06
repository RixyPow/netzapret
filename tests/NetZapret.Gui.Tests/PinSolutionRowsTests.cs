using System.Windows;
using NetZapret.Gui.Views;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Раздел «Пины напрямую» в «Настройках маршрутов».
/// </summary>
public sealed class PinSolutionRowsTests
{
    private static PinSolution Solution(string id, bool ipV6Only = false, bool mixed = false) => new()
    {
        Id = id,
        Name = id,
        Note = "Пояснение.",
        NeedsIpV6 = ipV6Only,
        Pins =
        [
            new PinSolutionGroup(
                ipV6Only ? ["2606:50c0:8000::154"]
                : mixed ? ["2a03:2880:f330:25:face:b00c:0:4420", "163.70.151.174"]
                : ["163.70.151.174"],
                [id + ".com"]),
        ],
    };

    private static PinSolutionState Off(PinSolution s) => new(0, s.Names.Count, 0);

    [Fact]
    public void IpV6OnlySolutionWaitsForTheCheck()
    {
        // Пока IPv6 не проверен, решение на одних адресах IPv6 не включить:
        // вслепую прописали бы адреса, до которых может не быть дороги.
        var rows = PinSolutionRows.Build([Solution("github", ipV6Only: true)], haveIpV6: null, Off);

        Assert.False(rows[0].Available);
        Assert.Equal("Проверяю, есть ли IPv6…", rows[0].Caption);
    }

    [Fact]
    public void WithoutIpV6TheReasonIsShown()
    {
        var rows = PinSolutionRows.Build([Solution("github", ipV6Only: true)], haveIpV6: false, Off);

        Assert.False(rows[0].Available);
        Assert.Equal("Нужен IPv6 — сейчас его нет", rows[0].Caption);
        Assert.True(rows[0].Fade < 1);
    }

    [Fact]
    public void MixedSolutionStaysAvailableWithoutIpV6()
    {
        // Instagram: адреса IPv6 и IPv4 — без IPv6 пишутся одни IPv4.
        var rows = PinSolutionRows.Build([Solution("instagram", mixed: true)], haveIpV6: false, Off);

        Assert.True(rows[0].Available);
    }

    [Fact]
    public void PinnedSolutionCanBeTurnedOffEvenWithoutIpV6()
    {
        // Включили при IPv6, сеть сменилась — снять обязаны давать всё равно.
        var rows = PinSolutionRows.Build(
            [Solution("github", ipV6Only: true)],
            haveIpV6: false,
            s => new PinSolutionState(1, 1, 0));

        Assert.True(rows[0].Available);
        Assert.True(rows[0].On);
        Assert.Equal("вкл.", rows[0].Word);
    }

    [Fact]
    public void PartlyPinnedSaysHowMuch()
    {
        var solution = Solution("x") with
        {
            Pins = [new PinSolutionGroup(["104.244.43.131"], ["x.com", "abs.twimg.com", "api.x.com"])],
        };

        var rows = PinSolutionRows.Build([solution], haveIpV6: true, s => new PinSolutionState(2, 3, 1));

        Assert.Equal("частично", rows[0].Word);
        Assert.Contains("Прибито 2 из 3 имён.", rows[0].Caption);
        Assert.Contains("Своих пинов на его имена: 1", rows[0].Caption);
    }

    [Fact]
    public void FirstRowHasNoDividerAbove()
    {
        var rows = PinSolutionRows.Build([Solution("a"), Solution("b")], haveIpV6: true, Off);

        Assert.Equal(Visibility.Collapsed, rows[0].LineShown);
        Assert.Equal(Visibility.Visible, rows[1].LineShown);
    }

    [Fact]
    public void TheWindowIsCreatedWithoutThrowing()
    {
        Sta.Run(() =>
        {
            var window = new RoutesSettingsWindow();
            window.Close();
        });
    }
}
