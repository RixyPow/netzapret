using System.Text.Json;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AndroidX.Core.Content;
using NetZapret.Android.Startup;
using NetZapret.Mobile;

namespace NetZapret.Android.Host;

/// <summary>
/// Хозяин интерфейса на Android: VPN-служба, движки, разрешения, сведения
/// о телефоне (DESIGN.md §5).
/// </summary>
/// <remarks>
/// <para>
/// Один на процесс (<see cref="MainApplication.Host"/>): интерфейс, плитка
/// и служба живут в одном процессе (portability.md S9).
/// </para>
/// <para>
/// Состояние движков опрашивается у NzBox раз в секунду, пока интерфейс на
/// экране или идёт запуск и остановка; в фоне опрашивать некого — человек
/// не смотрит, а процессор телефона дорог. <see cref="StatusChanged"/>
/// приходит в главном потоке Android — он же поток Avalonia.
/// </para>
/// </remarks>
public sealed class AndroidEngineHost : IMobileHost
{
    private readonly Context _context;
    private readonly Handler _main = new(Looper.MainLooper!);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pollSync = new();

    private Timer? _poll;
    private bool _uiVisible;
    private int _busy;
    private string? _lastJson;
    private EngineStatus _status = EngineStatus.Stopped;

    /// <summary>Рассылка NzBox при смене состояния (только своему пакету).</summary>
    private const string StatusAction = "org.netzapret.android.STATUS_CHANGED";

    public AndroidEngineHost(Context context)
    {
        _context = context.ApplicationContext ?? context;
        FilesDirectory = _context.FilesDir!.AbsolutePath;

        // Смену состояния NzBox рассылает сам — между опросами раз в секунду
        // (и когда опроса нет вовсе: окно свёрнуто, VPN выключили из шторки)
        // интерфейс узнаёт о ней сразу.
        var receiver = new StatusReceiver(this);
        var filter = new IntentFilter(StatusAction);

        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            _context.RegisterReceiver(receiver, filter, ReceiverFlags.NotExported);
        else
            _context.RegisterReceiver(receiver, filter);
    }

    private sealed class StatusReceiver(AndroidEngineHost host) : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent) =>
            ThreadPool.QueueUserWorkItem(_ => host.Poll(null));
    }

    public string FilesDirectory { get; }

    public EngineStatus Status => _status;

    public event Action? StatusChanged;

    /// <summary>Заголовок уведомления VPN-службы.</summary>
    public string NotificationTitle { get; set; } = "NetZapret";

    /// <summary>Из MainActivity: интерфейс на экране — опрашивать движок; ушёл — перестать.</summary>
    public void SetUiVisible(bool visible)
    {
        lock (_pollSync)
        {
            _uiVisible = visible;
            UpdatePolling();
        }

        if (visible)
            Poll(null);
    }

    public async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken))
            return "Запуск или остановка уже идёт.";

        Busy(+1);

        try
        {
            if (AppStartup.PayloadError is { } broken)
                return broken;

            if (!NzBoxBridge.Available(out var noBridge))
                return noBridge;

            var vpn = await PrepareVpnAsync(cancellationToken);

            if (vpn is not null)
                return vpn;

            // Без уведомлений служба всё равно работает — только значка не видно.
            await RequestNotificationsAsync(cancellationToken);

            // Root спрашивается, только если человек включил nfqws2 в настройках
            // телефона: окно Magisk «разрешить?» на каждом телефоне с root без
            // спроса было бы навязчиво, а главное обязано работать и без него.
            var mobile = MobileSettings.Load(Path.Combine(FilesDirectory, MobileSettings.DefaultPath));
            bool root = mobile.UseRoot && await RootShell.HasRootAsync(cancellationToken);
            var (port, password) = EngineStart.NewSession();

            var request = new EngineStartRequest(
                FilesDirectory,
                _context.ApplicationInfo!.NativeLibraryDir!,
                _context.PackageName!,
                global::Android.OS.Process.MyUid(),
                global::Android.OS.Process.MyPid(),
                root,
                port,
                EngineStart.ByeDpiUser,
                password,
                mobile);

            AndroidJournal.Write("Запуск", "Десинк: " + mobile.DescribeDesync(root) + ".");

            var prepared = await Task.Run(() => EngineStart.PrepareAsync(request, cancellationToken), cancellationToken);

            if (prepared.Error is not null)
            {
                AndroidJournal.Write("Запуск", prepared.Error);
                return prepared.Error;
            }

            var config = await File.ReadAllTextAsync(prepared.ConfigPath!, cancellationToken);
            var rejected = await Task.Run(() => NzBoxBridge.CheckConfig(config), cancellationToken);

            if (!string.IsNullOrEmpty(rejected))
            {
                AndroidJournal.Write("Запуск", "Движок не принял конфиг: " + rejected);
                return "Движок не принял конфиг: " + rejected;
            }

            var json = EngineStart.RequestJson(FilesDirectory, prepared, NotificationTitle);
            var before = Refresh().State;
            await Task.Run(() => NzBoxBridge.Start(_context, json), cancellationToken);

            var final = await WaitForAsync(s => s is "running" or "failed" or "stopped", TimeSpan.FromSeconds(60), cancellationToken, before);

            return final.State switch
            {
                "running" => null,
                "failed" => final.Error ?? "Движок не поднялся.",
                "stopped" => final.Error ?? "Движок остановился, не поднявшись.",
                _ => "Движок не поднялся за минуту.",
            };
        }
        catch (System.OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            // Только тип и текст исключения: запрос с паролем ByeDPI в него не попадает.
            AndroidJournal.Write("Запуск", $"Сбой запуска: {error.GetType().Name}: {error.Message}");
            return $"Сбой запуска: {error.Message}";
        }
        finally
        {
            Busy(-1);
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        Busy(+1);

        try
        {
            if (!NzBoxBridge.Available(out _))
                return;

            await Task.Run(() => NzBoxBridge.Stop(_context));
            await WaitForAsync(s => s is "stopped" or "failed", TimeSpan.FromSeconds(15), CancellationToken.None, previous: null);
        }
        finally
        {
            Busy(-1);
            _gate.Release();
        }
    }

    public Task<string> CommandAsync(string name, string argJson) =>
        Task.Run(() => NzBoxBridge.Command(name, argJson));

    public PlatformFacts Facts()
    {
        string? privateDns = null;

        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(28) && PhysicalNetworkWatch.CurrentLinkProperties() is { } link && link.IsPrivateDnsActive)
                privateDns = string.IsNullOrEmpty(link.PrivateDnsServerName) ? "auto" : link.PrivateDnsServerName;
        }
        catch (Java.Lang.Exception)
        {
        }

        bool batteryOptimized = false;

        try
        {
            if (_context.GetSystemService(Context.PowerService) is PowerManager power)
                batteryOptimized = !power.IsIgnoringBatteryOptimizations(_context.PackageName);
        }
        catch (Java.Lang.Exception)
        {
        }

        // Своя VPN-сеть есть, пока движок поднят; всё сверх неё — чужой VPN.
        int vpns = PhysicalNetworkWatch.VpnNetworkCount();
        bool otherVpn = vpns > (_status.IsRunning ? 1 : 0);

        // Root — подтверждённый в этом процессе (договор PlatformFacts.Root):
        // «не спрашивали» тоже false. Признаков вроде «su лежит» здесь нет
        // нарочно: выключатель nfqws2 интерфейс показывает и без root (разбор
        // 27.10 — прежде круг: выключатель ждал root, root ждал выключателя),
        // а «Есть» в «Диагностике» должно значить «дали», а не «похоже».
        return new PlatformFacts(
            Root: RootShell.Known ?? false,
            PrivateDns: privateDns,
            BatteryOptimized: batteryOptimized,
            OtherVpnActive: otherVpn,
            AlwaysOn: AlwaysOn(),
            Abi: Build.SupportedAbis?.FirstOrDefault() ?? "unknown",
            Sdk: (int)Build.VERSION.SdkInt,
            AppVersion: AppVersion());
    }

    public IReadOnlyList<InstalledApp> InstalledApps()
    {
        var packages = _context.PackageManager!;

        IList<ApplicationInfo> installed = OperatingSystem.IsAndroidVersionAtLeast(33)
            ? packages.GetInstalledApplications(PackageManager.ApplicationInfoFlags.Of(0))
            : packages.GetInstalledApplications((PackageInfoFlags)0);

        return installed
            .Where(info => info.PackageName is not null)
            .Select(info => new InstalledApp(
                info.PackageName!,
                packages.GetApplicationLabel(info) ?? info.PackageName!,
                (info.Flags & ApplicationInfoFlags.System) != 0))
            .OrderBy(app => app.Label, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public Task ShareFileAsync(string path, string mime)
    {
        // Наружу — копия в cache/share: FileProvider открыт только на эту папку,
        // а files/ целиком — это config/ со ссылками подписок.
        var share = Path.Combine(_context.CacheDir!.AbsolutePath, "share");
        Directory.CreateDirectory(share);

        var copy = Path.Combine(share, Path.GetFileName(path));
        File.Copy(path, copy, overwrite: true);

        var uri = FileProvider.GetUriForFile(_context, _context.PackageName + ".share", new Java.IO.File(copy))!;

        var send = new Intent(Intent.ActionSend);
        send.SetType(mime);
        send.PutExtra(Intent.ExtraStream, uri);
        send.AddFlags(ActivityFlags.GrantReadUriPermission);

        var chooser = Intent.CreateChooser(send, "Поделиться")!;
        StartActivity(chooser);

        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url)
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(url)));
        }
        catch (ActivityNotFoundException)
        {
            // Браузера нет — ничего не открываем.
        }

        return Task.CompletedTask;
    }

    public Task<string?> ReadClipboardAsync()
    {
        // С Android 10 буфер читает только приложение в фокусе — и только
        // из главного потока.
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        _main.Post(() =>
        {
            try
            {
                var clipboard = (ClipboardManager?)_context.GetSystemService(Context.ClipboardService);
                var clip = clipboard?.PrimaryClip;

                completion.TrySetResult(clip is { ItemCount: > 0 }
                    ? clip.GetItemAt(0)?.CoerceToText(_context)?.ToString()
                    : null);
            }
            catch (Java.Lang.Exception)
            {
                completion.TrySetResult(null);
            }
        });

        return completion.Task;
    }

    /// <summary>QR пока не снимается: камера — отдельная библиотека, её ещё нет.</summary>
    public Task<string?> ScanQrAsync() => Task.FromResult<string?>(null);

    private async Task<string?> PrepareVpnAsync(CancellationToken cancellationToken)
    {
        var intent = global::Android.Net.VpnService.Prepare(_context);

        if (intent is null)
            return null;

        if (ActivityRequests.Current is not { } activity)
            return "Откройте NetZapret: разрешение на VPN спрашивается в его окне.";

        var result = await ActivityRequests.StartForResultAsync(activity, intent, cancellationToken);

        return result == Result.Ok
            ? null
            : "VPN не разрешён — без этого телефон не пустит трафик через NetZapret.";
    }

    private async Task RequestNotificationsAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || ActivityRequests.Current is not { } activity)
            return;

        await ActivityRequests.RequestPermissionAsync(activity, global::Android.Manifest.Permission.PostNotifications, cancellationToken);
    }

    private void StartActivity(Intent intent)
    {
        if (ActivityRequests.Current is { } activity)
        {
            activity.StartActivity(intent);
            return;
        }

        intent.AddFlags(ActivityFlags.NewTask);
        _context.StartActivity(intent);
    }

    /// <summary>
    /// Выбран ли NetZapret «постоянным VPN». Настройка скрытая: на части
    /// телефонов её не прочесть — тогда «нет».
    /// </summary>
    private bool AlwaysOn()
    {
        try
        {
            var app = global::Android.Provider.Settings.Secure.GetString(_context.ContentResolver, "always_on_vpn_app");
            return string.Equals(app, _context.PackageName, StringComparison.Ordinal);
        }
        catch (Java.Lang.Exception)
        {
            return false;
        }
    }

    private string AppVersion()
    {
        try
        {
            return _context.PackageManager!.GetPackageInfo(_context.PackageName!, (PackageInfoFlags)0)!.VersionName ?? "";
        }
        catch (Java.Lang.Exception)
        {
            return "";
        }
    }

    /// <param name="previous">Состояние до start(): пока оно не сменилось, прежнее «stopped» или «failed» — не итог нового запуска.</param>
    private async Task<EngineStatus> WaitForAsync(Func<string, bool> done, TimeSpan limit, CancellationToken cancellationToken, string? previous)
    {
        var begun = DateTime.UtcNow;
        var deadline = begun + limit;

        // Первые секунды после start() — ещё прежнее состояние: «stopped» или
        // «failed» прошлой попытки до того, как служба стала «starting», —
        // не итог этого запуска.
        var settle = begun + TimeSpan.FromSeconds(3);

        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);

        while (true)
        {
            var status = Refresh();

            bool early = previous is not null
                && (status.State is "stopped" or "failed")
                && status.State == previous
                && DateTime.UtcNow < settle;

            if (done(status.State) && !early)
                return status;

            if (DateTime.UtcNow >= deadline)
                return status;

            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
        }
    }

    private void Busy(int delta)
    {
        lock (_pollSync)
        {
            _busy += delta;
            UpdatePolling();
        }
    }

    private void UpdatePolling()
    {
        bool want = _uiVisible || _busy > 0;

        if (want && _poll is null)
            _poll = new Timer(Poll, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));
        else if (!want && _poll is not null)
        {
            _poll.Dispose();
            _poll = null;
        }
    }

    private void Poll(object? _)
    {
        try
        {
            Refresh();
        }
        catch (Exception error)
        {
            AndroidJournal.Write("Состояние", $"{error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>Спросить NzBox; изменилось — известить интерфейс в главном потоке.</summary>
    private EngineStatus Refresh()
    {
        var json = NzBoxBridge.Status();

        if (json == _lastJson)
            return _status;

        _lastJson = json;
        _status = ParseStatus(json);
        _main.Post(() => StatusChanged?.Invoke());

        return _status;
    }

    /// <summary>Ответ NzBox.status (DESIGN.md §4) — в EngineStatus интерфейса.</summary>
    internal static EngineStatus ParseStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            string state = Text(root, "state") ?? "stopped";
            string? error = Text(root, "error");

            DateTimeOffset? startedAt = root.TryGetProperty("startedAt", out var started)
                && started.ValueKind == JsonValueKind.Number
                && started.TryGetInt64(out var milliseconds)
                && milliseconds > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                    : null;

            bool desync = false;
            int restarts = 0;

            if (root.TryGetProperty("byedpi", out var byedpi) && byedpi.ValueKind == JsonValueKind.Object)
            {
                desync |= byedpi.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True;

                if (byedpi.TryGetProperty("restarts", out var count) && count.TryGetInt32(out var value))
                    restarts = value;
            }

            if (root.TryGetProperty("root", out var rooted) && rooted.ValueKind == JsonValueKind.Object)
                desync |= rooted.TryGetProperty("running", out var running) && running.ValueKind == JsonValueKind.True;

            var log = new List<string>();

            if (root.TryGetProperty("log", out var lines) && lines.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in lines.EnumerateArray())
                {
                    if (line.ValueKind == JsonValueKind.String)
                        log.Add(line.GetString()!);
                }
            }

            return new EngineStatus(state, error, startedAt, desync, restarts, log);
        }
        catch (JsonException)
        {
            return new EngineStatus("failed", "движок ответил непонятно", null, false, 0, []);
        }

        static string? Text(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }
}
