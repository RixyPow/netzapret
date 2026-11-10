using System.Net;
using System.Net.Sockets;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Каким именам адрес узла видео отвечает мимо обхода.</summary>
public enum NodeFilter
{
    /// <summary>Не мерилось: мимо обхода пройти было нечем.</summary>
    Unknown,

    /// <summary>Отвечает любому имени — фильтра нет.</summary>
    Open,

    /// <summary>Отвечает только имени Google: фильтр по адресу.</summary>
    GoogleOnly,

    /// <summary>Не отвечает ни одному имени.</summary>
    Closed,
}

/// <summary>Что вышло по узлам видео в целом.</summary>
public enum VideoNodesVerdict
{
    /// <summary>Судить не по чему: узлов с адресом меньше <see cref="VideoNodes.NodeFloor"/>.</summary>
    NotMeasured,

    /// <summary>Фильтра нет, соединения встают.</summary>
    Fine,

    /// <summary>Фильтр есть, но соединения встают.</summary>
    FilteredButPasses,

    /// <summary>Часть соединений не встаёт.</summary>
    Lossy,

    /// <summary>По TCP не встаёт ни одно.</summary>
    Closed,
}

/// <summary>Замер одного зарубежного узла видео.</summary>
public sealed record VideoNode
{
    public required string Host { get; init; }

    /// <summary>Адрес, по которому мерили; <c>null</c> — у имени нет адреса.</summary>
    public string? Address { get; init; }

    /// <summary>Имя заведено в туннель (адрес подставной) — мимо него не мерили.</summary>
    public bool Tunnelled { get; init; }

    /// <summary>Сколько раз спрошено по TCP своим именем узла.</summary>
    public int Tries { get; init; }

    /// <summary>Сколько раз узел ответил ServerHello.</summary>
    public int Tcp { get; init; }

    /// <summary>Сколько раз спрошено по QUIC; ноль — QUIC на машине нет.</summary>
    public int QuicTries { get; init; }

    public int Quic { get; init; }

    public NodeFilter Filter { get; init; }

    public bool Measured => Address is not null && !Tunnelled && Tries > 0;

    /// <summary>Имя узла без общей зоны: её повторять в каждой строке незачем.</summary>
    public string Short => Host.EndsWith(VideoNodes.Zone, StringComparison.OrdinalIgnoreCase)
        ? Host[..^VideoNodes.Zone.Length]
        : Host;

    public string Describe()
    {
        if (Address is null)
            return $"{Short} — нет адреса у имени";

        if (Tunnelled)
            return $"{Short} — имя заведено в туннель, мимо него не мерилось";

        var filter = Filter switch
        {
            NodeFilter.Open => "; мимо обхода отвечает любому имени",
            NodeFilter.GoogleOnly => "; мимо обхода — только имени Google",
            NodeFilter.Closed => "; мимо обхода не отвечает ни одному имени",
            _ => string.Empty,
        };

        return $"{Short} ({Address}) — TCP {Tcp} из {Tries}"
            + (QuicTries > 0 ? $", QUIC {Quic} из {QuicTries}" : string.Empty)
            + filter;
    }
}

/// <summary>Замер зарубежных узлов видео за один прогон.</summary>
public sealed record VideoNodesReport
{
    public required IReadOnlyList<VideoNode> Nodes { get; init; }

    /// <summary>Работал ли десинк: без него числа — о голой сети, а не об обходе.</summary>
    public bool Desync { get; init; }

    public IReadOnlyList<VideoNode> Measured => Nodes.Where(n => n.Measured).ToList();

    public int Tries => Measured.Sum(n => n.Tries);

    public int Tcp => Measured.Sum(n => n.Tcp);

    public int QuicTries => Measured.Sum(n => n.QuicTries);

    public int Quic => Measured.Sum(n => n.Quic);

    /// <summary>Узлов под фильтром: отвечают только имени Google или никому.</summary>
    public int Filtered => Measured.Count(n => n.Filter is NodeFilter.GoogleOnly or NodeFilter.Closed);

    public VideoNodesVerdict Verdict
    {
        get
        {
            if (Measured.Count < VideoNodes.NodeFloor)
                return VideoNodesVerdict.NotMeasured;

            if (Tcp == 0)
                return VideoNodesVerdict.Closed;

            if (Tcp < Tries * VideoNodes.PassShare || Quic < QuicTries * VideoNodes.PassShare)
                return VideoNodesVerdict.Lossy;

            return Filtered > 0 ? VideoNodesVerdict.FilteredButPasses : VideoNodesVerdict.Fine;
        }
    }

    /// <summary>Числа одной строкой: «TCP 25 из 30, QUIC 23 из 30, под фильтром 8 узлов из 10».</summary>
    public string Totals =>
        $"TCP {Tcp} из {Tries}"
        + (QuicTries > 0 ? $", QUIC {Quic} из {QuicTries}" : string.Empty)
        + (Nodes.Any(n => n.Measured && n.Filter != NodeFilter.Unknown)
            ? $", под фильтром {Filtered} из {Measured.Count} узлов"
            : string.Empty);

    /// <summary>Итог словами; <c>null</c> — судить не по чему.</summary>
    public string? Summary => Verdict switch
    {
        VideoNodesVerdict.NotMeasured => Nodes.Count > 0 && Nodes.All(n => n.Tunnelled)
            ? "узлы видео YouTube заведены в туннель — мимо него не мерились"
            : null,
        VideoNodesVerdict.Closed => $"не открываются{Bare}: {Totals}",
        VideoNodesVerdict.Lossy => $"открываются не всегда{Bare}: {Totals}",
        VideoNodesVerdict.FilteredButPasses => $"под фильтром, соединения проходят: {Totals}",
        _ => $"открываются: {Totals}",
    };

    private string Bare => Desync ? string.Empty : " (десинк не работал — это сеть без обхода)";

    /// <summary>По строке на узел.</summary>
    public IReadOnlyList<string> Lines => Nodes.Select(n => n.Describe()).ToList();
}

/// <summary>
/// Замер зарубежных узлов видео Google — тех, с которых YouTube отдаёт то, чего
/// нет в кэше провайдера.
/// </summary>
/// <remarks>
/// <para>
/// Проверка блокировок спрашивала youtube.com и узлы из кэша DNS, а это узел
/// провайдера: с него идёт популярное, и он здоров. Редкий или старый ролик,
/// свежую загрузку и дальнее место при перемотке кэш провайдера переадресует
/// на зарубежный узел Google (замер 10.11, SevenSky: шесть роликов на 22–566
/// просмотров, все ушли на 74.125.x и 173.194.x). На большинстве таких узлов
/// с 19–24.10 стоит фильтр по адресу: рукопожатие проходит только с именем
/// Google. Человек видит это как «популярное играет, редкое не идёт», а отчёт
/// его проверки говорил «YouTube доступен».
/// </para>
/// <para>
/// Мерится три вещи, и нужны все три. Своим именем узла по TCP и по QUIC —
/// как пойдёт плеер: через обход, если он работает. И мимо обхода (порты
/// пробы) двумя чужими именами — что делает сама сеть: отвечает узел любому
/// имени, только имени Google или никому. Без третьего не отличить «рецепт
/// не справляется» от «сеть теряет соединения сама»: 10.11 те же адреса мимо
/// обхода с именем www.google.com ответили 78 раз из 96, через обход своим
/// именем — 82 из 96, по QUIC — 47 из 60. Чистые узлы тех же сетей за четыре
/// прогона этого замера не потеряли ничего (24 из 24 по TCP и по QUIC):
/// теряет фильтр, а не сеть, и потому два чистых узла стоят в наборе мерилом.
/// </para>
/// <para>
/// К одному адресу — одно соединение за раз: одиннадцать разом на адрес
/// давали разброс, по которому ничего не сравнить (10.11). Узлы идут разом —
/// адреса у них разные.
/// </para>
/// </remarks>
public static class VideoNodes
{
    /// <summary>Общая зона узлов.</summary>
    public const string Zone = ".googlevideo.com";

    /// <summary>Сколько раз спрашивать узел каждым путём.</summary>
    /// <remarks>
    /// Теряется около пятой части соединений, и по одному разу на узел этого
    /// не видно. Три раза на десять узлов — тридцать соединений на путь:
    /// пятую часть от случайной единичной потери это отличает.
    /// </remarks>
    public const int Tries = 3;

    /// <summary>Меньше стольких узлов с адресом — судить не по чему.</summary>
    public const int NodeFloor = 4;

    /// <summary>Доля ответов, ниже которой «открываются не всегда».</summary>
    /// <remarks>Одна потеря из тридцати — случай; четыре — уже нет.</remarks>
    public const double PassShare = 0.9;

    /// <summary>Имя Google, которое фильтр по адресу пропускает.</summary>
    public const string AllowedName = "www.google.com";

    /// <summary>
    /// Узлы — по одному из группы.
    /// </summary>
    /// <remarks>
    /// Группы, на которые кэш SevenSky в Москве отправлял плеер 09–10.11 (второй
    /// узел в <c>mn=</c> ссылки потока и переадресации редких роликов). Восемь
    /// из них были под фильтром, две последние — чистые, 74.125.173.38
    /// и 173.194.188.70: по ним видно, фильтр это или легла сеть. В другом
    /// городе плеер получит другие группы, но вопрос тот же — открываются ли
    /// отсюда зарубежные узлы Google, — и этот набор на него отвечает. Имя,
    /// у которого не стало адреса, в счёт не идёт.
    /// </remarks>
    public static IReadOnlyList<string> Hosts { get; } =
    [
        "rr1---sn-4g5ednrz" + Zone,
        "rr1---sn-4g5ednrs" + Zone,
        "rr1---sn-aj5go5-5p" + Zone,
        "rr1---sn-aj5goc-5j" + Zone,
        "rr1---sn-aj5goc-5x" + Zone,
        "rr1---sn-aj5go5-53" + Zone,
        "rr1---sn-u14g55-cw" + Zone,
        "rr1---sn-5go7yner" + Zone,
        "rr1---sn-4g5lznl6" + Zone,
        "rr1---sn-4g5ednsl" + Zone,
    ];

    /// <summary>
    /// Касается ли прогон YouTube: есть ли среди проверяемых имён его зоны.
    /// </summary>
    /// <remarks>
    /// Точечная проверка другого сервиса узлы видео не трогает: тридцать лишних
    /// соединений к Google ради вопроса про Discord ни к чему.
    /// </remarks>
    public static bool Concerns(IEnumerable<string> hosts) => hosts.Any(host =>
        host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
        || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(Zone, StringComparison.OrdinalIgnoreCase));

    /// <summary>Сколько ждать соединения; ответа на приветствие проба ждёт сама, три секунды.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Мерит узлы; до полуминуты, когда не отвечает никто.
    /// </summary>
    /// <param name="desync">Работает ли десинк — только для слов итога.</param>
    /// <param name="aside">
    /// Можно ли пройти мимо обхода с портов пробы: десинк их не перехватывает
    /// (<c>KeepsProbesOut</c>) или не работает вовсе. Нельзя — род фильтра
    /// остаётся неизвестным.
    /// </param>
    public static async Task<VideoNodesReport> MeasureAsync(bool desync, bool aside, CancellationToken cancellationToken)
    {
        // Через физический адаптер, как пробы «Десинк» и «Напрямую»: соединение
        // с порта пробы, ушедшее в TUN, движок открыл бы заново со своего порта,
        // и мимо обхода оно бы уже не прошло.
        using var adapter = ProbePorts.Through(PhysicalNetwork.Interface());

        var nodes = await Task.WhenAll(Hosts.Select(host => MeasureNodeAsync(host, aside, cancellationToken)));

        return new VideoNodesReport { Nodes = nodes, Desync = desync };
    }

    private static async Task<VideoNode> MeasureNodeAsync(string host, bool aside, CancellationToken cancellationToken)
    {
        IPAddress? address;

        try
        {
            address = (await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork, cancellationToken)).FirstOrDefault();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            address = null;
        }

        if (address is null)
            return new VideoNode { Host = host };

        if (TunnelHealth.IsFakeIp(address))
            return new VideoNode { Host = host, Address = address.ToString(), Tunnelled = true };

        int tcp = 0;

        for (int i = 0; i < Tries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Браузерным приветствием: его и увидит фильтр от Хрома — длинное,
            // двумя пакетами. Ответ — ServerHello, а не отказ: узел своё имя знает.
            if ((await BrowserHello.AskAsync(address, host, Timeout, cancellationToken)).Answer == HelloAnswer.Accepted)
                tcp++;
        }

        int quic = 0, quicTries = 0;

        for (int i = 0; i < Tries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // null — QUIC на этой машине нет, и спрашивать дальше незачем.
            if (await QuicProbe.AskAsync(address, host, Timeout, cancellationToken) is not { } answer)
                break;

            quicTries++;

            if (answer.Ok)
                quic++;
        }

        return new VideoNode
        {
            Host = host,
            Address = address.ToString(),
            Tries = Tries,
            Tcp = tcp,
            QuicTries = quicTries,
            Quic = quic,
            Filter = aside ? await FilterAsync(address, cancellationToken) : NodeFilter.Unknown,
        };
    }

    /// <summary>Спрашивает адрес мимо обхода двумя чужими именами.</summary>
    private static async Task<NodeFilter> FilterAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var ports = ProbePorts.Use();

        bool neutral = BlockCheck.Reached(
            await BrowserHello.AskAsync(address, BlockCheck.NameProbeNeutral, Timeout, cancellationToken));

        if (neutral)
            return NodeFilter.Open;

        // Имя Google — до трёх раз: и с ним теряется около пятой части соединений
        // (78 из 96, 10.11), и по одной потере узел сошёл бы за закрытый. С двумя
        // попытками первый же прогон 10.11 назвал закрытым узел, отвечавший
        // через обход 3 из 3.
        for (int i = 0; i < Tries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (BlockCheck.Reached(await BrowserHello.AskAsync(address, AllowedName, Timeout, cancellationToken)))
                return NodeFilter.GoogleOnly;
        }

        return NodeFilter.Closed;
    }
}
