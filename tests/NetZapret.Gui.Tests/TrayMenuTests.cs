using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using NetZapret.Core.Rules;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Меню трея — окно WPF в стиле программы (24.09): создаётся, берёт цвета
/// темы и говорит о состоянии то же, что «Главная».
/// </summary>
public sealed class TrayMenuTests
{
    [Fact]
    public void TheMenuShowsTheStateInThemeColors()
    {
        Sta.Run(() =>
        {
            var menu = new TrayMenu(() => { }, _ => { }, () => { }, _ => { });

            menu.Render(new TrayStatus(
                Running: true,
                AllHealthy: true,
                Tunnel: ServiceHealth.Healthy,
                Desync: ServiceHealth.Healthy,
                Busy: false));

            var dot = (Ellipse)menu.FindName("Dot");
            var toggle = (Button)menu.FindName("Toggle");
            var accent = (SolidColorBrush)Application.Current.FindResource("Accent");

            // Цвет — из темы, а не зашитый: в этом и была вся правка.
            Assert.Equal(accent.Color, ((SolidColorBrush)dot.Fill).Color);
            Assert.Equal("Остановить", toggle.Content);
            Assert.Equal("работает", ((TextBlock)menu.FindName("Tunnel")).Text);

            menu.Render(new TrayStatus(
                Running: true,
                AllHealthy: false,
                Tunnel: ServiceHealth.Healthy,
                Desync: null,
                Busy: true));

            var warn = (SolidColorBrush)Application.Current.FindResource("Warn");

            Assert.Equal(warn.Color, ((SolidColorBrush)dot.Fill).Color);
            Assert.Equal("Останавливаю…", toggle.Content);
            Assert.False(toggle.IsEnabled);

            // Службы нет в состоянии — её выключили, а не остановили.
            Assert.Equal("выключен", ((TextBlock)menu.FindName("Desync")).Text);

            menu.Close();
        });
    }

    [Fact]
    public void StoppedSaysSoAndOffersToStart()
    {
        var status = new TrayStatus(false, false, null, null, Busy: false);

        Assert.Equal("Остановлено", status.Headline);

        Sta.Run(() =>
        {
            var menu = new TrayMenu(() => { }, _ => { }, () => { }, _ => { });
            menu.Render(status);

            Assert.Equal("Запустить", ((Button)menu.FindName("Toggle")).Content);
            Assert.Equal("—", ((TextBlock)menu.FindName("Tunnel")).Text);

            menu.Close();
        });
    }

    /// <summary>Режим выбирается в трее теми же тремя названиями, что на «Главной» (04.10).</summary>
    [Fact]
    public void ModesAreChosenByTheirNames()
    {
        Sta.Run(() =>
        {
            WorkMode? chosen = null;
            var menu = new TrayMenu(() => { }, mode => chosen = mode, () => { }, _ => { });
            menu.Render(new TrayStatus(Running: false, AllHealthy: false, Tunnel: null, Desync: null, Busy: false));

            Assert.Equal(WorkModes.RouteName, ((TextBlock)menu.FindName("ModeRouteName")).Text);

            ((RadioButton)menu.FindName("ModeRoute")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal(WorkMode.Route, chosen);

            menu.Close();
        });
    }
}

/// <summary>Мёртвые выходы VPN трей называет прямо, а не «с оговорками» (26.09).</summary>
public sealed class TrayDeadExitsTests
{
    [Fact]
    public void DeadExitsAreNamed()
    {
        var status = new TrayStatus(
            Running: true,
            AllHealthy: false,
            Tunnel: ServiceHealth.Degraded,
            Desync: ServiceHealth.Healthy,
            Busy: false);

        Assert.Equal("Выходы VPN не отвечают", status.Headline);
        Assert.Equal("выходы VPN не отвечают", TrayStatus.Describe(status.Tunnel, tunnel: true));
        Assert.Equal("не отвечает", TrayStatus.Describe(ServiceHealth.Degraded));
    }
}
