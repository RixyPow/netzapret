namespace NetZapret.Mobile;

/// <summary>
/// Всё, что интерфейсу нужно от платформы: движок, VPN-служба, буфер обмена,
/// камера, список приложений.
/// </summary>
/// <remarks>
/// Интерфейс телефона живёт в библиотеке без Android API, чтобы его можно было
/// открыть на Windows с поддельным хозяином и потом перенести на iPhone. На
/// Android хозяина реализует NetZapret.Android (AndroidEngineHost), в окне
/// просмотра — FakeHost. Данные — правила, подписки, настройки — интерфейс
/// читает и пишет сам, через библиотеки src/: они общие с Windows.
/// </remarks>
public interface IMobileHost
{
    /// <summary>Папка данных: config/, presets/, engines/, runtime/ — как у установки Windows.</summary>
    string FilesDirectory { get; }

    /// <summary>Что с движками сейчас (из NzBox.status).</summary>
    EngineStatus Status { get; }

    event Action? StatusChanged;

    /// <summary>Поднять VPN-службу и движки; <c>null</c> — поднято, иначе причина словами.</summary>
    Task<string?> StartAsync(CancellationToken cancellationToken);

    Task StopAsync();

    /// <summary>Команда движку (группы, выбор сервера, замер) — JSON в обе стороны, см. android/DESIGN.md.</summary>
    Task<string> CommandAsync(string name, string argJson);

    /// <summary>Что известно о телефоне — для «Диагностики» и подсказок.</summary>
    PlatformFacts Facts();

    /// <summary>Установленные приложения — для маршрутов по приложению.</summary>
    IReadOnlyList<InstalledApp> InstalledApps();

    Task ShareFileAsync(string path, string mime);

    Task OpenUrlAsync(string url);

    Task<string?> ReadClipboardAsync();

    /// <summary>Снять QR-код камерой; <c>null</c> — не поддержано или отменено.</summary>
    Task<string?> ScanQrAsync();
}

/// <param name="State">stopped, starting, running, stopping, failed.</param>
public sealed record EngineStatus(
    string State,
    string? Error,
    DateTimeOffset? StartedAt,
    bool DesyncRunning,
    int DesyncRestarts,
    IReadOnlyList<string> Log)
{
    public static EngineStatus Stopped { get; } = new("stopped", null, null, false, 0, []);

    public bool IsRunning => State == "running";
}

/// <param name="PrivateDns">Частный DNS телефона: null — выключен, "auto" — автоматически, иначе имя сервера.</param>
/// <param name="BatteryOptimized">Система вправе усыплять приложение — VPN может падать в фоне.</param>
public sealed record PlatformFacts(
    bool Root,
    string? PrivateDns,
    bool BatteryOptimized,
    bool OtherVpnActive,
    bool AlwaysOn,
    string Abi,
    int Sdk,
    string AppVersion);

public sealed record InstalledApp(string Package, string Label, bool System);
