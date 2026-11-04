using System.Diagnostics;
using System.Text;
using NetZapret.Core;

namespace NetZapret.Supervisor;

/// <summary>
/// dnscrypt-proxy под надзором — резолвер движка в режимах «Шифрованные» (<see cref="DnsCrypt"/>).
/// </summary>
/// <remarks>
/// Поднимается раньше sing-box: тот на первом же запросе спросит 127.0.0.1, и без
/// прокси всё до его готовности разрешалось бы в пустоту. Готов, когда назвал
/// число живых серверов (<see cref="DnsCrypt.ReadyMarker"/>), у владельца 04.11 за 3–4 с.
/// Здоровье — жив ли процесс: серверы он перебирает сам.
/// </remarks>
public sealed class DnsCryptService : SupervisedService
{
    private readonly string _executablePath;
    private readonly DnsCryptMode _mode;
    private readonly string _shippedDirectory;
    private readonly string _directory;

    public DnsCryptService(string executablePath, DnsCryptMode mode, string shippedDirectory, string directory)
    {
        _executablePath = executablePath;
        _mode = mode;
        _shippedDirectory = shippedDirectory;
        _directory = Path.GetFullPath(directory);
    }

    public const string ServiceName = "dnscrypt";

    public override string Name => ServiceName;

    public override IReadOnlyList<string> EngineProcessNames => [DnsCrypt.ProcessName];

    private string ConfigPath => Path.Combine(_directory, "dnscrypt-proxy.toml");

    public override string? ValidatePrerequisites() =>
        !File.Exists(_executablePath) ? $"dnscrypt-proxy не найден: {_executablePath}" : null;

    protected override Task PrepareAsync(CancellationToken cancellationToken)
    {
        DnsCrypt.SeedLists(_mode, _shippedDirectory, _directory);
        File.WriteAllText(ConfigPath, DnsCrypt.Config(_mode, _directory), new UTF8Encoding(false));

        Note($"режим {DnsCrypt.Describe(_mode).Title}, {DnsCrypt.Address}:{DnsCrypt.Port}");
        return Task.CompletedTask;
    }

    protected override ProcessStartInfo BuildStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        startInfo.ArgumentList.Add("-config");
        startInfo.ArgumentList.Add(ConfigPath);

        return startInfo;
    }

    private volatile bool _ready;

    protected override void Observe(string line)
    {
        if (line.Contains(DnsCrypt.ReadyMarker, StringComparison.OrdinalIgnoreCase))
            _ready = true;
    }

    protected override void ForgetRunState() => _ready = false;

    /// <summary>
    /// Готов по строке, а без неё — через 10 с живого процесса, как winws2:
    /// замер серверов у медленной сети бывает дольше, а sing-box ждать нечего.
    /// </summary>
    public override Task<ServiceCheck> CheckFunctionalAsync(CancellationToken cancellationToken) =>
        Task.FromResult(WinwsService.Ready(IsProcessAlive, _ready, StartedAt, DateTimeOffset.Now)
            ? ServiceCheck.Healthy
            : ServiceCheck.Broken);
}
