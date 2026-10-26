using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia.Android;
using NetZapret.Android.Host;

namespace NetZapret.Android;

/// <summary>
/// Окно приложения. Своего в нём немного: держать ссылку для системных
/// запросов (разрешение VPN, уведомления) и говорить хозяину, виден ли
/// интерфейс — пока виден, состояние движков опрашивается раз в секунду.
/// </summary>
/// <remarks>
/// Имя Java-класса задано, чтобы Java-часть (уведомление VPN-службы, плитка)
/// открывала окно по имени, а не по хэшу, который сборка дала бы классу сама.
/// </remarks>
[Activity(
    Name = "org.netzapret.android.MainActivity",
    Label = "NetZapret",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize)]
public sealed class MainActivity : AvaloniaMainActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        ActivityRequests.Attach(this);
        base.OnCreate(savedInstanceState);
    }

    protected override void OnResume()
    {
        base.OnResume();
        ActivityRequests.Attach(this);
        MainApplication.Host.SetUiVisible(true);
    }

    protected override void OnPause()
    {
        MainApplication.Host.SetUiVisible(false);
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        ActivityRequests.Detach(this);
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        if (!ActivityRequests.OnActivityResult(requestCode, resultCode))
            base.OnActivityResult(requestCode, resultCode, data);
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        if (!ActivityRequests.OnPermissionsResult(requestCode, grantResults))
            base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
    }
}
