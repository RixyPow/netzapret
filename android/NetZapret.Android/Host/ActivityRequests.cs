using System.Collections.Concurrent;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace NetZapret.Android.Host;

/// <summary>
/// Запросы, на которые Android отвечает активности: разрешение на VPN
/// (StartActivityForResult) и разрешения (RequestPermissions) — как задачи.
/// </summary>
/// <remarks>
/// Ответ приходит в MainActivity.OnActivityResult и OnRequestPermissionsResult,
/// а ждёт его хозяин в StartAsync: между ними — задача по номеру запроса.
/// Номера из своего диапазона 0x4E00–0x4EFF («NZ»): Avalonia просит у той же
/// активности свои (выбор файлов), и ответы не должны перепутаться. Не больше
/// 16 бит — FragmentActivity отказывает в больших номерах.
/// </remarks>
internal static class ActivityRequests
{
    private const int First = 0x4E00;

    private static int _next = First;
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<Result>> Results = new();
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> Permissions = new();
    private static readonly Handler Main = new(Looper.MainLooper!);

    private static WeakReference<Activity>? _activity;

    /// <summary>Активность на экране — её зовут запросы; null — окна нет.</summary>
    public static Activity? Current =>
        _activity is not null && _activity.TryGetTarget(out var activity) && !activity.IsFinishing ? activity : null;

    public static void Attach(Activity activity) => _activity = new WeakReference<Activity>(activity);

    public static void Detach(Activity activity)
    {
        if (Current == activity)
            _activity = null;
    }

    /// <summary>Открыть системное окно и дождаться его ответа.</summary>
    public static async Task<Result> StartForResultAsync(Activity activity, Intent intent, CancellationToken cancellationToken)
    {
        int code = NextCode();
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        Results[code] = completion;

        Main.Post(() =>
        {
            try
            {
                activity.StartActivityForResult(intent, code);
            }
            catch (Java.Lang.Exception)
            {
                // Окна для этого запроса в системе нет — считаем отказом.
                if (Results.TryRemove(code, out var pending))
                    pending.TrySetResult(Result.Canceled);
            }
        });

        using var registration = cancellationToken.Register(() =>
        {
            if (Results.TryRemove(code, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });

        return await completion.Task;
    }

    /// <summary>Попросить разрешение; true — дано.</summary>
    public static async Task<bool> RequestPermissionAsync(Activity activity, string permission, CancellationToken cancellationToken)
    {
        if (activity.CheckSelfPermission(permission) == Permission.Granted)
            return true;

        int code = NextCode();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Permissions[code] = completion;

        Main.Post(() =>
        {
            try
            {
                activity.RequestPermissions([permission], code);
            }
            catch (Java.Lang.Exception)
            {
                if (Permissions.TryRemove(code, out var pending))
                    pending.TrySetResult(false);
            }
        });

        using var registration = cancellationToken.Register(() =>
        {
            if (Permissions.TryRemove(code, out var pending))
                pending.TrySetCanceled(cancellationToken);
        });

        return await completion.Task;
    }

    /// <summary>Из MainActivity.OnActivityResult; true — запрос наш.</summary>
    public static bool OnActivityResult(int requestCode, Result resultCode)
    {
        if (!Results.TryRemove(requestCode, out var pending))
            return false;

        pending.TrySetResult(resultCode);
        return true;
    }

    /// <summary>Из MainActivity.OnRequestPermissionsResult; true — запрос наш.</summary>
    public static bool OnPermissionsResult(int requestCode, Permission[] grantResults)
    {
        if (!Permissions.TryRemove(requestCode, out var pending))
            return false;

        pending.TrySetResult(grantResults.Length > 0 && grantResults.All(g => g == Permission.Granted));
        return true;
    }

    private static int NextCode()
    {
        // По кругу внутри своего диапазона: 256 одновременных запросов не бывает.
        int value = Interlocked.Increment(ref _next);
        return First + (value - First) % 0x100;
    }
}
