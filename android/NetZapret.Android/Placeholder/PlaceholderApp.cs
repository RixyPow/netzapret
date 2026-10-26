using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using NetZapret.Mobile;
using Application = Avalonia.Application;
using Button = Avalonia.Controls.Button;
using ScrollViewer = Avalonia.Controls.ScrollViewer;
using TextBlock = Avalonia.Controls.TextBlock;
using Orientation = Avalonia.Layout.Orientation;

namespace NetZapret.Android.Placeholder;

/// <summary>
/// Заглушка интерфейса — пока в NetZapret.Mobile нет App (WP5 пишет его
/// параллельно). Собирается, только когда App там нет (.csproj, NzPlaceholderApp).
/// </summary>
/// <remarks>
/// Не интерфейс, а стенд хозяина: «Запустить», «Остановить», состояние,
/// сведения о телефоне и последние строки журнала движка — чтобы APK можно
/// было поставить и проверить VPN-службу, ByeDPI и root до готового интерфейса.
/// Удалить вместе с папкой, когда App появится.
/// </remarks>
public sealed class PlaceholderApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime single)
            single.MainView = new PlaceholderView(MainApplication.Host);

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class PlaceholderView : UserControl
{
    private readonly IMobileHost _host;
    private readonly TextBlock _state = new() { FontSize = 20, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _facts = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
    private readonly TextBlock _log = new() { TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("monospace"), FontSize = 11 };
    private readonly Button _start = new() { Content = "Запустить" };
    private readonly Button _stop = new() { Content = "Остановить" };

    public PlaceholderView(IMobileHost host)
    {
        _host = host;

        _start.Click += async (_, _) =>
        {
            _start.IsEnabled = false;
            _message.Text = "Запускаю…";
            var error = await _host.StartAsync(CancellationToken.None);
            _message.Text = error ?? "Поднято.";
            _start.IsEnabled = true;
            Show();
        };

        _stop.Click += async (_, _) =>
        {
            _stop.IsEnabled = false;
            await _host.StopAsync();
            _message.Text = "Остановлено.";
            _stop.IsEnabled = true;
            Show();
        };

        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16, 32, 16, 16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = "NetZapret — стенд хозяина", FontSize = 14, Opacity = 0.6 },
                    _state,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _start, _stop } },
                    _message,
                    _facts,
                    _log,
                },
            },
        };

        _host.StatusChanged += Show;
        Show();
    }

    private void Show()
    {
        var status = _host.Status;
        _state.Text = status.State + (status.DesyncRunning ? " · десинк работает" : "");

        if (status.Error is not null)
            _message.Text = status.Error;

        var facts = _host.Facts();
        _facts.Text = $"Android SDK {facts.Sdk}, {facts.Abi}, версия {facts.AppVersion}; root: {(facts.Root ? "да" : "нет/не спрашивали")}; "
            + $"частный DNS: {facts.PrivateDns ?? "выкл."}; экономия батареи: {(facts.BatteryOptimized ? "душит" : "нет")}; "
            + $"чужой VPN: {(facts.OtherVpnActive ? "да" : "нет")}";

        _log.Text = string.Join('\n', status.Log.TakeLast(30));
    }
}
