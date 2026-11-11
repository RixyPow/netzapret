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

    /// <summary>Узел кэша провайдера, а не зарубежный: в общий счёт не идёт.</summary>
    public bool Cache { get; init; }

    /// <summary>
    /// Узел, к которому Google приписал сеть клиента, — площадка самого Google
    /// (<c>fra16s69</c>), а не кэш у провайдера (<c>sevensky-vko2</c>).
    /// </summary>
    public bool GoogleSite { get; init; }

    /// <summary>Задержка до узла: наименьшее время установки TCP; <c>null</c> — не соединился ни разу.</summary>
    public int? PingMs { get; init; }

    /// <summary>Время каждой попытки TCP от SYN до ServerHello, мс; <c>null</c> — ответа не было.</summary>
    public IReadOnlyList<int?> TcpMs { get; init; } = [];

    /// <summary>Время каждого рукопожатия QUIC, мс; <c>null</c> — не прошло.</summary>
    public IReadOnlyList<int?> QuicMs { get; init; } = [];

    public bool Measured => Address is not null && !Tunnelled && Tries > 0;

    /// <summary>
    /// Сколько ответов по TCP пришло с задержкой: дольше двух задержек до узла
    /// на <see cref="VideoNodes.LagMs"/> и больше.
    /// </summary>
    /// <remarks>
    /// Обычное рукопожатие — две задержки (SYN и приветствие). Соединению с малым
    /// окном (рецепт YT FIX) сервер шлёт первые два байта по своему таймеру,
    /// через 0,27–0,34 с (замер 10.11); столько же вечером прибавляет и сам узел
    /// каждому третьему-четвёртому соединению. По одной попытке одно от другого
    /// не отличить — в отчёте это названо как есть.
    /// </remarks>
    public int Lagged => PingMs is { } ping
        ? TcpMs.Count(ms => ms is { } took && took >= 2 * ping + VideoNodes.LagMs)
        : 0;

    /// <summary>
    /// Потолок скорости одного соединения с малым окном, Мбит/с; <c>null</c> — задержка не мерилась.
    /// </summary>
    /// <remarks>
    /// Соединение без множителя окна — 65 535 Б на задержку. Замер 11.11 на
    /// фронтах Google в сетях узлов: 36 мс — 16 Мбит/с, 50 мс — 9,6–10,6.
    /// </remarks>
    public double? CeilingMbit => PingMs is > 0 and var ping ? 65535 * 8 / (ping / 1000.0) / 1e6 : null;

    /// <summary>Дальше порога, за которым рецепт прощает первый срез (<see cref="VideoNodes.FarMs"/>).</summary>
    public bool Far => PingMs > VideoNodes.FarMs;

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

    /// <summary>
    /// То же с числами для разбора: задержка, время каждой попытки, потолок с малым окном.
    /// </summary>
    public string Detail()
    {
        if (!Measured)
            return Describe();

        var filter = Filter switch
        {
            NodeFilter.Open => "; мимо обхода отвечает любому имени",
            NodeFilter.GoogleOnly => "; мимо обхода — только имени Google",
            NodeFilter.Closed => "; мимо обхода не отвечает ни одному имени",
            _ => string.Empty,
        };

        return $"{Short} ({Address}) — пинг {(PingMs is { } ping ? $"{ping} мс" : "не измерен")}"
            + $"; TCP {Tcp} из {Tries}{Times(TcpMs)}"
            + (Lagged > 0 ? $", с задержкой {Lagged}" : string.Empty)
            + (QuicTries > 0 ? $"; QUIC {Quic} из {QuicTries}{Times(QuicMs)}" : "; QUIC не мерился")
            + filter
            + (!Cache && CeilingMbit is { } ceiling ? $"; с малым окном до ~{ceiling:0} Мбит/с" : string.Empty);
    }

    private static string Times(IReadOnlyList<int?> times) => times.Count == 0
        ? string.Empty
        : ": " + string.Join(", ", times.Select(ms => ms is { } took ? took.ToString() : "нет")) + " мс";
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

    /// <summary>Узел кэша провайдера, с которого плеер начинает; <c>null</c> — не определён.</summary>
    public VideoNode? CacheNode { get; init; }

    /// <summary>Почему узел кэша не определён.</summary>
    public string? CacheNote { get; init; }

    /// <summary>Пресет десинка и стоит ли в нём рецепт узлов видео — словами; <c>null</c> — не выяснялось.</summary>
    public string? Recipe { get; init; }

    /// <summary>
    /// Всё, что нужно для разбора жалобы на YouTube: пресет, кэш провайдера,
    /// узлы с задержкой и временем каждой попытки, сводка.
    /// </summary>
    /// <remarks>
    /// По одному итогу «TCP 26 из 30» не понять, какой у человека случай
    /// (разбор 11.11): идёт ли плеер по QUIC или по TCP, близко узлы или
    /// далеко (от этого зависит цена малого окна), отвечает ли сам кэш
    /// провайдера. Отчёт несёт это числами, чтобы не переспрашивать.
    /// </remarks>
    public IReadOnlyList<string> Details
    {
        get
        {
            var lines = new List<string>();

            if (Recipe is not null)
                lines.Add(Recipe);

            // Площадка Google вместо кэша провайдера — главное, что надо знать
            // о такой сети: всё видео, и популярное тоже, идёт с зарубежных
            // узлов под фильтром (11.11: Тула, Ростелеком — fra16s69; СПб,
            // эТелеком — arn09s18; у обоих «не грузится вообще»).
            if (CacheNode is { GoogleSite: true } site)
            {
                lines.Add("Кэша Google у провайдера нет: YouTube приписал эту сеть к своей площадке "
                    + VideoNodes.SiteName(site.Short.Replace("rr1---", string.Empty))
                    + " — всё видео, и популярное тоже, идёт с зарубежных узлов, как редкие ролики.");
                lines.Add("  " + site.Detail());
            }
            else
            {
                lines.Add(CacheNode is { } cache
                    ? "Кэш провайдера: " + cache.Detail()
                    : "Кэш провайдера: " + (CacheNote ?? "не определён"));
            }

            lines.Add(string.Empty);
            lines.Add("Зарубежные узлы (время попыток — от SYN до ответа сервера):");
            lines.AddRange(Nodes.Select(n => "  " + n.Detail()));

            var measured = Measured;

            if (measured.Count == 0)
                return lines;

            lines.Add(string.Empty);
            lines.Add("Сводка:");

            foreach (var (name, group) in new[]
                     {
                         ($"ближние (пинг до {VideoNodes.FarMs} мс)", measured.Where(n => n.PingMs is not null && !n.Far).ToList()),
                         ($"дальние (пинг больше {VideoNodes.FarMs} мс)", measured.Where(n => n.Far).ToList()),
                     })
            {
                if (group.Count == 0)
                    continue;

                int low = group.Min(n => n.PingMs!.Value), high = group.Max(n => n.PingMs!.Value);

                static string Span(double from, double to) => $"{from:0}" == $"{to:0}" ? $"{from:0}" : $"{from:0}–{to:0}";
                int quicTries = group.Sum(n => n.QuicTries);

                lines.Add($"  {name}: узлов {group.Count}, пинг {(low == high ? $"{low}" : $"{low}–{high}")} мс"
                    + $"; TCP {group.Sum(n => n.Tcp)} из {group.Sum(n => n.Tries)}"
                    + (quicTries > 0 ? $"; QUIC {group.Sum(n => n.Quic)} из {quicTries}" : string.Empty)
                    + $"; соединение с малым окном — до ~{Span(group.Min(n => n.CeilingMbit!.Value), group.Max(n => n.CeilingMbit!.Value))} Мбит/с");
            }

            int unreached = measured.Count(n => n.PingMs is null);

            if (unreached > 0)
                lines.Add($"  не соединились ни разу (пинг не измерен): узлов {unreached}");

            int lagged = measured.Sum(n => n.Lagged);

            lines.Add($"  ответов по TCP с задержкой от {VideoNodes.LagMs} мс сверх обычного: {lagged} из {Tcp}"
                + (lagged > 0 ? " — малое окно рецепта либо медленный ответ узла" : string.Empty));

            if (QuicTries > 0)
            {
                int silent = measured.Count(n => n.QuicTries > 0 && n.Quic == 0);

                // Плеер сначала идёт по QUIC (11.11 дома редкие ролики и 4K шли
                // с зарубежных узлов по h3, рецепт TCP в деле не участвовал).
                lines.Add(silent == 0
                    ? "  плеер сначала пробует QUIC: он отвечает у всех узлов — видео, скорее всего, пойдёт по QUIC, без малого окна"
                    : $"  плеер сначала пробует QUIC: у {silent} из {measured.Count} узлов он не прошёл ни разу — с них видео пойдёт по TCP, после ожидания QUIC");
            }
            else
            {
                lines.Add("  QUIC на этой машине не мерился — как пойдёт плеер, по нему не сказать");
            }

            lines.Add("  скорость самого видео не мерилась: ссылку на поток выдаёт только плеер; потолок выше — расчёт (64 КБ окна на пинг)");

            return lines;
        }
    }
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

    /// <summary>Задержка, выше которой узел — «дальний»: порог <c>far</c> рецепта в пресете YT FIX.</summary>
    /// <remarks>
    /// Дома 11.11 задержка до узлов двух родов: 25–31 мс и 50–61 мс. Дальним
    /// малое окно режет скорость до ~10 Мбит/с, и рецепт прощает им первый срез.
    /// </remarks>
    public const int FarMs = 35;

    /// <summary>На сколько ответ должен опоздать против двух задержек, чтобы счесться задержанным.</summary>
    /// <remarks>Малое окно прибавляет 270–340 мс (замер 10.11), обычный разброс — десятки.</remarks>
    public const int LagMs = 200;

    /// <summary>Откуда узнаётся узел кэша провайдера: Google отвечает именем узла, к которому приписан адрес клиента.</summary>
    private const string MappingUrl = "https://redirector.googlevideo.com/report_mapping?di=no";

    /// <summary>
    /// Имя площадки самого Google: код аэропорта, номер, «s» и номер — <c>fra16s69</c>,
    /// <c>arn09s18</c>. У кэша провайдера имя своё, с дефисом: <c>sevensky-vko2</c>.
    /// </summary>
    public static bool IsGoogleSite(string name) =>
        System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z]{3}[0-9]{2}s[0-9]{1,3}$");

    /// <summary>Площадка с городом, когда код аэропорта знаком: «fra16s69 (Франкфурт)».</summary>
    public static string SiteName(string name) =>
        name.Length >= 3 && Cities.TryGetValue(name[..3], out var city) ? $"{name} ({city})" : name;

    /// <summary>Города площадок Google, куда приписывают сети в России и рядом.</summary>
    private static readonly Dictionary<string, string> Cities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fra"] = "Франкфурт", ["arn"] = "Стокгольм", ["ams"] = "Амстердам", ["hel"] = "Хельсинки",
        ["waw"] = "Варшава", ["prg"] = "Прага", ["vie"] = "Вена", ["bud"] = "Будапешт",
        ["muc"] = "Мюнхен", ["zrh"] = "Цюрих", ["mil"] = "Милан", ["lhr"] = "Лондон",
        ["par"] = "Париж", ["mad"] = "Мадрид", ["otp"] = "Бухарест", ["sof"] = "София",
        ["beg"] = "Белград", ["ber"] = "Берлин", ["ham"] = "Гамбург", ["dus"] = "Дюссельдорф",
        ["cph"] = "Копенгаген", ["osl"] = "Осло", ["tll"] = "Таллин", ["rix"] = "Рига",
        ["ist"] = "Стамбул", ["led"] = "Санкт-Петербург", ["mow"] = "Москва", ["svo"] = "Москва",
    };

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

        var cache = CacheAsync(aside, cancellationToken);
        var nodes = await Task.WhenAll(Hosts.Select(host => MeasureNodeAsync(host, aside, cancellationToken)));
        var (cacheNode, cacheNote) = await cache;

        return new VideoNodesReport { Nodes = nodes, Desync = desync, CacheNode = cacheNode, CacheNote = cacheNote };
    }

    /// <summary>
    /// Узел кэша провайдера — тот, с которого плеер начинает, — и его замер.
    /// </summary>
    /// <remarks>
    /// Из ответа берётся только имя узла: в нём же стоит внешний адрес клиента,
    /// и в отчёт он не попадает. Мимо обхода узел кэша не спрашивается — фильтра
    /// на нём нет, он у провайдера.
    /// </remarks>
    private static async Task<(VideoNode? Node, string? Note)> CacheAsync(bool aside, CancellationToken cancellationToken)
    {
        string name;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var text = await http.GetStringAsync(MappingUrl, cancellationToken);
            var match = System.Text.RegularExpressions.Regex.Match(text, @"=>\s*([a-z0-9-]+)");

            if (!match.Success)
                return (null, "не определён — Google не назвал узел");

            name = match.Groups[1].Value;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (null, "не определён — redirector.googlevideo.com не ответил");
        }

        var node = (await MeasureNodeAsync($"rr1---{name}{Zone}", aside, cancellationToken, filter: false))
            with { Cache = true, GoogleSite = IsGoogleSite(name) };

        return node.Tunnelled
            ? (null, $"{name} — YouTube заведён в туннель, это кэш у выхода; не мерился")
            : (node, null);
    }

    /// <summary>Задержка до адреса: наименьшее из трёх времён установки TCP; <c>null</c> — не соединился.</summary>
    /// <remarks>
    /// Мимо обхода, когда можно: новое соединение через winws2 стоит ему 10–30 мс
    /// процессора (замер 03.10 и 07.10), а узлы спрашиваются разом — первый прогон
    /// 11.11 через обход показал 38–40 мс там, где curl по одному давал 27–28.
    /// </remarks>
    private static async Task<int?> PingAsync(IPAddress address, bool aside, CancellationToken cancellationToken)
    {
        using var ports = aside ? ProbePorts.Use() : null;

        int? best = null;

        for (int i = 0; i < Tries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var client = ProbePorts.Client(AddressFamily.InterNetwork);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(Timeout);

            var watch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await client.ConnectAsync(address, 443, limit.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            int took = Math.Max(1, (int)watch.ElapsedMilliseconds);

            if (best is null || took < best)
                best = took;
        }

        return best;
    }

    private static async Task<VideoNode> MeasureNodeAsync(string host, bool aside, CancellationToken cancellationToken, bool filter = true)
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

        // Задержка — первой и голым соединением: приветствия за ней могут
        // пометить группу, и следующие пойдут с малым окном.
        int? ping = await PingAsync(address, aside, cancellationToken);

        int tcp = 0;
        var tcpMs = new List<int?>();

        for (int i = 0; i < Tries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Браузерным приветствием: его и увидит фильтр от Хрома — длинное,
            // двумя пакетами. Ответ — ServerHello, а не отказ: узел своё имя знает.
            var hello = await BrowserHello.AskAsync(address, host, Timeout, cancellationToken);
            bool accepted = hello.Answer == HelloAnswer.Accepted;

            if (accepted)
                tcp++;

            tcpMs.Add(accepted ? (int)hello.Elapsed.TotalMilliseconds : null);
        }

        int quic = 0, quicTries = 0;
        var quicMs = new List<int?>();

        for (int i = 0; i < Tries; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // null — QUIC на этой машине нет, и спрашивать дальше незачем.
            if (await QuicProbe.AskAsync(address, host, Timeout, cancellationToken) is not { } answer)
                break;

            quicTries++;

            if (answer.Ok)
                quic++;

            quicMs.Add(answer.Ok ? (int)answer.Elapsed.TotalMilliseconds : null);
        }

        return new VideoNode
        {
            Host = host,
            Address = address.ToString(),
            Tries = Tries,
            Tcp = tcp,
            QuicTries = quicTries,
            Quic = quic,
            PingMs = ping,
            TcpMs = tcpMs,
            QuicMs = quicMs,
            Filter = aside && filter ? await FilterAsync(address, cancellationToken) : NodeFilter.Unknown,
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
