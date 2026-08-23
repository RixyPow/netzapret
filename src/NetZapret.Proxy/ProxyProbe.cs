using System.Diagnostics;
using System.Net;
using NetZapret.Subscriptions;

namespace NetZapret.Proxy;

public sealed record ProbeOptions
{
    /// <summary>Локальный порт для инбаунда пробника.</summary>
    public int ListenPort { get; init; } = 21080;

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Общий бюджет времени на один сервер.
    /// </summary>
    /// <remarks>
    /// Обязателен: без него мёртвый сервер съедает таймаут за каждую службу
    /// определения адреса подряд, и проверка четырнадцати серверов занимает
    /// минуты вместо десятков секунд.
    /// </remarks>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(14);

    /// <summary>
    /// Сколько серверов проверять одновременно.
    /// </summary>
    /// <remarks>
    /// Каждая проверка поднимает свой процесс sing-box на своём порту,
    /// так что они независимы. Ограничение нужно, чтобы не поднимать
    /// полтора десятка движков разом.
    /// </remarks>
    public int Parallelism { get; init; } = 5;

    /// <summary>Каталог для конфигов и логов пробника.</summary>
    public string WorkDirectory { get; init; } = "runtime/probe";

    public string LogLevel { get; init; } = "debug";

    /// <summary>
    /// Службы, возвращающие внешний адрес открытым текстом.
    /// </summary>
    /// <remarks>
    /// Их несколько, потому что часть может быть недоступна или заблокирована,
    /// и тогда неудача одной службы выглядела бы как неисправность прокси.
    /// </remarks>
    public IReadOnlyList<string> IpServices { get; init; } =
    [
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://icanhazip.com",
    ];
}

public sealed record ProbeResult
{
    public required string ServerTag { get; init; }
    public required bool Success { get; init; }

    /// <summary>Внешний адрес, увиденный через прокси.</summary>
    public string? ExternalIp { get; init; }

    public TimeSpan Elapsed { get; init; }
    public string? Error { get; init; }
    public string? LogPath { get; init; }

    /// <summary>Последние строки лога sing-box — если пробник упал, причина обычно здесь.</summary>
    public IReadOnlyList<string> LogTail { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Проверяет, что через сервер действительно идёт трафик.
/// </summary>
/// <remarks>
/// Проверка функциональная, а не «порт слушает»: sing-box поднимает инбаунд
/// раньше, чем выясняет, что до сервера не достучаться. Единственный надёжный
/// признак — HTTP-запрос, ушедший через прокси и вернувший чужой внешний адрес.
/// </remarks>
public sealed class ProxyProbe
{
    private readonly string _singBoxPath;
    private readonly SingBoxConfigCompiler _compiler = new();

    public ProxyProbe(string singBoxPath)
    {
        _singBoxPath = singBoxPath;
    }

    /// <summary>Определяет внешний адрес без прокси — для сравнения.</summary>
    public static async Task<string?> GetDirectIpAsync(ProbeOptions options, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = options.RequestTimeout };
        return await TryServicesAsync(http, options.IpServices, cancellationToken);
    }

    /// <summary>
    /// Проверяет несколько серверов одновременно.
    /// </summary>
    /// <param name="onResult">Вызывается по мере готовности каждого результата.</param>
    public async Task<IReadOnlyList<ProbeResult>> RunManyAsync(
        IReadOnlyList<ProxyServer> servers,
        ProbeOptions options,
        Action<ProbeResult>? onResult,
        CancellationToken cancellationToken)
    {
        var results = new List<ProbeResult>();
        var sync = new object();

        using var limit = new SemaphoreSlim(Math.Max(1, options.Parallelism));

        var tasks = servers.Select(async (server, index) =>
        {
            await limit.WaitAsync(cancellationToken);

            try
            {
                // Каждой проверке свой порт: процессы sing-box поднимаются
                // одновременно и за один порт подрались бы.
                var own = options with { ListenPort = options.ListenPort + index };
                var result = await RunAsync(server, own, cancellationToken);

                lock (sync)
                {
                    results.Add(result);
                    onResult?.Invoke(result);
                }
            }
            finally
            {
                limit.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    public async Task<ProbeResult> RunAsync(ProxyServer server, ProbeOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.WorkDirectory);

        // Общий бюджет на сервер: иначе мёртвый узел удерживает проверку
        // столько, сколько служб определения адреса мы перебираем.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.TotalTimeout);
        cancellationToken = budget.Token;

        var safeTag = MakeFileSafe(server.Tag);
        var configPath = Path.Combine(options.WorkDirectory, $"probe-{safeTag}.json");
        var logPath = Path.Combine(options.WorkDirectory, $"probe-{safeTag}.log");

        // Старый лог мешает читать причину текущей неудачи.
        if (File.Exists(logPath))
            File.Delete(logPath);

        var json = _compiler.CompileProbeConfig(server, options.ListenPort, logPath, options.LogLevel);
        SingBoxConfigCompiler.WriteToFile(configPath, json);

        var stopwatch = Stopwatch.StartNew();
        await using var runner = new SingBoxRunner(_singBoxPath, configPath);

        bool ready;

        try
        {
            ready = await runner.StartAsync(options.ListenPort, options.StartupTimeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return Failure(server, stopwatch.Elapsed, logPath, "превышен бюджет времени при запуске");
        }

        if (!ready)
        {
            stopwatch.Stop();
            return Failure(server, stopwatch.Elapsed, logPath,
                runner.ExitCode is { } code
                    ? $"sing-box завершился с кодом {code}, инбаунд не поднялся"
                    : "инбаунд не начал принимать соединения за отведённое время");
        }

        try
        {
            var (ip, error) = await QueryThroughProxyAsync(options, cancellationToken);
            stopwatch.Stop();

            if (ip is null)
            {
                return Failure(server, stopwatch.Elapsed, logPath,
                    budget.IsCancellationRequested && !budget.Token.IsCancellationRequested
                        ? "превышен бюджет времени"
                        : error ?? "внешний адрес не получен");
            }

            return new ProbeResult
            {
                ServerTag = server.Tag,
                Success = true,
                ExternalIp = ip,
                Elapsed = stopwatch.Elapsed,
                LogPath = logPath,
            };
        }
        finally
        {
            await runner.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
    }

    private async Task<(string? Ip, string? Error)> QueryThroughProxyAsync(
        ProbeOptions options,
        CancellationToken cancellationToken)
    {
        // Инбаунд mixed обслуживает и SOCKS, и HTTP CONNECT, поэтому обычного
        // HTTP-прокси достаточно и не нужен отдельный клиент SOCKS.
        var handler = new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{options.ListenPort}"),
            UseProxy = true,
        };

        using var http = new HttpClient(handler) { Timeout = options.RequestTimeout };

        try
        {
            var ip = await TryServicesAsync(http, options.IpServices, cancellationToken);
            return ip is null ? (null, "ни одна служба определения адреса не ответила через прокси") : (ip, null);
        }
        catch (Exception ex)
        {
            return (null, ex.GetBaseException().Message);
        }
    }

    private static async Task<string?> TryServicesAsync(
        HttpClient http,
        IReadOnlyList<string> services,
        CancellationToken cancellationToken)
    {
        foreach (var service in services)
        {
            try
            {
                var text = await http.GetStringAsync(service, cancellationToken);
                var ip = text.Trim();

                if (IPAddress.TryParse(ip, out _))
                    return ip;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Пробуем следующую службу.
            }
        }

        return null;
    }

    private static ProbeResult Failure(ProxyServer server, TimeSpan elapsed, string logPath, string error) => new()
    {
        ServerTag = server.Tag,
        Success = false,
        Elapsed = elapsed,
        Error = error,
        LogPath = logPath,
        LogTail = ReadLogTail(logPath, 15),
    };

    private static IReadOnlyList<string> ReadLogTail(string logPath, int lines)
    {
        try
        {
            return File.Exists(logPath)
                ? File.ReadLines(logPath).TakeLast(lines).ToList()
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string MakeFileSafe(string value)
    {
        var chars = value.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c).ToArray();
        var name = new string(chars).Trim('_');

        return name.Length == 0 ? "server" : name[..Math.Min(name.Length, 48)];
    }
}
