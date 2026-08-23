using System.Diagnostics;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

public enum ServiceHealth
{
    /// <summary>Не запускался или остановлен намеренно.</summary>
    Stopped,

    /// <summary>Процесс жив и функциональная проверка пройдена.</summary>
    Healthy,

    /// <summary>Процесс жив, но функциональная проверка не проходит.</summary>
    Degraded,

    /// <summary>Процесс умер.</summary>
    Dead,

    /// <summary>Перезапуски исчерпаны, служба сдалась.</summary>
    Faulted,
}

/// <summary>
/// Служба под присмотром супервизора.
/// </summary>
/// <remarks>
/// Проверка живости двухуровневая намеренно. «Процесс существует» — слабая
/// гарантия: sing-box успевает подняться и умереть по существу, оставаясь
/// живым процессом. Поэтому у каждой службы есть ещё и функциональная
/// проверка, своя для каждой.
/// </remarks>
public abstract class SupervisedService
{
    public abstract string Name { get; }

    protected Process? Process { get; set; }

    /// <summary>
    /// Куда складывать вывод службы. Если не задан, вывод отбрасывается,
    /// но всё равно вычитывается.
    /// </summary>
    public string? OutputLogPath { get; set; }

    /// <summary>Последние строки вывода — для диагностики при падении.</summary>
    public IReadOnlyList<string> RecentOutput
    {
        get
        {
            lock (_outputLock)
                return _recentOutput.ToList();
        }
    }

    private readonly object _outputLock = new();
    private readonly Queue<string> _recentOutput = new();

    public int? ProcessId => Process is { HasExited: false } ? Process.Id : null;

    public DateTimeOffset? StartedAt { get; private set; }

    public int RestartCount { get; private set; }

    public string? LastError { get; protected set; }

    /// <summary>Процесс существует и не завершился.</summary>
    public bool IsProcessAlive => Process is { HasExited: false };

    /// <summary>
    /// Функциональная проверка: слушается ли порт, отвечает ли API.
    /// Для служб без наблюдаемого признака возвращает <c>true</c>.
    /// </summary>
    public abstract Task<bool> CheckFunctionalAsync(CancellationToken cancellationToken);

    protected abstract ProcessStartInfo BuildStartInfo();

    /// <summary>Проверяет, что всё нужное для запуска на месте.</summary>
    public abstract string? ValidatePrerequisites();

    public async Task<bool> StartAsync(TimeSpan readinessTimeout, CancellationToken cancellationToken)
    {
        var problem = ValidatePrerequisites();

        if (problem is not null)
        {
            LastError = problem;
            return false;
        }

        try
        {
            Process = Process.Start(BuildStartInfo());
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            return false;
        }

        if (Process is null)
        {
            LastError = "процесс не запустился";
            return false;
        }

        // Вывод обязан вычитываться. Перенаправленный и никем не читаемый
        // конвейер заполняется и намертво блокирует дочерний процесс на записи
        // в stdout — служба при этом выглядит живой и проходит проверку порта.
        StartOutputPump(Process.StandardOutput);
        StartOutputPump(Process.StandardError);

        StartedAt = DateTimeOffset.Now;

        var deadline = DateTime.UtcNow + readinessTimeout;

        while (DateTime.UtcNow < deadline)
        {
            if (!IsProcessAlive)
            {
                LastError = $"процесс завершился с кодом {Process.ExitCode}";
                return false;
            }

            if (await CheckFunctionalAsync(cancellationToken))
            {
                LastError = null;
                return true;
            }

            await Task.Delay(300, cancellationToken);
        }

        LastError = "функциональная проверка не прошла за отведённое время";
        return false;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Process is null || Process.HasExited)
        {
            Process = null;
            StartedAt = null;
            return;
        }

        try
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (Exception)
        {
            // Мог завершиться сам между проверкой и вызовом.
        }
        finally
        {
            Process?.Dispose();
            Process = null;
            StartedAt = null;
        }
    }

    internal void NoteRestart() => RestartCount++;

    private void StartOutputPump(StreamReader reader)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() is { } line)
                    RecordOutput(line);
            }
            catch (Exception)
            {
                // Поток закрывается вместе с процессом — это штатное завершение насоса.
            }
        });
    }

    private void RecordOutput(string line)
    {
        lock (_outputLock)
        {
            _recentOutput.Enqueue(line);

            while (_recentOutput.Count > 100)
                _recentOutput.Dequeue();
        }

        if (OutputLogPath is null)
            return;

        try
        {
            var directory = Path.GetDirectoryName(OutputLogPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(OutputLogPath, line + Environment.NewLine, System.Text.Encoding.UTF8);
        }
        catch (IOException)
        {
            // Лог не должен ронять службу.
        }
    }
}

/// <summary>
/// Прокси-движок sing-box.
/// </summary>
/// <remarks>
/// Функциональная проверка — доступность Clash API. Порт указан в самом
/// конфиге, так что проверка работает одинаково и для TUN-конфига,
/// и для проверочного с локальным инбаундом.
/// </remarks>
public sealed class SingBoxService : SupervisedService
{
    private readonly string _executablePath;
    private readonly string _configPath;
    private readonly int _healthPort;
    private readonly int? _trafficPort;
    private readonly int _trafficCheckEvery;

    private int _checkCounter;
    private bool _lastTrafficOk = true;

    /// <param name="healthPort">Порт Clash API — быстрая проверка живости.</param>
    /// <param name="trafficPort">
    /// Порт локального прокси для глубокой проверки. Если задан, раз в
    /// <paramref name="trafficCheckEvery"/> опросов делается настоящий запрос
    /// через прокси. Слушающий порт означает лишь, что движок поднялся;
    /// он ничего не говорит о том, доходит ли трафик до сервера.
    /// </param>
    public SingBoxService(
        string executablePath,
        string configPath,
        int healthPort = 9090,
        int? trafficPort = null,
        int trafficCheckEvery = 6)
    {
        _executablePath = executablePath;
        _configPath = configPath;
        _healthPort = healthPort;
        _trafficPort = trafficPort;
        _trafficCheckEvery = Math.Max(1, trafficCheckEvery);
    }

    public override string Name => "sing-box";

    public override string? ValidatePrerequisites()
    {
        if (!File.Exists(_executablePath))
            return $"движок не найден: {_executablePath}";

        if (!File.Exists(_configPath))
            return $"конфиг не найден: {_configPath}";

        return null;
    }

    protected override ProcessStartInfo BuildStartInfo() => new()
    {
        FileName = _executablePath,
        ArgumentList = { "run", "-c", Path.GetFullPath(_configPath) },
        WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_executablePath)) ?? ".",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    public override async Task<bool> CheckFunctionalAsync(CancellationToken cancellationToken)
    {
        if (!await SingBoxRunner.IsPortAcceptingAsync(_healthPort, TimeSpan.FromSeconds(2), cancellationToken))
            return false;

        if (_trafficPort is null)
            return true;

        // Глубокая проверка делается редко: она уходит в сеть и стоит секунды.
        // Между проверками используется её последний результат.
        if (_checkCounter++ % _trafficCheckEvery != 0)
            return _lastTrafficOk;

        _lastTrafficOk = await CheckTrafficAsync(_trafficPort.Value, cancellationToken);

        if (!_lastTrafficOk)
            LastError = "трафик через прокси не проходит";

        return _lastTrafficOk;
    }

    private static async Task<bool> CheckTrafficAsync(int proxyPort, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"http://127.0.0.1:{proxyPort}"),
                UseProxy = true,
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            var text = await http.GetStringAsync("https://api.ipify.org", cancellationToken);

            return System.Net.IPAddress.TryParse(text.Trim(), out _);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Десинк winws2.
/// </summary>
/// <remarks>
/// <para>
/// Функциональной проверки нет: winws2 не слушает портов и не имеет API —
/// он висит на фильтре WinDivert. Единственный доступный признак — живость
/// процесса, что и отражено возвратом <c>true</c>.
/// </para>
/// <para>
/// ВНИМАНИЕ: не тестировано. Запуск winws2 требует прав администратора
/// (драйвер WinDivert), а ночная сессия работала без них по условию задания.
/// Код написан, но вживую не проверялся — нужен ручной запуск с повышением прав.
/// </para>
/// </remarks>
public sealed class WinwsService : SupervisedService
{
    private readonly string _executablePath;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string _workingDirectory;

    public WinwsService(string executablePath, IReadOnlyList<string> arguments, string workingDirectory)
    {
        _executablePath = executablePath;
        _arguments = arguments;
        _workingDirectory = workingDirectory;
    }

    public override string Name => "winws2";

    public override string? ValidatePrerequisites() =>
        File.Exists(_executablePath) ? null : $"движок не найден: {_executablePath}";

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
        };

        foreach (var argument in _arguments)
            startInfo.ArgumentList.Add(argument);

        return startInfo;
    }

    public override Task<bool> CheckFunctionalAsync(CancellationToken cancellationToken) =>
        Task.FromResult(IsProcessAlive);
}
