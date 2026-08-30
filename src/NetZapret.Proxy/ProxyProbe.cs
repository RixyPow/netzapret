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

    /// <summary>
    /// Оставлять временные файлы после проверки.
    /// </summary>
    /// <remarks>
    /// По умолчанию конфиги удаляются: они одноразовые и содержат UUID
    /// и пароли открытым текстом, а копятся по одному на каждый сервер.
    /// Логи неудачных попыток сохраняются — по ним разбирают причину.
    /// Флаг нужен только при отладке самого пробника.
    /// </remarks>
    public bool KeepArtifacts { get; init; }

    public string LogLevel { get; init; } = "debug";

    /// <summary>
    /// Адреса, по которым определяется, идёт ли через сервер трафик.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отдельно от <see cref="IpServices"/>, и это принципиально. Раньше
    /// признаком работоспособности был полученный внешний адрес, а когда
    /// провайдер перекрыл службы его определения, проверка стала показывать
    /// ноль рабочих серверов из четырнадцати — при полностью живом VPN.
    /// Замер это подтвердил: через каждый сервер <c>generate_204</c>
    /// отвечает за 63–568 мс, а ipify не доходит ни через один.
    /// </para>
    /// <para>
    /// Здесь нарочно лёгкие и общеизвестные адреса, отдающие пустой ответ:
    /// вопрос «доходит ли трафик» не должен зависеть от живости стороннего
    /// сервиса. Их несколько, чтобы отказ одного не выглядел отказом сервера.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> ConnectivityUrls { get; init; } =
    [
        "http://cp.cloudflare.com/generate_204",
        "http://www.gstatic.com/generate_204",
        "http://www.msftconnecttest.com/connecttest.txt",
    ];

    /// <summary>
    /// Определять внешний адрес после успешной проверки связности.
    /// </summary>
    /// <remarks>
    /// Отключается там, где адрес не показывают. Службы бывают перекрыты —
    /// у этого провайдера так и есть, — и тогда на каждом живом сервере
    /// впустую перебираются все три подряд.
    /// </remarks>
    public bool LookupExternalIp { get; init; } = true;

    /// <summary>
    /// Службы, возвращающие внешний адрес открытым текстом.
    /// </summary>
    /// <remarks>
    /// Сведения приятные, но не решающие: адрес показывает, куда именно
    /// вышел трафик, а вот его отсутствие сервер не порочит — служба может
    /// быть перекрыта, что у этого провайдера и произошло.
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

    /// <summary>Внешний адрес, увиденный через прокси; может отсутствовать.</summary>
    public string? ExternalIp { get; init; }

    /// <summary>
    /// Время отклика через сервер — по нему и сортируют.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Elapsed"/>: тот включает запуск sing-box
    /// и определение внешнего адреса, то есть больше говорит о нашей машине,
    /// чем о сервере. Сравнивать серверы по нему нельзя.
    /// </remarks>
    public TimeSpan? Latency { get; init; }

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
        var (ip, _) = await TryServicesAsync(http, options.IpServices, cancellationToken);
        return ip;
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
        if (!options.KeepArtifacts)
            CleanWorkDirectory(options.WorkDirectory);

        var results = new List<ProbeResult>();
        var sync = new object();

        using var limit = new SemaphoreSlim(Math.Max(1, options.Parallelism));

        var tasks = servers.Select(async (server, index) =>
        {
            await limit.WaitAsync(cancellationToken);

            ProbeResult result;

            try
            {
                // Каждой проверке свой порт: процессы sing-box поднимаются
                // одновременно и за один порт подрались бы.
                var own = options with { ListenPort = options.ListenPort + index };
                result = await RunAsync(server, own, cancellationToken);
            }
            catch (Exception ex)
            {
                // Один сервер не вправе оборвать проверку остальных. Любая
                // его беда — это его отрицательный результат, а не отказ
                // всей проверки: иначе отчёт молча недосчитывается серверов,
                // и понять, каких именно, уже нельзя.
                result = new ProbeResult
                {
                    ServerTag = server.Tag,
                    Success = false,
                    Error = ex.GetBaseException().Message,
                };
            }
            finally
            {
                limit.Release();
            }

            lock (sync)
            {
                results.Add(result);
                onResult?.Invoke(result);
            }
        });

        // Дожидаемся всех, не пробрасывая исключение: тело задачи уже
        // превращает любую беду в отрицательный результат, а наружу отсюда
        // может выйти разве что отмена пользователем. Пробросить её значило бы
        // потерять то, что успели измерить, и уничтожить семафор под теми,
        // кто ещё не вернулся.
        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return results;
    }

    public async Task<ProbeResult> RunAsync(ProxyServer server, ProbeOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.WorkDirectory);

        // Общий бюджет на сервер: иначе мёртвый узел удерживает проверку
        // столько, сколько адресов мы перебираем.
        var outer = cancellationToken;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.TotalTimeout);
        cancellationToken = budget.Token;

        var safeTag = MakeFileSafe(server.Tag);
        var configPath = Path.Combine(options.WorkDirectory, $"probe-{safeTag}.json");
        var logPath = Path.Combine(options.WorkDirectory, $"probe-{safeTag}.log");

        // Старый лог мешает читать причину текущей неудачи.
        if (File.Exists(logPath))
            File.Delete(logPath);

        // Имя сервера разрешается здесь, а не внутри sing-box. При работающем
        // движке запрос к резолверу перехватывает TUN, и проверка падала на
        // разрешении имени, ни разу не дойдя до сервера — притом что сервер
        // был жив. Неудача не фатальна: тогда имя уходит в конфиг как есть,
        // и разрешать его будет sing-box, как и прежде.
        string? address;

        using (var lookup = new HttpClient { Timeout = options.RequestTimeout })
        {
            address = await DohResolver.ResolveAsync(server.Host, lookup, cancellationToken);
        }

        var json = _compiler.CompileProbeConfig(server, options.ListenPort, logPath, options.LogLevel, address);
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
            using var handler = new HttpClientHandler
            {
                // Инбаунд mixed обслуживает и SOCKS, и HTTP CONNECT, поэтому
                // обычного HTTP-прокси достаточно и не нужен клиент SOCKS.
                Proxy = new WebProxy($"http://127.0.0.1:{options.ListenPort}"),
                UseProxy = true,
            };

            using var http = new HttpClient(handler) { Timeout = options.RequestTimeout };

            var (latency, error) = await MeasureAsync(http, options.ConnectivityUrls, cancellationToken);

            if (latency is null)
            {
                stopwatch.Stop();

                return Failure(server, stopwatch.Elapsed, logPath,
                    budget.IsCancellationRequested && !budget.Token.IsCancellationRequested
                        ? "превышен бюджет времени"
                        : error ?? "трафик через сервер не пошёл");
            }

            // Внешний адрес — сведения сверх необходимого. Его недоступность
            // сервер не порочит: службы определения адреса бывают перекрыты,
            // и раньше именно это выдавалось за неисправность сервера.
            var ip = options.LookupExternalIp
                ? (await TryServicesAsync(http, options.IpServices, cancellationToken)).Ip
                : null;

            stopwatch.Stop();

            // Лог удачной проверки не нужен: разбирают только неудачи,
            // а лишние файлы мешают найти нужный.
            if (!options.KeepArtifacts)
                Delete(logPath);

            return new ProbeResult
            {
                ServerTag = server.Tag,
                Success = true,
                ExternalIp = ip,
                Latency = latency,
                Elapsed = stopwatch.Elapsed,
                LogPath = options.KeepArtifacts ? logPath : null,
            };
        }
        catch (OperationCanceledException) when (!outer.IsCancellationRequested)
        {
            // Исчерпанный бюджет — обычный отрицательный результат, а не сбой.
            // Без этого отмена уходила наружу и роняла Task.WhenAll: первый же
            // сервер, упёршийся в таймаут, обрывал проверку остальных, и
            // из четырнадцати серверов до отчёта доходило восемь.
            stopwatch.Stop();
            return Failure(server, stopwatch.Elapsed, logPath, "превышен бюджет времени");
        }
        finally
        {
            await runner.StopAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            // Конфиг содержит учётные данные и больше не нужен — удаляем
            // сразу, не дожидаясь следующего запуска.
            if (!options.KeepArtifacts)
                Delete(configPath);
        }
    }

    /// <summary>
    /// Убирает файлы прошлых проверок.
    /// </summary>
    /// <remarks>
    /// Конфиги содержат UUID и пароли открытым текстом, поэтому уходят все.
    /// Логи тоже: они относятся к прошлому прогону и только сбивают с толку
    /// при разборе текущего.
    /// </remarks>
    private static void CleanWorkDirectory(string directory)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var extension = Path.GetExtension(file);

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".log", StringComparison.OrdinalIgnoreCase))
            {
                Delete(file);
            }
        }
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Файл мог быть занят движком, который ещё не завершился.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Замеряет отклик через прокси; <c>null</c> — трафик не пошёл.
    /// </summary>
    private static async Task<(TimeSpan? Latency, string? Error)> MeasureAsync(
        HttpClient http,
        IReadOnlyList<string> urls,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();

        foreach (var url in urls)
        {
            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var response = await http.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                    return (stopwatch.Elapsed, null);

                reasons.Add($"{Short(url)}: {(int)response.StatusCode}");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Причины по каждому адресу, а не общее «не ответило»: без них
                // разбор упирался в догадки, и мы приняли сломанную проверку
                // за сломанный VPN.
                reasons.Add($"{Short(url)}: {ex.GetBaseException().Message}");
            }
        }

        return (null, reasons.Count > 0 ? string.Join("; ", reasons) : null);
    }

    private static async Task<(string? Ip, IReadOnlyList<string> Reasons)> TryServicesAsync(
        HttpClient http,
        IReadOnlyList<string> services,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();

        foreach (var service in services)
        {
            try
            {
                var text = await http.GetStringAsync(service, cancellationToken);
                var ip = text.Trim();

                if (IPAddress.TryParse(ip, out _))
                    return (ip, reasons);

                reasons.Add($"{Short(service)}: ответ не похож на адрес");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                reasons.Add($"{Short(service)}: {ex.GetBaseException().Message}");
            }
        }

        return (null, reasons);
    }

    private static string Short(string service) =>
        Uri.TryCreate(service, UriKind.Absolute, out var url) ? url.Host : service;

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
