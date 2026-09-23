using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace NetZapret.Proxy;

/// <summary>Провайдер DNS и то, как до него достучаться.</summary>
/// <param name="Udp">Адреса для обычного DNS на 53 порту.</param>
/// <param name="TlsAddress">Адрес для DoT и DoH; <c>null</c> — шифрованного нет.</param>
/// <param name="TlsName">Имя в SNI и сертификате.</param>
/// <param name="DohPath">Путь DoH; <c>null</c> — только DoT.</param>
public sealed record DnsProvider(
    string Name,
    IReadOnlyList<string> Udp,
    string? TlsAddress = null,
    string? TlsName = null,
    string? DohPath = "/dns-query");

/// <summary>Что выяснилось про одного провайдера.</summary>
public sealed record DnsSurveyRow
{
    public required DnsProvider Provider { get; init; }

    /// <summary>Лучшая задержка; <c>null</c> — не мерили или не прошло.</summary>
    public double? DohMs { get; init; }

    public double? DotMs { get; init; }

    public double? UdpMs { get; init; }

    /// <summary>Почему не прошло: «SYN DROP», «TLS DROP», «TIMEOUT»; пусто — прошло или не мерили.</summary>
    public string DohFailure { get; init; } = string.Empty;

    public string DotFailure { get; init; } = string.Empty;

    public string UdpFailure { get; init; } = string.Empty;

    /// <summary>Сколько адресов UDP ответили из скольких.</summary>
    public int UdpAnswered { get; init; }

    /// <summary>
    /// Адрес, с которого на самом деле ходит наружу тот, кто ответил на UDP.
    /// </summary>
    /// <remarks>
    /// Спрашивается <c>whoami.akamai.net</c>: авторитетный сервер Akamai
    /// отвечает адресом того, кто к нему пришёл. Если на запрос к 8.8.8.8
    /// пришёл резолвер российской сети — запрос перехвачен по дороге.
    /// </remarks>
    public string? RealResolver { get; init; }

    /// <summary>Сеть этого адреса по данным Team Cymru.</summary>
    public string? RealNetwork { get; init; }

    /// <summary>Сколько проверочных имён резолвер подменил, из скольких спрошенных.</summary>
    public int Spoofed { get; init; }

    public int SpoofChecked { get; init; }

    /// <summary>Перехвачен ли UDP по дороге: ответил не тот, кого спрашивали.</summary>
    public bool Intercepted { get; init; }
}

/// <summary>
/// Обзор резолверов: задержка DoH, DoT и UDP, кто отвечает на самом деле
/// и подменяет ли он ответы.
/// </summary>
/// <remarks>
/// <para>
/// Заведено 23.09 по снимку владельца — таблица из чужого инструмента,
/// где у Google и Cloudflare по UDP «реальный резолвер» оказался российским
/// (RIPN-RU-RND, RELARN), а подмена — пять из пяти. Такое не видно ни
/// по задержке, ни по тому, отвечает ли резолвер: он отвечает, только
/// не он.
/// </para>
/// <para>
/// Сокеты привязаны к физическому адаптеру (<c>IP_UNICAST_IF</c>), как
/// привязывает свои и sing-box. Иначе при работающем движке запросы
/// к системным резолверам уходили бы в TUN, и мерился бы наш движок,
/// а не сеть провайдера.
/// </para>
/// <para>
/// DoH и DoT — по адресу, с именем только в SNI: системный резолвер для
/// этого не спрашивается, иначе обзор резолверов зависел бы от одного из них.
/// </para>
/// </remarks>
public static class DnsSurvey
{
    /// <summary>
    /// Провайдеры обзора.
    /// </summary>
    /// <remarks>
    /// Шифрованные адреса взяты только там, где сертификат по этому адресу
    /// выписан на названное имя. Где не уверены — шифрованного нет, и строка
    /// честно меряет один UDP.
    /// </remarks>
    public static readonly IReadOnlyList<DnsProvider> Providers =
    [
        new("Google", ["8.8.8.8", "8.8.4.4"], "8.8.8.8", "dns.google"),
        new("Cloudflare", ["1.1.1.1", "1.0.0.1"], "1.1.1.1", "cloudflare-dns.com"),
        new("Quad9", ["9.9.9.9", "149.112.112.112"], "9.9.9.9", "dns.quad9.net"),
        new("AdGuard", ["94.140.14.14", "94.140.15.15"], "94.140.14.14", "dns.adguard-dns.com"),
        new("Alibaba", ["223.5.5.5", "223.6.6.6"], "223.5.5.5", "dns.alidns.com"),
        new("CleanBrowsing", ["185.228.168.9", "185.228.169.9"], "185.228.168.9",
            "security-filter-dns.cleanbrowsing.org", "/doh/security-filter/"),
        new("DNS.SB", ["185.222.222.222", "45.11.45.11"], "185.222.222.222", "dns.sb"),
        new("OpenDNS", ["208.67.222.222", "208.67.220.220"], "208.67.222.222", "doh.opendns.com"),
        new("Yandex", ["77.88.8.8", "77.88.8.1"], "77.88.8.8", "common.dot.dns.yandex.net"),
        new("Comss.one", ["83.220.169.155", "212.109.195.93"], "83.220.169.155", "dns.comss.one"),
        new("Mullvad", [], "194.242.2.2", "dns.mullvad.net"),
        new("NextDNS", ["45.90.28.0", "45.90.30.0"]),
        new("ControlD", ["76.76.2.0", "76.76.10.0"]),
        new("MSK-IX", ["62.76.76.62", "62.76.62.76"]),
        new("НСДИ", ["195.208.4.1", "195.208.5.1"]),
    ];

    /// <summary>
    /// Имена для проверки подмены: закрыты в России давно и адрес у них
    /// устойчивый, так что расхождение с честным ответом значит подмену,
    /// а не другой узел сети доставки.
    /// </summary>
    public static readonly IReadOnlyList<string> SpoofProbes =
    [
        "rutracker.org",
        "nnmclub.to",
        "rutor.info",
        "kinozal.tv",
        "linkedin.com",
    ];

    private const string WhoAmI = "whoami.akamai.net";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>Сколько раз мерить; берётся лучшее.</summary>
    private const int Tries = 3;

    /// <summary>
    /// Индекс физического адаптера с маршрутом по умолчанию; <c>null</c> — не нашли.
    /// </summary>
    /// <remarks>
    /// Туннель sing-box — тоже адаптер с адресом и шлюзом, и отличить его
    /// можно только по имени: он наш, и назван нами.
    /// </remarks>
    public static int? PhysicalInterface()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                || nic.Name.StartsWith("netzapret", StringComparison.OrdinalIgnoreCase)
                || nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase)
                || nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var props = nic.GetIPProperties();

            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                    && !g.Address.Equals(IPAddress.Any)))
            {
                continue;
            }

            return props.GetIPv4Properties()?.Index;
        }

        return null;
    }

    /// <summary>Привязывает сокет к адаптеру: исходящие пойдут через него, мимо TUN.</summary>
    private static void Bind(Socket socket, int? index)
    {
        if (index is not { } value)
            return;

        // IP_UNICAST_IF = 31; индекс — в сетевом порядке байт.
        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(value));
    }

    /// <summary>Один запрос по UDP; ответ и время, либо причина отказа.</summary>
    public static async Task<(DnsWire.Answer? Answer, double Ms, string Failure)> UdpAsync(
        string server, string name, ushort type, int? nic, CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Bind(socket, nic);

        var query = DnsWire.Query(name, type);
        var buffer = new byte[4096];
        var watch = Stopwatch.StartNew();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);

        try
        {
            var endpoint = new IPEndPoint(IPAddress.Parse(server), 53);
            await socket.SendToAsync(query, SocketFlags.None, endpoint, limit.Token);
            var received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), limit.Token);

            return (DnsWire.Parse(buffer.AsSpan(0, received.ReceivedBytes)), watch.Elapsed.TotalMilliseconds, string.Empty);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, 0, "TIMEOUT");
        }
        catch (SocketException)
        {
            return (null, 0, "TIMEOUT");
        }
    }

    /// <summary>Соединение TLS до адреса с именем в SNI.</summary>
    private static async Task<(SslStream? Stream, string Failure)> TlsAsync(
        string address, int port, string name, int? nic, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Bind(socket, nic);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);

        try
        {
            await socket.ConnectAsync(IPAddress.Parse(address), port, limit.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            return (null, "SYN DROP");
        }

        var stream = new SslStream(new NetworkStream(socket, ownsSocket: true));

        try
        {
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = name,
            }, limit.Token);

            return (stream, string.Empty);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            stream.Dispose();
            return (null, "TLS DROP");
        }
    }

    /// <summary>DoT: лучшая задержка запроса по одному соединению.</summary>
    public static async Task<(double? Ms, string Failure)> DotAsync(
        DnsProvider provider, int? nic, CancellationToken cancellationToken)
    {
        if (provider.TlsAddress is null || provider.TlsName is null)
            return (null, string.Empty);

        var (stream, failure) = await TlsAsync(provider.TlsAddress, 853, provider.TlsName, nic, cancellationToken);

        if (stream is null)
            return (null, failure);

        await using (stream)
        {
            double? best = null;

            for (int i = 0; i < Tries; i++)
            {
                var query = DnsWire.Query(DnsProbeName, DnsWire.TypeA);
                var framed = new byte[query.Length + 2];
                framed[0] = (byte)(query.Length >> 8);
                framed[1] = (byte)query.Length;
                query.CopyTo(framed, 2);

                var watch = Stopwatch.StartNew();

                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    limit.CancelAfter(Timeout);

                    await stream.WriteAsync(framed, limit.Token);

                    var head = new byte[2];
                    await stream.ReadExactlyAsync(head, limit.Token);
                    var body = new byte[(head[0] << 8) | head[1]];
                    await stream.ReadExactlyAsync(body, limit.Token);

                    if (DnsWire.Parse(body) is not null)
                        best = Math.Min(best ?? double.MaxValue, watch.Elapsed.TotalMilliseconds);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    return (best, best is null ? "TIMEOUT" : string.Empty);
                }
            }

            return (best, string.Empty);
        }
    }

    /// <summary>
    /// Клиент HTTP, который ходит по названному адресу и через физический
    /// адаптер, а имя держит только в SNI и заголовке Host.
    /// </summary>
    /// <remarks>
    /// Через HttpClient, а не вручную: часть серверов DoH говорит только
    /// HTTP/2 — Quad9 отвечал на HTTP/1.1 кодом 505, Mullvad и Alibaba рвали
    /// рукопожатие, не найдя h2 в ALPN.
    /// </remarks>
    private static HttpClient DohClient(string address, int? nic) =>
        new(new SocketsHttpHandler
        {
            ConnectTimeout = Timeout,
            ConnectCallback = async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                Bind(socket, nic);

                try
                {
                    await socket.ConnectAsync(IPAddress.Parse(address), context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        })
        {
            Timeout = Timeout,
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

    /// <summary>DoH: POST в проводном формате, лучшее из нескольких по одному клиенту.</summary>
    public static async Task<(double? Ms, string Failure)> DohAsync(
        DnsProvider provider, int? nic, CancellationToken cancellationToken)
    {
        if (provider.TlsAddress is null || provider.TlsName is null || provider.DohPath is null)
            return (null, string.Empty);

        using var http = DohClient(provider.TlsAddress, nic);
        var url = $"https://{provider.TlsName}{provider.DohPath}";
        double? best = null;

        for (int i = 0; i < Tries; i++)
        {
            var watch = Stopwatch.StartNew();

            try
            {
                using var content = new ByteArrayContent(DnsWire.Query(DnsProbeName, DnsWire.TypeA));
                content.Headers.ContentType = new("application/dns-message");

                // Версия — на самом запросе: DefaultRequestVersion клиента
                // действует только на то, что он строит сам, и собранный
                // вручную запрос уходил по HTTP/1.1 — отсюда 505 от Quad9.
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = content,
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                };
                request.Headers.Accept.Add(new("application/dns-message"));

                using var response = await http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                if (response.IsSuccessStatusCode && DnsWire.Parse(body) is not null)
                    best = Math.Min(best ?? double.MaxValue, watch.Elapsed.TotalMilliseconds);
                else if (best is null)
                    return (null, $"HTTP {(int)response.StatusCode}");
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (best is not null)
                    break;

                return (null, Classify(ex));
            }
        }

        return (best, string.Empty);
    }

    /// <summary>Отказ словами таблицы: соединение, рукопожатие или молчание.</summary>
    private static string Classify(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException)
                return "TLS DROP";

            if (e is SocketException)
                return "SYN DROP";
        }

        return "TIMEOUT";
    }
    /// <summary>Читает ответ HTTP/1.1 с Content-Length.</summary>
    private static async Task<(int Status, byte[] Body)> ReadHttpAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        var one = new byte[1];

        while (header.Count < 8192)
        {
            await stream.ReadExactlyAsync(one, cancellationToken);
            header.Add(one[0]);

            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n')
                break;
        }

        var text = Encoding.ASCII.GetString([.. header]);
        var lines = text.Split("\r\n");
        int status = int.TryParse(lines[0].Split(' ').ElementAtOrDefault(1), out var code) ? code : 0;

        int length = lines
            .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            .Select(l => int.TryParse(l[15..].Trim(), out var n) ? n : 0)
            .FirstOrDefault();

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, cancellationToken);

        return (status, body);
    }

    /// <summary>Имя для замера задержки: короткое и заведомо существующее.</summary>
    private const string DnsProbeName = "example.com";

    /// <summary>
    /// Сеть адреса по Team Cymru, через честный DoH; <c>null</c> — не выяснилось.
    /// </summary>
    public static async Task<string?> NetworkOfAsync(IPAddress address, int? nic, CancellationToken cancellationToken)
    {
        var octets = address.GetAddressBytes();
        var origin = $"{octets[3]}.{octets[2]}.{octets[1]}.{octets[0]}.origin.asn.cymru.com";

        var first = await HonestTxtAsync(origin, nic, cancellationToken);

        if (first is null || ParseCymruAsn(first) is not { } asn)
            return null;

        var second = await HonestTxtAsync($"AS{asn}.asn.cymru.com", nic, cancellationToken);

        return second is null ? $"AS{asn}" : ParseCymruName(second) ?? $"AS{asn}";
    }

    /// <summary>
    /// Номер сети из ответа <c>origin.asn.cymru.com</c>:
    /// «15169 | 8.8.8.0/24 | US | arin | 2000-03-30».
    /// </summary>
    public static string? ParseCymruAsn(string text)
    {
        var first = text.Split('|')[0].Trim().Split(' ')[0];

        return first.Length > 0 && first.All(char.IsDigit) ? first : null;
    }

    /// <summary>
    /// Имя сети из ответа <c>asn.cymru.com</c>:
    /// «15169 | US | arin | 2000-03-30 | GOOGLE, US» — берётся «GOOGLE».
    /// </summary>
    public static string? ParseCymruName(string text)
    {
        var parts = text.Split('|');

        if (parts.Length < 5)
            return null;

        var name = parts[4].Trim();
        int comma = name.LastIndexOf(',');

        if (comma > 0)
            name = name[..comma];

        // «RIPN-RU-RND RIPN ...» — первое слово и есть имя в реестре.
        int space = name.IndexOf(' ');

        return (space > 0 ? name[..space] : name).Trim();
    }

    /// <summary>Честный резолвер для служебных вопросов: Cloudflare по DoH.</summary>
    private static readonly DnsProvider Honest = Providers[1];

    /// <summary>Запрос TXT через честный DoH.</summary>
    private static async Task<string?> HonestTxtAsync(string name, int? nic, CancellationToken cancellationToken)
    {
        var answer = await HonestAsync(name, DnsWire.TypeTxt, nic, cancellationToken);

        return answer?.Texts.FirstOrDefault();
    }

    /// <summary>Один запрос через честный DoH.</summary>
    public static async Task<DnsWire.Answer?> HonestAsync(
        string name, ushort type, int? nic, CancellationToken cancellationToken)
    {
        var (stream, _) = await TlsAsync(Honest.TlsAddress!, 443, Honest.TlsName!, nic, cancellationToken);

        if (stream is null)
            return null;

        await using (stream)
        {
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(Timeout);

                var query = DnsWire.Query(name, type);
                var head = Encoding.ASCII.GetBytes(
                    $"POST {Honest.DohPath} HTTP/1.1\r\nHost: {Honest.TlsName}\r\n"
                    + "Content-Type: application/dns-message\r\nAccept: application/dns-message\r\n"
                    + $"Content-Length: {query.Length}\r\n\r\n");

                await stream.WriteAsync(head, limit.Token);
                await stream.WriteAsync(query, limit.Token);

                var (status, body) = await ReadHttpAsync(stream, limit.Token);

                return status == 200 ? DnsWire.Parse(body) : null;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Подменён ли ответ по имени.
    /// </summary>
    /// <remarks>
    /// Подмена — когда ответ не пересекается с честным и при этом выдаёт себя:
    /// пуст, указывает в служебную или частную сеть, либо одинаков для разных
    /// закрытых имён (заглушка оператора одна на всех). Одно расхождение
    /// адресов подменой не считается: у сетей доставки узлов десятки, и
    /// честные ответы двух резолверов законно разные.
    /// </remarks>
    public static bool IsSpoofed(
        IReadOnlyList<IPAddress> answer,
        IReadOnlyList<IPAddress> honest,
        IReadOnlySet<IPAddress> sharedAcrossNames)
    {
        if (honest.Count == 0)
            return false;

        if (answer.Count == 0)
            return true;

        if (answer.Any(honest.Contains))
            return false;

        return answer.Any(a => DnsSpoof.IsSinkhole(a) || DnsSpoof.IsPrivate(a) || sharedAcrossNames.Contains(a));
    }

    /// <summary>Обзор одного провайдера.</summary>
    public static async Task<DnsSurveyRow> SurveyAsync(
        DnsProvider provider,
        IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> honest,
        int? nic,
        CancellationToken cancellationToken)
    {
        var doh = DohAsync(provider, nic, cancellationToken);
        var dot = DotAsync(provider, nic, cancellationToken);

        double? udpBest = null;
        string udpFailure = string.Empty;
        int answered = 0;
        string? real = null;
        string? network = null;
        bool intercepted = false;
        int spoofed = 0;
        int checkedNames = 0;

        foreach (var server in provider.Udp)
        {
            bool ok = false;

            for (int i = 0; i < Tries; i++)
            {
                var (answer, ms, failure) = await UdpAsync(server, DnsProbeName, DnsWire.TypeA, nic, cancellationToken);

                if (answer is null)
                {
                    udpFailure = failure;
                    continue;
                }

                ok = true;
                udpBest = Math.Min(udpBest ?? double.MaxValue, ms);
            }

            if (ok)
                answered++;
        }

        if (answered > 0)
        {
            udpFailure = string.Empty;
            var server = provider.Udp[0];

            // Кто на самом деле ходит наружу за этот UDP.
            var (who, _, _) = await UdpAsync(server, WhoAmI, DnsWire.TypeA, nic, cancellationToken);

            if (who?.Addresses.FirstOrDefault() is { } egress)
            {
                real = egress.ToString();
                network = await NetworkOfAsync(egress, nic, cancellationToken);

                // Ответил не сам провайдер, если его выход в чужой сети.
                // Бывает и законно — Quad9 ходит наружу через партнёров, —
                // поэтому это признак для глаза, а не вердикт; вердикт —
                // подмена ниже.
                var own = await NetworkOfAsync(IPAddress.Parse(server), nic, cancellationToken);
                intercepted = network is not null && own is not null
                    && !string.Equals(network, own, StringComparison.OrdinalIgnoreCase);
            }

            // Подмена: те же закрытые имена, что у честного, — у этого по UDP.
            var answers = new Dictionary<string, IReadOnlyList<IPAddress>>();

            foreach (var name in SpoofProbes)
            {
                var (answer, _, _) = await UdpAsync(server, name, DnsWire.TypeA, nic, cancellationToken);

                if (answer is not null)
                    answers[name] = answer.Addresses;
            }

            var shared = answers.Values
                .SelectMany(a => a.Distinct())
                .GroupBy(a => a)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet();

            foreach (var (name, answer) in answers)
            {
                if (!honest.TryGetValue(name, out var truth) || truth.Count == 0)
                    continue;

                checkedNames++;

                if (IsSpoofed(answer, truth, shared))
                    spoofed++;
            }
        }

        var (dohMs, dohFailure) = await doh;
        var (dotMs, dotFailure) = await dot;

        return new DnsSurveyRow
        {
            Provider = provider,
            DohMs = dohMs,
            DohFailure = dohFailure,
            DotMs = dotMs,
            DotFailure = dotFailure,
            UdpMs = udpBest,
            UdpFailure = provider.Udp.Count == 0 ? string.Empty : udpFailure,
            UdpAnswered = answered,
            RealResolver = real,
            RealNetwork = network,
            Intercepted = intercepted,
            Spoofed = spoofed,
            SpoofChecked = checkedNames,
        };
    }

    /// <summary>
    /// Честные ответы по проверочным именам — один раз на весь обзор.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<IPAddress>>> HonestAnswersAsync(
        int? nic, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<IPAddress>>();

        foreach (var name in SpoofProbes)
        {
            var answer = await HonestAsync(name, DnsWire.TypeA, nic, cancellationToken);

            if (answer is not null)
                result[name] = answer.Addresses;
        }

        return result;
    }

    /// <summary>Обзор всех провайдеров, по нескольку разом.</summary>
    public static async Task<IReadOnlyList<DnsSurveyRow>> SurveyAllAsync(
        IReadOnlyList<DnsProvider>? providers = null,
        IProgress<DnsSurveyRow>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var list = providers ?? Providers;
        var nic = PhysicalInterface();
        var honest = await HonestAnswersAsync(nic, cancellationToken);

        using var slots = new SemaphoreSlim(4);

        var tasks = list.Select(async provider =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                var row = await SurveyAsync(provider, honest, nic, cancellationToken);
                progress?.Report(row);
                return row;
            }
            finally
            {
                slots.Release();
            }
        });

        return await Task.WhenAll(tasks);
    }
}
