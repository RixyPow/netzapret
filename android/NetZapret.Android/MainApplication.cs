using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;
using NetZapret.Android.Host;
using NetZapret.Android.Startup;
#if NZ_PLACEHOLDER_APP
using UiApp = NetZapret.Android.Placeholder.PlaceholderApp;
#else
using UiApp = NetZapret.Mobile.App;
#endif

namespace NetZapret.Android;

/// <summary>
/// Приложение: поставка и крючки библиотек — раньше всего остального,
/// потом Avalonia.
/// </summary>
/// <remarks>
/// Процесс поднимается не только окном: VPN-служба с «постоянным VPN»,
/// плитка быстрых настроек и запуск после загрузки приходят сюда же, мимо
/// MainActivity. Поэтому распаковка и крючки — в OnCreate приложения: любой
/// путь в процесс застаёт библиотеки уже настроенными.
/// Имя Java-класса задано, чтобы на него могла сослаться Java-часть (WP3).
/// </remarks>
[Application(Name = "org.netzapret.android.NzApplication", Label = "NetZapret", Icon = "@drawable/icon")]
public sealed class MainApplication : AvaloniaAndroidApplication<UiApp>
{
    private static AndroidEngineHost? _host;

    public MainApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    /// <summary>Хозяин на весь процесс — интерфейсу, плитке и службе один.</summary>
    public static AndroidEngineHost Host =>
        _host ?? throw new InvalidOperationException("Хозяин ещё не создан: MainApplication.OnCreate не выполнялся.");

    public override void OnCreate()
    {
        AppStartup.Run(this);

        // Класс NzBox — из главного потока, пока загрузчик классов приложения
        // под рукой (NzBoxBridge.Initialize).
        NzBoxBridge.Initialize();
        _host = new AndroidEngineHost(this);

#if !NZ_PLACEHOLDER_APP
        // Интерфейс берёт хозяина отсюда в OnFrameworkInitializationCompleted:
        // до base.OnCreate, который и поднимает Avalonia.
        UiApp.Host = _host;
#endif

        base.OnCreate();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder);
}
