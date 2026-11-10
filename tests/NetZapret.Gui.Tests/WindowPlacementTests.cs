using System.Windows;
using NetZapret.Core;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Окно встаёт туда, где его оставили (10.11), — и развёрнутым, если было развёрнуто.
/// </summary>
public sealed class WindowPlacementTests
{
    [Fact]
    public void WindowOpensWhereItWasLeft()
    {
        Sta.Run(() =>
        {
            var place = new WindowPlace { Left = 120, Top = 90, Right = 1020, Bottom = 690 };

            var window = new Window { ShowInTaskbar = false, Width = 400, Height = 300, WindowStartupLocation = WindowStartupLocation.CenterScreen };
            WindowPlacement.Prepare(window, place);
            window.Show();

            try
            {
                Assert.Equal(place, WindowPlacement.Read(window));
                Assert.Equal(WindowState.Normal, window.WindowState);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void MaximizedComesBackMaximizedWithItsNormalPlace()
    {
        Sta.Run(() =>
        {
            var place = new WindowPlace { Left = 150, Top = 100, Right = 950, Bottom = 700, Maximized = true };

            var window = new Window { ShowInTaskbar = false };
            WindowPlacement.Prepare(window, place);
            window.Show();

            try
            {
                Assert.Equal(WindowState.Maximized, window.WindowState);

                // Развёрнутое окно помнит обычное место — туда оно вернётся.
                Assert.Equal(place, WindowPlacement.Read(window));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void NothingSavedLeavesTheWindowAlone()
    {
        Sta.Run(() =>
        {
            var window = new Window { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            WindowPlacement.Prepare(window, null);
            Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);

            WindowPlacement.Prepare(window, new WindowPlace { Left = 10, Top = 10, Right = 20, Bottom = 20 });
            Assert.Equal(WindowStartupLocation.CenterScreen, window.WindowStartupLocation);
        });
    }
}
