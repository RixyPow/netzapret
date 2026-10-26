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
    private readonly Dictionary<string, int> _delays;

    public FakeHost(string filesDirectory)
    {
        FilesDirectory = filesDirectory;
        _delays = Program.DemoServers.Select((s, i) => (s.Name, Delay: 40 + i * 37 % 260)).ToDictionary(x => x.Name, x => x.Delay);
        _delays[Program.DemoServers[^1].Name] = 65535;
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
                    var group = (string?)JsonNode.Parse(argJson)?["group"];

                    if (group is not null && _delays.ContainsKey(group))
                        _delays[group] = Random.Shared.Next(35, 320);

                    return Task.FromResult("{}");

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
        var fastest = _delays.Where(d => d.Value < 65535).OrderBy(d => d.Value).First().Key;

        JsonObject Item(string tag, string type) => new()
        {
            ["tag"] = tag,
            ["type"] = type,
            ["delay"] = _delays.TryGetValue(tag, out var ms) ? ms : 0,
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
