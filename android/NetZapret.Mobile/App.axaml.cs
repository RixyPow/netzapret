using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile;

/// <summary>
/// Приложение Avalonia — общее для телефона и окна просмотра на Windows.
/// </summary>
/// <remarks>
/// Хозяин (<see cref="IMobileHost"/>) ставится до запуска Avalonia: на Android —
/// в <c>Application.OnCreate</c> до базового вызова, в окне просмотра — в Main.
/// Пустой хозяин — ошибка сборки приложения, а не повод рисовать пустой экран:
/// падение здесь видно сразу, а немой экран разбирали бы часами.
/// </remarks>
public partial class App : Application
{
    /// <summary>Платформа: движки, VPN-служба, буфер обмена. Ставит хозяин до запуска.</summary>
    public static IMobileHost? Host { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        var host = Host ?? throw new InvalidOperationException(
            "App.Host не задан: хозяин платформы ставит его до запуска Avalonia.");

        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                single.MainView = new MainView(host);
                break;

            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = new Window
                {
                    Title = "NetZapret — телефон",
                    Width = 412,
                    Height = 915,
                    CanResize = true,
                    Content = new MainView(host),
                };
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
