using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetZapret.Proxy;

/// <summary>Что сейчас меряется.</summary>
public enum SpeedPhase
{
    /// <summary>Связь с сервером замера и откуда мы ему видны.</summary>
    Connect,

    /// <summary>Задержка.</summary>
    Ping,

    /// <summary>Скачивание.</summary>
    Download,

    /// <summary>Отдача.</summary>
    Upload,
}

/// <summary>Показание на ходу — для стрелки.</summary>
/// <param name="Mbps">Скорость за последнюю секунду; у задержки — ноль.</param>
/// <param name="Fraction">Доля пройденного этапа, 0–1.</param>
/// <param name="PingMs">Последний ответ при замере задержки.</param>
/// <param name="Done">Этап кончился, и это его итог, а не показание на ходу.</param>
public readonly record struct SpeedReading(
    SpeedPhase Phase, double Mbps, double Fraction, double? PingMs = null, bool Done = false);

/// <summary>Итог замера. Чего не удалось измерить — <c>null</c>, а не ноль.</summary>
public sealed record SpeedResult
{
    /// <summary>Задержка, медиана, мс.</summary>
    public double? PingMs { get; init; }

    /// <summary>Разброс задержки: средняя разница соседних ответов, мс.</summary>
    public double? JitterMs { get; init; }

    public double? DownMbps { get; init; }

    public double? UpMbps { get; init; }

    /// <summary>Сколько байт ушло на замер — у туннеля это трафик подписки.</summary>
    public long DownBytes { get; init; }

    public long UpBytes { get; init; }

    /// <summary>Адрес, с которого нас видит сервер замера.</summary>
    public string? Address { get; init; }

    /// <summary>Страна этого адреса, двумя буквами.</summary>
    public string? Country { get; init; }

    /// <summary>Узел сервера замера, до которого мы дошли (у Cloudflare — код аэропорта).</summary>
    public string? Node { get; init; }

    /// <summary>Что не получилось, словами; <c>null</c> — всё измерено.</summary>
    public string? Problem { get; init; }
}

/// <summary>Чем и сколько мерить.</summary>
public sealed record SpeedTestOptions
{
    /// <summary>Сервер замера. По умолчанию — служба замера Cloudflare.</summary>
    public Uri Server { get; init; } = new("https://speed.cloudflare.com");

    /// <summary>Через что идти; <c>null</c> — напрямую, мимо системного прокси.</summary>
    public IWebProxy? Proxy { get; init; }

    /// <summary>Сколько соединений разом.</summary>
    public int Streams { get; init; } = 4;

    public TimeSpan DownloadFor { get; init; } = TimeSpan.FromSeconds(8);

    public TimeSpan UploadFor { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Сколько ответов брать в замер задержки.</summary>
    public int Pings { get; init; } = 10;
}

/// <summary>
/// Замер скорости: задержка, скачивание и отдача — напрямую или через туннель.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 30.09: «давай попробуем сделать замер скорости прям в программе»,
/// «как спидтест». В тот день главная беда туннеля — десинк, съедавший его
/// скорость вдесятеро, — нашлась за десять минут именно таким замером, только
/// собранным на коленке из curl (см. <c>TunnelEndpoints</c>).
/// </para>
/// <para>
/// Несколько соединений разом, а не одно: одно упирается в один поток TCP,
/// а не в канал. Замер 30.09 у владельца: одним соединением к Cloudflare
/// отдача 36 Мбит/с, Speedtest несколькими — 276.
/// </para>
/// <para>
/// По времени, а не по объёму: файл в 50 МБ на 300 Мбит/с кончается за секунду
/// с небольшим, и TCP не успевает разогнаться, а на 2 Мбит/с тянется три минуты.
/// Разгон в итог не идёт — скачивание считается после первой четверти срока.
/// </para>
/// <para>
/// Отдача считается по ответам сервера, а не по тому, что мы записали в сокет:
/// между нами и сервером стоят буферы — системные, а при замере через туннель
/// ещё и движка, — и записанное в них ещё не отдано. Стрелка идёт по записанному,
/// итог — по принятому сервером.
/// </para>
/// <para>
/// Сервер — Cloudflare: его узлы есть и рядом с выходами VPN, и в России,
/// и он не просит ни ключей, ни регистрации. Одним запросом больше 50 МБ
/// он не отдаёт (замер 30.09: 200 МБ — отказ 403), отсюда куски по 25 МБ.
/// </para>
/// </remarks>
public sealed class SpeedTest
{
    /// <summary>Размер одного скачиваемого куска; следующий запрашивается, пока идёт срок.</summary>
    private const int DownloadChunk = 25_000_000;

    /// <summary>С какого куска начинать отдачу и до какого расти.</summary>
    private const int UploadChunkMin = 256 * 1024;
    private const int UploadChunkMax = 8 * 1024 * 1024;

    /// <summary>Меньше этого за весь срок — не скорость, а замерший поток.</summary>
    private const long StalledBelow = 256 * 1024;

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(150);

    private readonly SpeedTestOptions _options;

    public SpeedTest(SpeedTestOptions? options = null) => _options = options ?? new SpeedTestOptions();

    public async Task<SpeedResult> RunAsync(
        IProgress<SpeedReading>? progress,
        CancellationToken cancellationToken)
    {
        using var handler = new SocketsHttpHandler
        {
            // Системный прокси не наш путь: «напрямую» значит напрямую.
            UseProxy = _options.Proxy is not null,
            Proxy = _options.Proxy,
            AutomaticDecompression = DecompressionMethods.None,
            MaxConnectionsPerServer = Math.Max(1, _options.Streams) * 2,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };

        using var http = new HttpClient(handler)
        {
            BaseAddress = _options.Server,
            Timeout = Timeout.InfiniteTimeSpan,
        };

        var result = new SpeedResult();

        progress?.Report(new SpeedReading(SpeedPhase.Connect, 0, 0));

        try
        {
            result = await TraceAsync(http, result, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return result with { Problem = Unreachable(ex) };
        }

        try
        {
            result = await PingAsync(http, result, progress, cancellationToken);
            result = await DownloadAsync(http, result, progress, cancellationToken);
            result = await UploadAsync(http, result, progress, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return result with { Problem = Short(ex) };
        }

        return result;
    }

    /// <summary>Откуда нас видно: адрес, страна и узел — из /cdn-cgi/trace.</summary>
    private static async Task<SpeedResult> TraceAsync(HttpClient http, SpeedResult result, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(15));

        var text = await http.GetStringAsync("/cdn-cgi/trace", limit.Token);
        var (address, country, node) = ParseTrace(text);

        return result with { Address = address, Country = country, Node = node };
    }

    /// <summary>Разбор ответа trace: строки «ключ=значение».</summary>
    public static (string? Address, string? Country, string? Node) ParseTrace(string text)
    {
        string? Value(string key) => text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(key + "=", StringComparison.Ordinal))
            .Select(line => line[(key.Length + 1)..].Trim())
            .FirstOrDefault(value => value.Length > 0);

        return (Value("ip"), Value("loc"), Value("colo"));
    }

    private async Task<SpeedResult> PingAsync(
        HttpClient http, SpeedResult result, IProgress<SpeedReading>? progress, CancellationToken cancellationToken)
    {
        var times = new List<double>();
        int count = Math.Max(1, _options.Pings);

        // Первый ответ не в счёт: в нём рукопожатие TLS, а через туннель ещё
        // и подключение к прокси. Дальше соединение одно и то же.
        for (int i = 0; i <= count; i++)
        {
            var watch = Stopwatch.StartNew();
            double server;

            using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                limit.CancelAfter(TimeSpan.FromSeconds(10));

                using var response = await http.GetAsync("/__down?bytes=0", limit.Token);
                await response.Content.ReadAsByteArrayAsync(limit.Token);

                server = response.Headers.TryGetValues("Server-Timing", out var timing)
                    ? ServerTime(timing)
                    : 0;
            }

            // Сервер думает над ответом дольше, чем идёт пакет, и сам говорит
            // сколько — это время не задержка сети.
            double ms = Math.Max(0, watch.Elapsed.TotalMilliseconds - server);

            if (i > 0)
                times.Add(ms);

            progress?.Report(new SpeedReading(SpeedPhase.Ping, 0, (double)i / count, i > 0 ? ms : null));
        }

        var median = Median(times);

        progress?.Report(new SpeedReading(SpeedPhase.Ping, 0, 1, median, Done: true));

        return result with { PingMs = median, JitterMs = Jitter(times) };
    }

    private async Task<SpeedResult> DownloadAsync(
        HttpClient http, SpeedResult result, IProgress<SpeedReading>? progress, CancellationToken cancellationToken)
    {
        long total = 0;
        string? refusal = null;

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stop.Token;
        var watch = Stopwatch.StartNew();

        var streams = Enumerable.Range(0, Math.Max(1, _options.Streams)).Select(_ => Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var response = await http.GetAsync(
                        $"/__down?bytes={DownloadChunk}", HttpCompletionOption.ResponseHeadersRead, token);

                    if (!response.IsSuccessStatusCode)
                    {
                        refusal = $"сервер замера ответил {(int)response.StatusCode}";
                        await Task.Delay(300, token);
                        continue;
                    }

                    await using var body = await response.Content.ReadAsStreamAsync(token);

                    int read;

                    while ((read = await body.ReadAsync(buffer, token)) > 0)
                        Interlocked.Add(ref total, read);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    refusal = Short(ex);

                    try
                    {
                        await Task.Delay(300, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, CancellationToken.None)).ToList();

        long bytes;
        double seconds;

        try
        {
            (bytes, seconds) = await WatchAsync(
                SpeedPhase.Download, _options.DownloadFor, watch, () => Interlocked.Read(ref total), progress, cancellationToken);
        }
        finally
        {
            // И при отмене тоже: потоки не должны пережить замер.
            await stop.CancelAsync();
            await Task.WhenAll(streams);
        }

        long received = Interlocked.Read(ref total);

        result = result with { DownBytes = received };

        if (received < StalledBelow)
        {
            return result with
            {
                Problem = received == 0
                    ? "скачивание не пошло: " + (refusal ?? "сервер замера молчит")
                    : $"скачивание замерло на {received / 1024} КБ — так выглядит замедление у оператора",
            };
        }

        double speed = Mbps(bytes, seconds);

        progress?.Report(new SpeedReading(SpeedPhase.Download, speed, 1, Done: true));

        return result with { DownMbps = speed };
    }

    private async Task<SpeedResult> UploadAsync(
        HttpClient http, SpeedResult result, IProgress<SpeedReading>? progress, CancellationToken cancellationToken)
    {
        long written = 0;
        long accepted = 0;
        long lastAcceptedAt = 0;
        string? refusal = null;

        var payload = new byte[UploadChunkMax];
        Random.Shared.NextBytes(payload);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stop.Token;
        var watch = Stopwatch.StartNew();
        var duration = _options.UploadFor;

        var streams = Enumerable.Range(0, Math.Max(1, _options.Streams)).Select(_ => Task.Run(async () =>
        {
            int size = UploadChunkMin;

            // Новый кусок — только пока идёт срок; начатый даём досказать,
            // иначе принятое сервером осталось бы несчитанным.
            while (watch.Elapsed < duration && !token.IsCancellationRequested)
            {
                var started = watch.Elapsed;

                try
                {
                    using var content = new CountingContent(payload, size, n => Interlocked.Add(ref written, n));
                    using var response = await http.PostAsync("/__up", content, token);

                    if (!response.IsSuccessStatusCode)
                    {
                        refusal = $"сервер замера ответил {(int)response.StatusCode}";
                        await Task.Delay(300, token);
                        continue;
                    }

                    Interlocked.Add(ref accepted, size);
                    Interlocked.Exchange(ref lastAcceptedAt, watch.ElapsedTicks);

                    // Кусок ушёл быстрее полсекунды — берём вдвое больше: на
                    // мелких кусках меряется не канал, а время на запрос.
                    if (watch.Elapsed - started < TimeSpan.FromMilliseconds(500))
                        size = Math.Min(size * 2, UploadChunkMax);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    refusal = Short(ex);

                    try
                    {
                        await Task.Delay(300, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, CancellationToken.None)).ToList();

        try
        {
            await WatchAsync(SpeedPhase.Upload, duration, watch, () => Interlocked.Read(ref written), progress, cancellationToken);

            // Начатым кускам — ещё немного, дольше ждать незачем.
            stop.CancelAfter(TimeSpan.FromSeconds(4));
        }
        catch
        {
            await stop.CancelAsync();
            throw;
        }
        finally
        {
            await Task.WhenAll(streams);
        }

        long sent = Interlocked.Read(ref accepted);

        result = result with { UpBytes = Interlocked.Read(ref written) };

        if (sent == 0)
        {
            return result with
            {
                Problem = "отдача не пошла: " + (refusal ?? "сервер не принял ни одного куска за срок замера"),
            };
        }

        double seconds = (double)Interlocked.Read(ref lastAcceptedAt) / Stopwatch.Frequency;

        double speed = Mbps(sent, seconds);

        progress?.Report(new SpeedReading(SpeedPhase.Upload, speed, 1, Done: true));

        return result with { UpMbps = speed };
    }

    /// <summary>
    /// Следит за счётчиком весь срок этапа: шлёт показания и возвращает, сколько
    /// прошло после разгона и за какое время.
    /// </summary>
    private static async Task<(long Bytes, double Seconds)> WatchAsync(
        SpeedPhase phase,
        TimeSpan duration,
        Stopwatch watch,
        Func<long> total,
        IProgress<SpeedReading>? progress,
        CancellationToken cancellationToken)
    {
        var rampUp = TimeSpan.FromTicks(duration.Ticks / 4);
        var samples = new Queue<(TimeSpan At, long Total)>();

        long atRamp = 0;
        TimeSpan rampAt = TimeSpan.Zero;
        bool ramped = false;

        while (watch.Elapsed < duration)
        {
            await Task.Delay(Tick, cancellationToken);

            var now = watch.Elapsed;
            long bytes = total();

            if (!ramped && now >= rampUp)
            {
                ramped = true;
                atRamp = bytes;
                rampAt = now;
            }

            samples.Enqueue((now, bytes));

            while (samples.Count > 1 && now - samples.Peek().At > Window)
                samples.Dequeue();

            var (oldAt, oldTotal) = samples.Peek();
            double span = (now - oldAt).TotalSeconds;

            progress?.Report(new SpeedReading(
                phase,
                span > 0 ? Mbps(bytes - oldTotal, span) : 0,
                Math.Min(1, now.TotalSeconds / duration.TotalSeconds)));
        }

        return (total() - atRamp, (watch.Elapsed - rampAt).TotalSeconds);
    }

    /// <summary>
    /// Сколько сервер замера сам потратил на ответ, мс, — из заголовка Server-Timing.
    /// </summary>
    /// <remarks>
    /// Замер 30.09, пять запросов по одному соединению: запрос занимал 59–74 мс,
    /// сервер называл <c>cfSpeedWorker</c> 27–40 и <c>cfSpeedEdge</c> 6–9,
    /// а сам же мерил задержку TCP до нас в 21,3–21,6 мс. За вычетом обоих
    /// остаётся 25–27 мс; за вычетом одного Worker — 32–34. Вычитаются оба.
    /// Прочие записи заголовка (<c>cfL4</c>) — не время работы сервера.
    /// </remarks>
    public static double ServerTime(IEnumerable<string> serverTiming)
    {
        double total = 0;

        foreach (var entry in serverTiming.SelectMany(value => value.Split(',')))
        {
            var parts = entry.Split(';', StringSplitOptions.TrimEntries);

            if (!parts[0].StartsWith("cfSpeed", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var part in parts.Skip(1))
            {
                if (part.StartsWith("dur=", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(
                        part[4..],
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double ms))
                {
                    total += ms;
                }
            }
        }

        return total;
    }

    private static double Mbps(long bytes, double seconds) =>
        seconds <= 0 ? 0 : bytes * 8 / seconds / 1_000_000;

    public static double? Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
            return null;

        var sorted = values.OrderBy(v => v).ToList();
        int middle = sorted.Count / 2;

        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>Разброс: средняя разница соседних ответов.</summary>
    public static double? Jitter(IReadOnlyList<double> values) =>
        values.Count < 2
            ? null
            : values.Zip(values.Skip(1), (a, b) => Math.Abs(a - b)).Average();

    /// <summary>Итог словами — для журнала и для буфера обмена.</summary>
    /// <remarks>
    /// Числа — в языке процесса: в окне это запятая, а библиотеки и тесты
    /// собраны без таблиц языков (InvariantGlobalization), там точка.
    /// </remarks>
    public static string Describe(SpeedResult result)
    {
        var parts = new List<string>();

        if (result.DownMbps is { } down)
            parts.Add($"скачивание {down:0.0} Мбит/с");

        if (result.UpMbps is { } up)
            parts.Add($"отдача {up:0.0} Мбит/с");

        if (result.PingMs is { } ping)
            parts.Add($"задержка {ping:0} мс");

        if (result.Problem is { Length: > 0 } problem)
            parts.Add(problem);

        return parts.Count == 0 ? "ничего не измерено" : string.Join(", ", parts);
    }

    /// <summary>Причина отказа коротко, без адресов и без стека.</summary>
    private static string Short(Exception ex) => ex.GetBaseException() switch
    {
        OperationCanceledException => "сервер замера не ответил вовремя",
        TimeoutException => "соединение не установилось за 10 с",
        var inner => inner.Message,
    };

    /// <summary>
    /// Почему не дошли до сервера замера.
    /// </summary>
    /// <remarks>
    /// Через туннель виноват чаще туннель, чем сервер, а прежде любой отказ
    /// звался «сервер замера недоступен» (01.10, два случая за вечер):
    /// <list type="bullet">
    /// <item>отказ в подключении к петле — входа проверки нет вовсе: до 0.10.3
    /// он поднимался только с «Проверкой прохода трафика», по умолчанию
    /// выключенной, и у большинства замер через туннель не работал;</item>
    /// <item>вход есть, а соединение не установилось за срок — туннель не
    /// пропустил: у владельца замер нажат через 24 с после запуска, когда
    /// WARP ещё не пропускал трафик.</item>
    /// </list>
    /// </remarks>
    private string Unreachable(Exception ex)
    {
        if (_options.Proxy is null)
            return "сервер замера недоступен: " + Short(ex);

        if (ex.GetBaseException() is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            return "вход проверки движка не отвечает — перезапустите движки";

        return "туннель не пропустил соединение: " + Short(ex);
    }

    /// <summary>Тело запроса, считающее записанное: по нему идёт стрелка отдачи.</summary>
    private sealed class CountingContent : HttpContent
    {
        private const int Piece = 32 * 1024;

        private readonly byte[] _payload;
        private readonly int _size;
        private readonly Action<int> _written;

        public CountingContent(byte[] payload, int size, Action<int> written)
        {
            _payload = payload;
            _size = size;
            _written = written;

            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < _size; offset += Piece)
            {
                int count = Math.Min(Piece, _size - offset);

                await stream.WriteAsync(_payload.AsMemory(offset, count), cancellationToken);
                _written(count);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _size;
            return true;
        }
    }
}
