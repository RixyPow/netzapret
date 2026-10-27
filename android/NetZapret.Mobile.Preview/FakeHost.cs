using System.Text.Json.Nodes;

namespace NetZapret.Mobile.Preview;

/// <summary>
/// Поддельный хозяин для окна просмотра: движков нет, сеть не трогается.
/// </summary>
/// <remarks>
/// Запуск и остановка только меняют состояние, группы движка выдуманы — те же
/// серверы, что в поддельной подписке (<see cref="Program.SeedDemo"/>), чтобы
/// вкладка VPN и «Главная» показывали одно и то же. Ничего из этого не
/// уходит наружу: ни запросов, ни открытия ссылок в браузере.
/// </remarks>
internal sealed class FakeHost : IMobileHost
{
    private readonly object _gate = new();
    private string _selected = "auto-latency";

    /// <summary>Память замеров, как у движка: тег → задержка и время (секунды Unix).</summary>
    /// <remarks>
    /// Провала в ней нет: настоящий движок при провале запись стирает
    /// (daemon/started_service.go, URLTest), и просмотр делает так же —
    /// иначе вкладка VPN показывала бы здесь то, чего на телефоне не бывает.
    /// </remarks>
    private readonly Dictionary<string, (int Delay, long Time)> _history;

    /// <summary>Сервер, который на замере не отвечает — последний в списке.</summary>
    private readonly string _dead = Program.DemoServers[^1].Name;

    public FakeHost(string filesDirectory)
    {
        FilesDirectory = filesDirectory;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600;
        _history = Program.DemoServers
            .Select((s, i) => (s.Name, Delay: 40 + i * 37 % 260))
            .Where(x => x.Name != _dead)
            .ToDictionary(x => x.Name, x => (Delay: x.Delay, Time: now));
    }

    public string FilesDirectory { get; }

    public EngineStatus Status { get; private set; } = EngineStatus.Stopped;

    public event Action? StatusChanged;

    /// <summary>Что вернёт <see cref="Facts"/> — снимки ставят предупреждения на «Главную».</summary>
    public PlatformFacts FakeFacts { get; set; } = new(
        Root: false, PrivateDns: null, BatteryOptimized: true, OtherVpnActive: false,
        AlwaysOn: false, Abi: "arm64-v8a", Sdk: 35, AppVersion: "1.1.1 (просмотр)");

    public void SetStatus(EngineStatus status)
    {
        Status = status;
        StatusChanged?.Invoke();
    }

    public async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        SetStatus(Status with { State = "starting", Error = null });
        await Task.Delay(700, cancellationToken);
        SetStatus(Running());
        return null;
    }

    public static EngineStatus Running() => new(
        "running", null, DateTimeOffset.Now.AddMinutes(-17), true, 0,
        ["[просмотр] движок не настоящий: окно просмотра на Windows", "[просмотр] группы и задержки выдуманы"]);

    public async Task StopAsync()
    {
        SetStatus(Status with { State = "stopping" });
        await Task.Delay(300);
        SetStatus(EngineStatus.Stopped);
    }

    public Task<string> CommandAsync(string name, string argJson)
    {
        lock (_gate)
        {
            switch (name)
            {
                case "groups":
                    return Task.FromResult(Groups());

                case "select":
                    var outbound = (string?)JsonNode.Parse(argJson)?["outbound"];

                    if (outbound is null)
                        return Task.FromResult("""{"error":"нет outbound"}""");

                    _selected = outbound;
                    return Task.FromResult("{}");

                case "urltest":
                    var tag = (string?)JsonNode.Parse(argJson)?["group"];

                    if (tag is not { } measured || !Program.DemoServers.Any(s => s.Name == measured))
                        return Task.FromResult("""{"error":"outbound not found"}""");

                    // Как движок: команда отвечает сразу, итог — позже, в памяти
                    // замеров; провал стирает запись.
                    _ = Task.Delay(Random.Shared.Next(300, 1500)).ContinueWith(_ =>
                    {
                        lock (_gate)
                        {
                            if (measured == _dead)
                                _history.Remove(measured);
                            else
                                _history[measured] = (Random.Shared.Next(35, 320), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        }
                    }, TaskScheduler.Default);

                    return Task.FromResult("""{"ok":true}""");

                case "logs":
                    return Task.FromResult(new JsonArray([.. Status.Log.Select(l => (JsonNode?)l)]).ToJsonString());

                default:
                    return Task.FromResult("""{"error":"в окне просмотра не поддержано"}""");
            }
        }
    }

    private string Groups()
    {
        if (!Status.IsRunning)
            return """{"error":"движок не запущен"}""";

        var names = Program.DemoServers.Select(s => s.Name).ToList();
        var fastest = _history.Count == 0 ? names[0] : _history.OrderBy(d => d.Value.Delay).First().Key;

        // Как NzCommands.writeGroups: записи нет — delay и time нули.
        JsonObject Item(string tag, string type) => new()
        {
            ["tag"] = tag,
            ["type"] = type,
            ["delay"] = _history.TryGetValue(tag, out var seen) ? seen.Delay : 0,
            ["time"] = _history.TryGetValue(tag, out var at) ? at.Time : 0,
        };

        var selector = new JsonObject
        {
            ["tag"] = "auto",
            ["type"] = "selector",
            ["selected"] = _selected,
            ["items"] = new JsonArray([Item("auto-latency", "urltest"), .. names.Select(n => (JsonNode)Item(n, "vless"))]),
        };

        var latency = new JsonObject
        {
            ["tag"] = "auto-latency",
            ["type"] = "urltest",
            ["selected"] = fastest,
            ["items"] = new JsonArray([.. names.Select(n => (JsonNode)Item(n, "vless"))]),
        };

        return new JsonArray(selector, latency).ToJsonString();
    }

    public PlatformFacts Facts() => FakeFacts;

    public IReadOnlyList<InstalledApp> InstalledApps() =>
    [
        new("org.telegram.messenger", "Telegram", false),
        new("com.google.android.youtube", "YouTube", false),
        new("com.android.chrome", "Chrome", false),
        new("com.discord", "Discord", false),
        new("ru.sberbankmobile", "СберБанк", false),
        new("ru.rostel", "Госуслуги", false),
        new("com.valvesoftware.android.steam.community", "Steam", false),
        new("com.instagram.android", "Instagram", false),
        new("com.android.settings", "Настройки", true),
        new("com.google.android.gms", "Сервисы Google Play", true),
    ];

    public Task ShareFileAsync(string path, string mime)
    {
        Console.WriteLine($"[просмотр] поделиться файлом: {Path.GetFileName(path)} ({mime})");
        return Task.CompletedTask;
    }

    public Task OpenUrlAsync(string url)
    {
        // Ссылку не открываем: просмотр не ходит в сеть и не трогает браузер.
        Console.WriteLine("[просмотр] открыть ссылку: " + url);
        return Task.CompletedTask;
    }

    public Task<string?> ReadClipboardAsync() => Task.FromResult<string?>(null);

    public Task<string?> ScanQrAsync() => Task.FromResult<string?>(null);
}
