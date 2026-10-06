using System.Diagnostics;
using System.Text;
using NetZapret.Core;

namespace NetZapret.Supervisor;

/// <summary>
/// Прокси для Telegram Desktop (<see cref="TgWsProxy"/>) под надзором — третьим движком.
/// </summary>
/// <remarks>
/// Устроен как winws2: процесс с ключами, вывод — в журнал, готов, когда
/// напечатал «Listening on». Здоровье — жив ли процесс: проверять сам путь
/// до Telegram отсюда нечем, а пробное соединение MTProto без клиента
/// не собрать.
/// </remarks>
public sealed class TgWsProxyService : SupervisedService
{
    private readonly string _executablePath;
    private readonly int _port;
    private readonly string _secret;
    private readonly string _workingDirectory;

    public TgWsProxyService(string executablePath, int port, string secret, string workingDirectory)
    {
        _executablePath = executablePath;
        _port = port;
        _secret = secret;
        _workingDirectory = workingDirectory;
    }

    /// <summary>Имя службы — по нему её узнаёт EngineHealth как необязательную.</summary>
    public const string ServiceName = "tg-ws-proxy";

    public override string Name => ServiceName;

    public override IReadOnlyList<string> EngineProcessNames => ["tg-ws-proxy"];

    public override string? ValidatePrerequisites() =>
        !File.Exists(_executablePath) ? $"прокси Telegram не найден: {_executablePath}"
        : !TgWsProxy.IsSecret(_secret) ? "у прокси Telegram нет секрета — включите его заново на вкладке «TG Proxy»"
        : null;

    protected override ProcessStartInfo BuildStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            WorkingDirectory = _workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in TgWsProxy.Arguments(_port))
            startInfo.ArgumentList.Add(argument);

        // Секрет — переменной, а не ключом: командную строку видят журналы
        // и диспетчер задач, а переменную — только сам процесс.
        startInfo.Environment[TgWsProxy.SecretVariable] = _secret;

        // Цвета терминала в журнал не нужны: в файле они — мусор из escape-кодов.
        startInfo.Environment["NO_COLOR"] = "1";

        return startInfo;
    }

    private volatile bool _listening;

    /// <summary>Сколько ждать строки «Listening on», прежде чем судить по одному процессу.</summary>
    public static readonly TimeSpan ListenGrace = TimeSpan.FromSeconds(10);

    protected override void Observe(string line)
    {
        if (line.Contains(TgWsProxy.ListeningMarker, StringComparison.OrdinalIgnoreCase))
            _listening = true;
    }

    protected override void ForgetRunState() => _listening = false;

    public override Task<ServiceCheck> CheckFunctionalAsync(CancellationToken cancellationToken) =>
        Task.FromResult(WinwsService.Ready(IsProcessAlive, _listening, StartedAt, DateTimeOffset.Now)
            ? ServiceCheck.Healthy
            : ServiceCheck.Broken);
}
