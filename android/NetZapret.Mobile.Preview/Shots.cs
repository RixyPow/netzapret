using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using NetZapret.Core;
using NetZapret.Mobile.Screens;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Preview;

/// <summary>
/// Снимки разделов в PNG без окна: Avalonia.Headless с настоящей отрисовкой Skia.
/// </summary>
/// <remarks>
/// Так смотрится раскладка без телефона и без эмулятора: 412×915 — частый
/// размер экрана Android в точках, 360×780 — узкий. Разделы, которые ходят
/// в сеть (проверка блокировок, замер скорости), здесь не запускаются:
/// проверка показывается на выдуманных итогах (<see cref="CheckSection.ShowSample"/>).
/// </remarks>
internal static class Shots
{
    public static int Run(string folder, FakeHost host, string files)
    {
        Directory.CreateDirectory(folder);

        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // Мастер первого запуска — на чистых настройках.
        var settingsPath = Path.Combine(files, AppSettings.DefaultPath);
        (AppSettings.Load(settingsPath) with { OnboardingDone = false }).Save(settingsPath);

        var (window, view) = Open(host, 412, 915);
        Shot(window, folder, "00-wizard-intro");

        if (view.Overlay.OfType<OnboardingView>().FirstOrDefault() is { } wizard)
        {
            wizard.GoTo(1);
            Shot(window, folder, "01-wizard-mode");
            wizard.GoTo(2);
            Shot(window, folder, "02-wizard-vpn");
        }

        window.Close();

        (AppSettings.Load(settingsPath) with { OnboardingDone = true }).Save(settingsPath);

        (window, view) = Open(host, 412, 915);
        Shot(window, folder, "03-home-stopped");

        host.SetStatus(FakeHost.Running());
        Pump(1500);
        Shot(window, folder, "04-home-running");

        view.SelectTab(MainView.VpnTab);
        Shot(window, folder, "05-vpn", 2500);

        if (view.Visible is VpnSection vpn)
        {
            vpn.ShowAddSheet();
            Shot(window, folder, "06-vpn-add");
            view.Back();
        }

        view.SelectTab(MainView.RoutesTab);
        Shot(window, folder, "07-routes", 6000);

        if (view.Visible is RoutesSection routes)
        {
            routes.ExpandFirst();
            Shot(window, folder, "08-routes-expanded");
            routes.ChooseFirst();
            Shot(window, folder, "09-routes-path");
            view.Back();
            routes.AddApp();
            Shot(window, folder, "10-routes-apps", 1500);
            view.Back();
        }

        view.SelectTab(MainView.CheckTab);
        Shot(window, folder, "11-check");

        if (view.Visible is CheckSection check)
        {
            check.ShowSample();
            Shot(window, folder, "12-check-results");
        }

        view.SelectTab(MainView.MoreTab);
        Shot(window, folder, "13-more");

        foreach (var (name, make) in new (string, Func<Section>)[]
        {
            ("14-settings", () => new SettingsSection(view)),
            ("15-dns", () => new DnsSection(view)),
            ("16-doctor", () => new DoctorSection(view)),
            ("17-speed", () => new SpeedSection(view)),
            ("18-log", () => new LogSection(view)),
            ("19-about", () => new AboutSection(view)),
        })
        {
            view.Open(make());
            Shot(window, folder, name, 1500);
            view.Back();
        }

        window.Close();

        host.FakeFacts = host.FakeFacts with { PrivateDns = "dns.adguard-dns.com", OtherVpnActive = true };
        host.SetStatus(EngineStatus.Stopped);
        (window, view) = Open(host, 360, 780);
        Shot(window, folder, "20-home-360-warnings");
        view.SelectTab(MainView.VpnTab);
        Shot(window, folder, "21-vpn-360", 2000);
        window.Close();

        Console.WriteLine("Снимки: " + folder);
        return 0;
    }

    private static (Window, MainView) Open(FakeHost host, double width, double height)
    {
        var view = new MainView(host);
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        Pump(800);
        return (window, view);
    }

    /// <summary>Прокачать очередь и отрисовку: фоновые чтения отдают результат через Post.</summary>
    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);

        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(30);
        }
        while (DateTime.UtcNow < until);

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    private static void Shot(Window window, string folder, string name, int settle = 700)
    {
        Pump(settle);
        var frame = window.CaptureRenderedFrame();

        if (frame is null)
        {
            Console.WriteLine("нет кадра: " + name);
            return;
        }

        var path = Path.Combine(folder, name + ".png");
        frame.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        Console.WriteLine(path);
    }
}
