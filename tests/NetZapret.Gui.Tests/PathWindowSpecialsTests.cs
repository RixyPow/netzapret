using System.Windows.Controls;
using NetZapret.Core.Rules;
using NetZapret.Gui.Views;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Особые пути в окне пути (владелец 12.11): пин сервиса, пин посредника, TG Proxy —
/// только у тех маршрутов, где они есть, между десинком и VPN.
/// </summary>
public sealed class PathWindowSpecialsTests
{
    private static readonly PinSolution Instagram = new()
    {
        Id = "instagram",
        Name = "Instagram",
        Note = "Адреса самой Meta.",
        Pins = [new PinSolutionGroup(["163.70.151.174"], ["instagram.com", "www.instagram.com"])],
    };

    /// <summary>Заголовки карточек путей — по порядку.</summary>
    private static IReadOnlyList<string> Heads(PathWindow window) =>
        ((StackPanel)window.FindName("Options")).Children
            .OfType<Button>()
            .Select(b => b.Content)
            .OfType<Grid>()
            .Select(g => g.Children.OfType<StackPanel>().First())
            .Select(text => text.Children.OfType<StackPanel>().First().Children.OfType<TextBlock>().First().Text)
            .ToList();

    [Fact]
    public void A_plain_route_has_no_special_paths()
    {
        Sta.Run(() =>
        {
            var window = new PathWindow("Сайт", PathPick.Desync, null, "десинк", true, "десинк", null);

            Assert.Equal(["Напрямую", "Десинк", "Через VPN", "Выключено"], Heads(window));
        });
    }

    [Fact]
    public void Special_paths_stand_between_desync_and_vpn()
    {
        Sta.Run(() =>
        {
            var window = new PathWindow("Instagram", PathPick.Desync, null, "десинк", true, "десинк", null,
                probe: "instagram.com",
                specials: new PathSpecials
                {
                    Solution = Instagram,
                    RelayPicker = () => throw new InvalidOperationException("окно подбора в тесте не открывается"),
                    TgProxy = true,
                });

            Assert.Equal(["Напрямую", "Десинк", "Пин сервиса", "Пин посредника", "TG Proxy", "Через VPN", "Выключено"], Heads(window));
        });
    }

    /// <summary>Нынешний пин посредника назван в его карточке — какой и на скольких именах.</summary>
    [Fact]
    public void The_current_relay_pin_is_named()
    {
        Sta.Run(() =>
        {
            var window = new PathWindow("Claude", PathPick.RelayPin, null, "VPN", true, "десинк", null,
                specials: new PathSpecials
                {
                    RelayPicker = () => throw new InvalidOperationException(),
                    Current = RouteSpecial.RelayPin,
                    CurrentPin = "Zapret DNS",
                    CurrentNames = 48,
                });

            var texts = ((StackPanel)window.FindName("Options")).Children
                .OfType<Button>()
                .SelectMany(b => Texts(b))
                .ToList();

            Assert.Contains("Сейчас: Zapret DNS — 48 имён.", texts);
            Assert.Equal(PathPick.RelayPin, window.Picked);
        });
    }

    private static IEnumerable<string> Texts(System.Windows.DependencyObject root)
    {
        if (root is TextBlock text)
            yield return text.Text;

        var children = root is ContentControl { Content: System.Windows.DependencyObject content }
            ? [content]
            : root is Panel panel
                ? panel.Children.Cast<System.Windows.DependencyObject>()
                : Enumerable.Empty<System.Windows.DependencyObject>();

        foreach (var child in children)
        {
            foreach (var inner in Texts(child))
                yield return inner;
        }
    }
}
