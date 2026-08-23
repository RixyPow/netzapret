using System.Diagnostics;
using System.Net.Sockets;

namespace NetZapret.Proxy;

/// <summary>
/// Запускает и останавливает процесс sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Используется и пробником, и супервизором. Готовность определяется не фактом
/// «процесс жив», а тем, что инбаунд принимает соединения: sing-box стартует
/// асинхронно и может упасть уже после запуска — например, не найдя сервер.
/// </para>
/// <para>
/// Лог пишется самим sing-box в файл через <c>log.output</c>, а не читается
/// из stdout: при заданном output в stdout не попадает ничего, а разбирать
/// два источника незачем.
/// </para>
/// </remarks>
public sealed class SingBoxRunner : IAsyncDisposable
{
    private readonly string _executablePath;
    private readonly string _configPath;
    private Process? _process;
    private bool _disposed;

    public SingBoxRunner(string executablePath, string configPath)
    {
        _executablePath = executablePath;
        _configPath = configPath;
    }

    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>Код возврата, если процесс уже завершился.</summary>
    public int? ExitCode => _process is { HasExited: true } ? _process.ExitCode : null;

    /// <summary>
    /// Запускает процесс и ждёт, пока указанный порт не начнёт принимать соединения.
    /// </summary>
    /// <returns><c>true</c>, если инбаунд ответил до истечения таймаута.</returns>
    public async Task<bool> StartAsync(int readinessPort, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsRunning)
            throw new InvalidOperationException("Процесс уже запущен.");

        var startInfo = new ProcessStartInfo
        {
            FileName = _executablePath,
            ArgumentList = { "run", "-c", Path.GetFullPath(_configPath) },
            // Рабочий каталог — рядом с движком: там лежит wintun.dll и прочее,
            // что sing-box ищет относительно себя.
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_executablePath)) ?? ".",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Не удалось запустить {_executablePath}.");

        return await WaitForPortAsync(readinessPort, timeout, cancellationToken);
    }

    /// <summary>
    /// Проверяет, что порт принимает соединения. Это и есть функциональная
    /// проверка живости для супервизора.
    /// </summary>
    public static async Task<bool> IsPortAcceptingAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cts.Token);
            return client.Connected;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
                return false;

            if (await IsPortAcceptingAsync(port, TimeSpan.FromMilliseconds(500), cancellationToken))
                return true;

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
            return;

        // sing-box не обрабатывает сигналы в Windows-консоли без своего окна,
        // поэтому завершаем принудительно. Состояние он на диске не держит,
        // а TUN-адаптер удаляется вместе с процессом.
        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
        }
        catch (Exception)
        {
            // Процесс мог завершиться сам между проверкой и вызовом.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        _process?.Dispose();
        _process = null;
    }
}
