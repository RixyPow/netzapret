namespace NetZapret.Zapret;

/// <summary>
/// Управляет процессом winws2.
/// </summary>
/// <remarks>
/// Появился, когда задача сменилась с «ужиться с Zapret GUI» на «заменить его».
/// По форме это тот же супервизор, что и у sing-box: запустить с аргументами,
/// следить за живостью, остановить.
/// </remarks>
public interface IZapretOrchestrator : IAsyncDisposable
{
    ZapretState State { get; }

    /// <summary>Активный пресет либо <c>null</c>, если десинк выключен.</summary>
    ZapretPreset? ActivePreset { get; }

    Task StartAsync(ZapretPreset preset, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public enum ZapretState
{
    Stopped,
    Starting,
    Running,
    Faulted,
}

/// <summary>
/// Расположение установки Zapret.
/// </summary>
public sealed record ZapretPaths
{
    /// <summary>Корень установки; относительно него заданы пути списков в пресетах.</summary>
    public required string Root { get; init; }

    public string PresetDirectory => Path.Combine(Root, "presets", "winws2");

    public string ListDirectory => Path.Combine(Root, "lists");

    public string ExecutablePath => Path.Combine(Root, "winws2.exe");

    /// <summary>
    /// Ищет установку Zapret в обычных местах.
    /// </summary>
    public static ZapretPaths? Discover(string? hint = null)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(hint))
            candidates.Add(hint);

        candidates.Add(@"C:\Zapret\Dev");
        candidates.Add(@"C:\Zapret");

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(Path.Combine(candidate, "presets", "winws2")))
                return new ZapretPaths { Root = candidate };
        }

        return null;
    }
}
