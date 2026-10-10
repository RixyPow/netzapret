using NetZapret.Core.Connections;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Кольцо и правая колонка «Наблюдения» при «Куда: наружу» не считают
/// локальное и движок — владелец 10.10.
/// </summary>
public sealed class WatchSideTests
{
    [Fact]
    public void OutsideHidesLocalAndEngine() =>
        Assert.Equal([WatchRoute.Proxy, WatchRoute.Desync, WatchRoute.Direct], WatchView.SideRoutes(outsideOnly: true, route: null));

    [Fact]
    public void EverythingShowsAllFive() =>
        Assert.Equal(5, WatchView.SideRoutes(outsideOnly: false, route: null).Count);

    [Theory]
    [InlineData(WatchRoute.Proxy, 3)]
    [InlineData(WatchRoute.Local, 5)]
    [InlineData(WatchRoute.Engine, 5)]
    public void APickedRouteKeepsTheRingReadable(WatchRoute route, int shown) =>
        Assert.Equal(shown, WatchView.SideRoutes(outsideOnly: true, route).Count);
}
