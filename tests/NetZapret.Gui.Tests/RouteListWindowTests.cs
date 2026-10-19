using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using NetZapret.Core.Rules;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>«Добавить списком» (19.10): разметка разбирается, итог считается сразу и при правке.</summary>
public sealed class RouteListWindowTests
{
    [Fact]
    public void The_window_counts_what_will_be_added_before_anything_is_pressed()
    {
        Sta.Run(() =>
        {
            var window = new RouteListWindow("7tv.app\ncdn.7tv.app\n7tv.io\nkws2.pclead.co.uk", engine: null, [], folder: "Twitch", reserved: []);

            Assert.Equal("Добавить 2", ((Button)window.FindName("Confirm")).Content);
            Assert.Equal("Twitch", ((TextBox)window.FindName("FolderBox")).Text);
            Assert.Contains("прокси Telegram", ((TextBlock)window.FindName("Summary")).Text);
        });
    }

    [Fact]
    public void Turning_off_the_reduction_counts_exact_names()
    {
        Sta.Run(() =>
        {
            var window = new RouteListWindow("7tv.app\ncdn.7tv.app", engine: null, [], folder: null, reserved: []);
            var toSite = (ToggleButton)window.FindName("ToSite");

            toSite.IsChecked = false;
            toSite.RaiseEvent(new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));

            Assert.Equal("Добавить 2", ((Button)window.FindName("Confirm")).Content);
        });
    }

    [Fact]
    public void An_empty_window_has_nothing_to_add()
    {
        Sta.Run(() =>
        {
            UserRuleEntry[] own = [new() { Match = MatchKind.Domain, Value = "*.7tv.app", Mode = RoutingMode.Direct }];
            var window = new RouteListWindow(string.Empty, engine: null, own, folder: null, reserved: []);

            Assert.False(((Button)window.FindName("Confirm")).IsEnabled);
        });
    }
}
