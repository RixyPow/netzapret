using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NetZapret.Proxy;

/// <summary>Чем именно закрыт доступ.</summary>
public enum BlockKind
{
    /// <summary>Открыт.</summary>
    None,

    /// <summary>Соединение рвётся на рукопожатии TLS — почерк DPI.</summary>
    TlsDpi,

    /// <summary>
    /// Не встаёт соединение на 443, тогда как 80 отвечает.
    /// </summary>
    /// <remarks>
    /// Маршрут до хоста жив — иначе не ответил бы и 80. Закрыт именно порт,
    /// и рукопожатия, в которое мог бы вмешаться десинк, попросту не
    /// происходит: остаётся туннель.
    /// </remarks>
    HttpsPort,

    /// <summary>Не отвечает ничего, включая обычный HTTP.</summary>
    Full,

    /// <summary>Имя резолвится в подставной адрес.</summary>
    Dns,

    /// <summary>
    /// У имени нет адреса вовсе.
    /// </summary>
    /// <remarks>
    /// Не блокировка. Так устроены базовые домены сетей доставки —
    /// <c>akamai.net</c>, <c>cloudfront.net</c>: записи A у них нет и не было,
    /// работают только поддомены. Выдавать это за подмену значит советовать
    /// VPN там, где лечить нечего.
    /// </remarks>
    NoAddress,
}

/// <summary>Исход одной пробы.</summary>
public sealed record ProbeOutcome
{
    public required bool Ok { get; init; }

    /// <summary>Соединение оборвано удалённой стороной — признак DPI.</summary>
    public bool Reset { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? Detail { get; init; }

    public string Describe() => Ok ? "ок" : Reset ? "RST" : Detail is null ? "—" : "нет";
}

/// <summary>Что выяснилось про одну цель.</summary>
public sealed record TargetReport
{
    public required string Host { get; init; }

    /// <summary>Сервис, к которому цель относится; для показа.</summary>
    public string? Service { get; init; }

    public required ProbeOutcome Tcp { get; init; }
    public required ProbeOutcome Tls12 { get; init; }
    public required ProbeOutcome Tls13 { get; init; }
    public required ProbeOutcome Http { get; init; }

    public required BlockKind Kind { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    public string Describe() => Kind switch
    {
        BlockKind.None => "доступен",
        BlockKind.TlsDpi => "DPI по TLS",
        BlockKind.HttpsPort => "порт 443 закрыт",
        BlockKind.Full => "закрыт полностью",
        BlockKind.Dns => "подмена DNS",
        _ => "нет адреса у имени",
    };

    /// <summary>Нужно ли вообще о нём говорить в итоге.</summary>
    /// <remarks>
    /// Имя без адреса — свойство списка, а не сети: советовать по нему
    /// нечего, и в перечне закрытого оно только сбивает.
    /// </remarks>
    public bool Actionable => Kind is not (BlockKind.None or BlockKind.NoAddress);

    /// <summary>
    /// Чем лечится.
    /// </summary>
    /// <remarks>
    /// Десинк работает с рукопожатием TLS: если рвут именно его, подмена
    /// пакетов помогает. Если же не отвечает ничего, включая обычный HTTP,
    /// значит закрыт сам маршрут до хоста, и вмешательство в рукопожатие
    /// бессмысленно — остаётся туннель.
    /// </remarks>
    public string Remedy() => Kind switch
    {
        BlockKind.None => "ничего не нужно",
        BlockKind.TlsDpi => "десинк",
        BlockKind.HttpsPort => "только VPN",
        BlockKind.Full => "только VPN",
        BlockKind.Dns => "VPN либо свой DNS",
        _ => "ничего — у имени нет адреса",
    };
}

/// <summary>Что вообще умеет эта сеть.</summary>
public sealed record NetworkBaseline
{
    public required bool Tls { get; init; }
    public required bool Http { get; init; }
    public required bool Doh { get; init; }
    public required bool Icmp { get; init; }
    public required bool IpV6 { get; init; }
}

/// <summary>
/// Измеряет, что и как закрыто в этой сети.
/// </summary>
/// <remarks>
/// <para>
/// Отвечает на вопрос «чем лечить», а не только «работает ли». Различие
/// в почерке: обрыв на рукопожатии TLS при живом TCP означает DPI, который
/// разбирает SNI, — с таким справляется десинк. Молчание по всем протоколам
/// означает закрытый маршрут, и десинку там делать нечего.
/// </para>
/// <para>
/// Проверка идёт по настоящим соединениям, а не по спискам блокировок:
/// списки говорят, что должно быть закрыто, а нас интересует, что закрыто
/// у этого человека на этом операторе сегодня.
/// </para>
/// </remarks>
public static class BlockCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Выясняет, что доступно в принципе.
    /// </summary>
    /// <remarks>
    /// Без этого выводы поплывут: недоступный ICMP легко принять за блокировку,
    /// а отсутствие IPv6 — за подмену адресов. Сперва отделяем свойства сети
    /// от следов вмешательства.
    /// </remarks>
    public static async Task<NetworkBaseline> MeasureBaselineAsync(CancellationToken cancellationToken)
    {
        var tls = await TlsAsync("www.google.com", SslProtocols.None, cancellationToken);
        var http = await HttpAsync("www.google.com", cancellationToken);

        var doh = await DnsProbe.CheckAsync(
            DnsProbe.Known.First(r => r.Address == "1.1.1.1"),
            Timeout,
            cancellationToken);

        bool icmp;
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync("1.1.1.1", (int)Timeout.TotalMilliseconds);
            icmp = reply.Status == System.Net.NetworkInformation.IPStatus.Success;
        }
        catch (Exception)
        {
            icmp = false;
        }

        bool ipv6;
        try
        {
            var addresses = await Dns.GetHostAddressesAsync("ipv6.google.com", cancellationToken);
            ipv6 = addresses.Any(a => a.AddressFamily == AddressFamily.InterNetworkV6)
                && await TcpAsync("ipv6.google.com", 443, AddressFamily.InterNetworkV6, cancellationToken) is { Ok: true };
        }
        catch (Exception)
        {
            ipv6 = false;
        }

        return new NetworkBaseline
        {
            Tls = tls.Ok,
            Http = http.Ok,
            Doh = doh.Works,
            Icmp = icmp,
            IpV6 = ipv6,
        };
    }

    /// <summary>
    /// Проверяет цель, перепроверяя неудачу.
    /// </summary>
    /// <remarks>
    /// Одиночная проба выносит приговор по одному соединению, а разовый
    /// таймаут случается и на исправной сети. Проверено на себе:
    /// <c>updates.discord.com</c> был объявлен закрытым полностью, а при
    /// повторе оказался доступен по всем протоколам — оба порта открыты,
    /// имя резолвится. Средству, по которому правят пресет, такое
    /// непозволительно.
    /// </remarks>
    public static async Task<TargetReport> CheckAsync(
        string host,
        string? service,
        CancellationToken cancellationToken)
    {
        var first = await ProbeAsync(host, service, cancellationToken);

        if (first.Kind == BlockKind.None)
            return first;

        // Второй заход только для неудач: подтверждать успех незачем,
        // а вот обвинять с одного раза нельзя.
        var second = await ProbeAsync(host, service, cancellationToken);

        return second.Kind == BlockKind.None ? second : Worse(first, second);
    }

    /// <summary>
    /// Из двух неудачных проб берёт ту, что говорит о меньшем.
    /// </summary>
    /// <remarks>
    /// Если один заход показал живой TCP, а другой нет, верить надо первому:
    /// достучаться до закрытого маршрута нельзя, а не достучаться до живого —
    /// запросто.
    /// </remarks>
    private static TargetReport Worse(TargetReport a, TargetReport b) =>
        a.Kind == BlockKind.Full && b.Kind != BlockKind.Full ? b : a;

    private static async Task<TargetReport> ProbeAsync(
        string host,
        string? service,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;

        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        }
        catch (Exception)
        {
            addresses = [];
        }

        if (addresses.All(a => a.AddressFamily != AddressFamily.InterNetwork))
        {
            return new TargetReport
            {
                Host = host,
                Service = service,
                Tcp = Failed("записи A нет"),
                Tls12 = Failed(null),
                Tls13 = Failed(null),
                Http = Failed(null),
                Kind = BlockKind.NoAddress,
            };
        }

        var tcp = await TcpAsync(host, 443, AddressFamily.InterNetwork, cancellationToken);

        // Пробы идут последовательно, а не разом: одновременные соединения
        // к одному хосту DPI иногда обрывает скопом, и картина смазывается.
        var tls12 = tcp.Ok ? await TlsAsync(host, SslProtocols.Tls12, cancellationToken) : Failed(null);
        var tls13 = tcp.Ok ? await TlsAsync(host, SslProtocols.Tls13, cancellationToken) : Failed(null);
        var http = await HttpAsync(host, cancellationToken);

        return new TargetReport
        {
            Host = host,
            Service = service,
            Tcp = tcp,
            Tls12 = tls12,
            Tls13 = tls13,
            Http = http,
            Kind = Classify(tcp, tls12, tls13, http),
            Addresses = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString()).Take(3).ToList(),
        };
    }

    /// <summary>
    /// Ставит диагноз по совокупности проб.
    /// </summary>
    /// <remarks>
    /// Порядок проверок отражает то, насколько уверенно каждый признак говорит
    /// о вмешательстве. Живой TCP при мёртвом TLS — самый ясный след: до хоста
    /// достучались, а разговор оборвали, разобрав имя в рукопожатии.
    /// </remarks>
    internal static BlockKind Classify(ProbeOutcome tcp, ProbeOutcome tls12, ProbeOutcome tls13, ProbeOutcome http)
    {
        if (tls12.Ok || tls13.Ok)
            return BlockKind.None;

        if (tcp.Ok && (tls12.Reset || tls13.Reset))
            return BlockKind.TlsDpi;

        // TCP есть, TLS не отвечает без обрыва — так выглядит тихое
        // отбрасывание пакетов, и лечится оно тем же десинком.
        if (tcp.Ok)
            return BlockKind.TlsDpi;

        // Дальше TCP на 443 не встал, и всё решает 80-й порт. Если он ответил,
        // маршрут до хоста жив и закрыт именно 443 — говорить «закрыт
        // полностью», показывая рядом «HTTP ок», значит противоречить самому
        // себе в одной строке.
        return http.Ok ? BlockKind.HttpsPort : BlockKind.Full;
    }

    private static ProbeOutcome Failed(string? detail) => new()
    {
        Ok = false,
        Detail = detail,
    };

    private static async Task<ProbeOutcome> TcpAsync(
        string host,
        int port,
        AddressFamily family,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient(family);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            await client.ConnectAsync(host, port, timeout.Token);

            return new ProbeOutcome { Ok = true, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Reset = IsReset(ex),
                Elapsed = stopwatch.Elapsed,
                Detail = Explain(ex),
            };
        }
    }

    private static async Task<ProbeOutcome> TlsAsync(
        string host,
        SslProtocols protocol,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            await client.ConnectAsync(host, 443, timeout.Token);

            // Сертификат не проверяется: нас занимает, доходит ли рукопожатие,
            // а не доверяем ли мы стороне. Подмена — отдельная проверка,
            // и сертификат там как раз проверяется строго.
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = protocol,
                },
                timeout.Token);

            return new ProbeOutcome { Ok = true, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Reset = IsReset(ex),
                Elapsed = stopwatch.Elapsed,
                Detail = Explain(ex),
            };
        }
    }

    /// <summary>
    /// Обычный HTTP на 80 порту.
    /// </summary>
    /// <remarks>
    /// Нужен, чтобы отличить вмешательство в TLS от закрытого маршрута:
    /// если по 80 порту хост отвечает, значит он достижим, и дело именно
    /// в рукопожатии.
    /// </remarks>
    private static async Task<ProbeOutcome> HttpAsync(string host, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            await client.ConnectAsync(host, 80, timeout.Token);

            var request = System.Text.Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\nUser-Agent: NetZapret\r\n\r\n");

            var stream = client.GetStream();
            await stream.WriteAsync(request, timeout.Token);

            var buffer = new byte[512];
            int read = await stream.ReadAsync(buffer, timeout.Token);

            if (read <= 0)
                return new ProbeOutcome { Ok = false, Elapsed = stopwatch.Elapsed, Detail = "пустой ответ" };

            var head = System.Text.Encoding.ASCII.GetString(buffer, 0, read);

            return new ProbeOutcome
            {
                Ok = head.StartsWith("HTTP/", StringComparison.Ordinal),
                Elapsed = stopwatch.Elapsed,
                Detail = head.Split('\r')[0],
            };
        }
        catch (Exception ex)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Reset = IsReset(ex),
                Elapsed = stopwatch.Elapsed,
                Detail = Explain(ex),
            };
        }
    }

    /// <summary>
    /// Переводит отказ на язык, на котором о нём можно думать.
    /// </summary>
    /// <remarks>
    /// Истёкший у нас таймаут приходит как «операция отменена», и в отчёте это
    /// выглядело сообщением от сети — будто хост так ответил. На деле ответа
    /// не было вовсе, и читающий вправе знать, что ждали мы, а не нам отказали.
    /// </remarks>
    private static string Explain(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException)
                return $"нет ответа за {Timeout.TotalSeconds:0} с";
        }

        return ex.GetBaseException().Message;
    }

    /// <summary>
    /// Оборвано ли соединение удалённой стороной.
    /// </summary>
    /// <remarks>
    /// Именно этот признак отличает DPI от заглохшего маршрута: пакет RST
    /// приходит быстро и намеренно, тогда как закрытый маршрут даёт тишину
    /// до истечения времени.
    /// </remarks>
    private static bool IsReset(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
            {
                if (socket.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted)
                    return true;
            }

            // Обрыв посреди рукопожатия TLS приходит как неожиданный конец
            // потока — на уровне сокета его уже не видно.
            if (e is IOException io && io.Message.Contains("EOF", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Ищет имена, где системный резолвер даёт нерабочий адрес.
    /// </summary>
    /// <returns>Имена, у которых адрес от DoH работает, а системный — нет.</returns>
    /// <remarks>
    /// <para>
    /// Задумывалось как поиск подмены DNS, но так называть результат нельзя:
    /// снаружи не отличить подставленный оператором адрес от честного, до
    /// которого закрыт маршрут. Зато измеряемое различие само по себе
    /// осмысленно — и лечится сменой резолвера, что программа умеет.
    /// </para>
    /// <para>
    /// Две проверки, и обе нужны. Одного расхождения адресов мало: у крупных
    /// сайтов они раздаются по географии, и на этом проверка обвинила пять имён
    /// подряд (<c>youtube.com</c>, <c>telegram.org</c>, <c>facebook.com</c>
    /// и другие), где оба резолвера ответили честно. Одного сертификата тоже
    /// мало: <c>googlevideo.com</c> не обслуживает голое имя вовсе, и «адрес
    /// не предъявил сертификат» там ничего не значит. Поэтому сперва
    /// устанавливается точка отсчёта — что честный адрес сертификат
    /// предъявляет, — и лишь затем то же спрашивается у системного.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<string>> FindBadSystemAddressesAsync(
        IReadOnlyList<string> hosts,
        CancellationToken cancellationToken)
    {
        var suspicious = new List<string>();

        foreach (var host in hosts)
        {
            try
            {
                var system = (await Dns.GetHostAddressesAsync(host, cancellationToken))
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToHashSet(StringComparer.Ordinal);

                if (system.Count == 0)
                    continue;

                var doh = await DohResolveAsync(host, cancellationToken);

                if (doh.Count == 0)
                    continue;

                if (system.Overlaps(doh))
                    continue;

                // Адрес от DoH — точка отсчёта. Если и он не предъявляет
                // сертификата на это имя, сравнивать не с чем: так ведут себя
                // домены, у которых TLS живёт только на поддоменах
                // (googlevideo.com — ровно такой). Молчим.
                if (!await CertificateMatchesAsync(host, doh.First(), cancellationToken))
                    continue;

                // Точка отсчёта есть: имя обслуживается по TLS, и честный
                // адрес это подтверждает. Раз системный не подтверждает,
                // дело именно в адресе — SNI-то в обоих случаях настоящий.
                if (!await CertificateMatchesAsync(host, system.First(), cancellationToken))
                    suspicious.Add(host);
            }
            catch (Exception)
            {
                // Неразрешимое имя — забота другой проверки.
            }
        }

        return suspicious;
    }

    /// <summary>
    /// Предъявляет ли адрес настоящий сертификат этого имени.
    /// </summary>
    /// <remarks>
    /// Единственный надёжный способ отличить подмену от географии. Проверка
    /// строгая, с цепочкой доверия: подставленный оператором адрес честного
    /// сертификата не покажет, а вот узел той же сети доставки — покажет.
    /// </remarks>
    private static async Task<bool> CertificateMatchesAsync(
        string host,
        string address,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            await client.ConnectAsync(IPAddress.Parse(address), 443, timeout.Token);

            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            await ssl.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions { TargetHost = host },
                timeout.Token);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<HashSet<string>> DohResolveAsync(string host, CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.Accept.Add(new("application/dns-json"));

            var json = await http.GetStringAsync(
                $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A",
                cancellationToken);

            using var document = System.Text.Json.JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Answer", out var answers))
                return result;

            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("data", out var data)
                    && IPAddress.TryParse(data.GetString(), out var address)
                    && address.AddressFamily == AddressFamily.InterNetwork)
                {
                    result.Add(address.ToString());
                }
            }
        }
        catch (Exception)
        {
        }

        return result;
    }
}
