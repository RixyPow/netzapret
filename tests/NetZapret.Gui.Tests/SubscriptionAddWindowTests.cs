using System.Windows;
using System.Windows.Controls;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>Окно добавления подписки (01.10): разметка разбирается, тип переключает поля.</summary>
public sealed class SubscriptionAddWindowTests
{
    [Fact]
    public void The_window_is_created_and_shows_subscription_fields_first()
    {
        Sta.Run(() =>
        {
            var window = new SubscriptionAddWindow([]);

            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("SubscriptionFields")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("KeyFields")).Visibility);
        });
    }

    [Fact]
    public void Choosing_keys_swaps_the_fields()
    {
        Sta.Run(() =>
        {
            var window = new SubscriptionAddWindow([]);

            ((ComboBox)window.FindName("KindBox")).SelectedIndex = 1;

            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("SubscriptionFields")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("KeyFields")).Visibility);
        });
    }
}

/// <summary>Окно выбора программы (01.10): разметка разбирается.</summary>
public sealed class ProgramPickerWindowTests
{
    [Fact]
    public void The_picker_is_created_without_throwing()
    {
        Sta.Run(() => Assert.NotNull(new ProgramPickerWindow()));
    }
}
