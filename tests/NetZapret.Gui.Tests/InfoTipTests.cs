using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Подсказка значка «i» открывается щелчком, и щелчок не уходит в кнопку под ним
/// (жалоба 03.10: «значки вопроса не нажимаются и ничего не показывают»).
/// </summary>
public sealed class InfoTipTests
{
    private static MouseButtonEventArgs Press(RoutedEvent routed) =>
        new(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = routed };

    [Fact]
    public void A_click_opens_the_tip_and_stops_there()
    {
        Sta.Run(() =>
        {

            var info = new ContentControl { Content = "Пояснение" };
            info.SetResourceReference(FrameworkElement.StyleProperty, "Info");

            var window = new Window
            {
                Left = -6000, Width = 200, Height = 100, ShowInTaskbar = false,
                WindowStyle = WindowStyle.None, Content = info,
            };
            window.Show();
            info.ApplyTemplate();

            var border = (Border)VisualTreeHelper.GetChild(info, 0);
            var tip = (ToolTip)border.ToolTip;

            Assert.Equal(200, ToolTipService.GetInitialShowDelay(border));

            var down = Press(UIElement.PreviewMouseLeftButtonDownEvent);
            border.RaiseEvent(down);
            Assert.True(down.Handled);

            var up = Press(UIElement.PreviewMouseLeftButtonUpEvent);
            border.RaiseEvent(up);
            Assert.True(up.Handled);
            Assert.True(tip.IsOpen);

            // Второй щелчок закрывает.
            border.RaiseEvent(Press(UIElement.PreviewMouseLeftButtonUpEvent));
            Assert.False(tip.IsOpen);

            window.Close();
        });
    }
}
