using System.Diagnostics;
using System.Net.Http.Headers;

namespace NetZapret.Proxy;

/// <summary>Что известно про один резолвер.</summary>
public sealed record DnsResolver
{
    public required string Name { get; init; }

    /// <summary>Адрес, а не имя: имя потребовало бы резолвера для себя самого.</summary>
    public required string Address { get; init; }

    /// <summary>Чем он отличается от прочих — это выбор, а не оптимизация.</summary>
    public string? Note { get; init; }
}

/// <summary>Итог проверки одного резолвера.</summary>
public sealed record DnsProbeResult
{
    public required DnsResolver Resolver { get; init; }

    public required bool Works { get; init; }

    public TimeSpan? Latency { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Проверяет, разрешаются ли имена через резолвер.
/// </summary>
/// <remarks>
/// <para>
/// Настоящим запросом DoH, а не пингом. Перекрывают именно запрос: ICMP
/// до 8.8.8.8 при этом ходит прекрасно, и проверка по пингу отвечала бы
/// на вопрос «жив ли хост» вместо «разрешится ли имя».
/// </para>
/// <para>
/// Запрос собирается в проводном формате (RFC 8484) и отправляется методом
/// POST. Формат JSON поддерживают не все, а проводной — обязателен для любого
/// DoH-сервера, так что сравнение получается честным.
/// </para>
/// </remarks>
public static class DnsProbe
{
    /// <summary>
    /// Резолверы, о которых мы знаем.
    /// </summary>
    /// <remarks>
    /// Список тот же, что предлагает Zapret в своём разделе DNS: человеку
    /// проще, когда в двух программах одни и те же имена.
    /// </remarks>
    public static IReadOnlyList<DnsResolver> Known { get; } =
    [
        new DnsResolver { Name = "Cloudflare", Address = "1.1.1.1", Note = "быстрый, без фильтрации" },
        new DnsResolver { Name = "Google", Address = "8.8.8.8", Note = "надёжный, без фильтрации" },
        new DnsResolver { Name = "Quad9", Address = "9.9.9.9", Note = "отсекает вредоносные домены" },
        new DnsResolver { Name = "AdGuard", Address = "94.140.14.14", Note = "отсекает рекламу и слежку" },
        new DnsResolver { Name = "Dns.SB", Address = "185.222.222.222", Note = "без цензуры" },
        new DnsResolver { Name = "OpenDNS", Address = "208.67.222.222", Note = "фильтрация по категориям" },
    ];

    /// <summary>Имя, которым проверяем. Короткое и заведомо существующее.</summary>
    private const string ProbeName = "example.com";

    /// <summary>
    /// Проверяет резолвер, беря лучшее из нескольких замеров.
    /// </summary>
    /// <remarks>
    /// Одиночный замер шумный: первый запрос платит за установку TLS,
    /// и разница выходит пятикратной. Google в первом прогоне показал
    /// 1121 мс, во втором 208 — сравнивать резолверы по такому нельзя.
    /// Берётся наименьшее: оно ближе к тому, что человек почувствует,
    /// когда соединение уже установлено.
    /// </remarks>
    public static async Task<DnsProbeResult> CheckAsync(
        DnsResolver resolver,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        DnsProbeResult? best = null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var result = await CheckOnceAsync(resolver, timeout, cancellationToken);

            if (!result.Works)
            {
                // Первая неудача не приговор — соединение могло не успеть.
                // Но если и вторая, вернём именно её причину.
                best ??= result;
                continue;
            }

            if (best is not { Works: true } || result.Latency < best.Latency)
                best = result;
        }

        return best!;
    }

    private static async Task<DnsProbeResult> CheckOnceAsync(
        DnsResolver resolver,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var handler = new HttpClientHandler();
            using var http = new HttpClient(handler) { Timeout = timeout };

            var query = BuildQuery(ProbeName);

            using var content = new ByteArrayContent(query);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");

            using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{resolver.Address}/dns-query")
            {
                Content = content,

                // HTTP/2 с откатом на 1.1. Часть серверов отвечает на 1.1
                // кодом 505: RFC 8484 рекомендует вторую версию, и некоторые
                // читают рекомендацию как требование. Без этого Quad9
                // показывался мёртвым, будучи живым.
                Version = System.Net.HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };

            using var response = await http.SendAsync(request, cancellationToken);

            stopwatch.Stop();

            if (!response.IsSuccessStatusCode)
            {
                return Failure(resolver, $"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // Ответ должен быть разбираемым и содержать хотя бы одну запись:
            // сервер, вернувший пустоту, имя не разрешил, чем бы он ни ответил
            // на уровне HTTP.
            if (!HasAnswer(body))
                return Failure(resolver, "ответ без записей");

            return new DnsProbeResult
            {
                Resolver = resolver,
                Works = true,
                Latency = stopwatch.Elapsed,
            };
        }
        catch (Exception ex)
        {
            return Failure(resolver, ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Проверяет все резолверы разом.
    /// </summary>
    public static async Task<IReadOnlyList<DnsProbeResult>> CheckAllAsync(
        IReadOnlyList<DnsResolver> resolvers,
        TimeSpan timeout,
        Action<DnsProbeResult>? onResult,
        CancellationToken cancellationToken)
    {
        var results = new List<DnsProbeResult>();
        var sync = new object();

        var tasks = resolvers.Select(async resolver =>
        {
            var result = await CheckAsync(resolver, timeout, cancellationToken);

            lock (sync)
            {
                results.Add(result);
                onResult?.Invoke(result);
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return results;
    }

    private static DnsProbeResult Failure(DnsResolver resolver, string error) => new()
    {
        Resolver = resolver,
        Works = false,
        Error = error,
    };

    /// <summary>
    /// Собирает запрос A-записи в проводном формате.
    /// </summary>
    /// <remarks>
    /// Вручную, без библиотеки: сообщение состоит из заголовка в двенадцать
    /// байт и имени, записанного по меткам. Тянуть зависимость ради тридцати
    /// строк, которые не поменяются никогда, того не стоит.
    /// </remarks>
    private static byte[] BuildQuery(string name)
    {
        var labels = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var message = new List<byte>(32);

        // Идентификатор нулевой: так требует RFC 8484, чтобы ответы можно было
        // кэшировать на стороне HTTP.
        message.AddRange([0x00, 0x00]);

        // Флаги: обычный запрос с рекурсией.
        message.AddRange([0x01, 0x00]);

        message.AddRange([0x00, 0x01]); // вопросов: 1
        message.AddRange([0x00, 0x00]); // ответов: 0
        message.AddRange([0x00, 0x00]); // записей полномочий: 0
        message.AddRange([0x00, 0x00]); // дополнительных: 0

        foreach (var label in labels)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);

            if (bytes.Length > 63)
                throw new ArgumentException($"Метка '{label}' длиннее 63 байт.", nameof(name));

            message.Add((byte)bytes.Length);
            message.AddRange(bytes);
        }

        message.Add(0x00);              // конец имени
        message.AddRange([0x00, 0x01]); // тип: A
        message.AddRange([0x00, 0x01]); // класс: IN

        return message.ToArray();
    }

    /// <summary>
    /// Есть ли в ответе хоть одна запись.
    /// </summary>
    /// <remarks>
    /// Читается только счётчик ответов из заголовка: разбирать записи целиком
    /// незачем, вопрос стоит «разрешилось ли имя», а не «во что именно».
    /// </remarks>
    private static bool HasAnswer(byte[] response)
    {
        if (response.Length < 12)
            return false;

        // Младшие четыре бита второго байта флагов — код возврата.
        int rcode = response[3] & 0x0F;

        if (rcode != 0)
            return false;

        int answers = (response[6] << 8) | response[7];
        return answers > 0;
    }
}
