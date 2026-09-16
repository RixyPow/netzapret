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
    /// Имя заведено в туннель, и туннель до него не дотянулся.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отличается от <see cref="TlsDpi"/> тем, кого мы измерили. Внутрь
    /// туннеля DPI не заглядывает — он видит шифрованный поток к серверу
    /// подписки и не знает, какое имя внутри. Значит рукопожатие оборвал
    /// не он, а сам туннель: сервер не дотянулся до хоста, отказал по своим
    /// причинам либо вышел там, где хост закрыт.
    /// </para>
    /// <para>
    /// Прежде такие цели получали «DPI по TLS», и в отчёте выходило шесть
    /// таких строк из девяти. Совет к ним прилагался про десинк — то есть
    /// про вмешательство в рукопожатие, которого DPI и не видел. Лечится
    /// это другим: сменой сервера, а не рецепта.
    /// </para>
    /// </remarks>
    TunnelFailed,

    /// <summary>
    /// Рукопожатие проходит, но поток обрывается на первых килобайтах.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Найдено разбором живого случая: <c>steamcommunity.com</c> перестал
    /// открываться, при том что проверка называла его доступным. Рукопожатие
    /// и вправду проходило — десинк своё дело делал, — а дальше приходило
    /// ровно 14381 байт, три раза из трёх, и поток замирал навсегда.
    /// </para>
    /// <para>
    /// Сперва я счёл это неизлечимым десинком: секция пресета помечена
    /// <c>--payload=tls_client_hello</c>, то есть трогает только рукопожатие,
    /// а обрыв случается много позже. Вывод оказался неверен, и опровергнут
    /// он замером: <c>skinsrestorer.net</c> рвался на 13 505 байтах из 154 921
    /// и вылечился именно десинком — рецептом на ClientHello.
    /// </para>
    /// <para>
    /// Объясняется тем, что DPI разбирает соединение на рукопожатии,
    /// а убивает поток позже. Сорвав опознание в начале, отменяешь и то,
    /// что должно было случиться потом. Поэтому сначала другой рецепт,
    /// и только если не помог — туннель.
    /// </para>
    /// </remarks>
    Stall,

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

    /// <summary>
    /// Системный резолвер имя не разрешает, а честный — разрешает.
    /// </summary>
    /// <remarks>
    /// Названо по тому, что измерено. Подменой это звать нельзя: снаружи
    /// не отличить подставленный ответ от честного, до которого не дошёл
    /// запрос. Лечится и то и другое одинаково — сменой резолвера.
    /// </remarks>
    Dns,

    /// <summary>
    /// Имя разрешается в заглушку: петлю или нулевой адрес.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Найдено на <c>seriesgraph.com</c>, у которого не грузились картинки.
    /// <c>image.tmdb.org</c> — это CNAME на <c>tmdb-image-prod.b-cdn.net</c>,
    /// и адресом оттуда приходит <c>127.0.0.1</c>: так сеть доставки
    /// отшивает регион.
    /// </para>
    /// <para>
    /// Здесь стояло, что петлю отдают все резолверы без исключения. Замер
    /// 2026-08-30 это опроверг: Google отвечает петлёй, а Cloudflare —
    /// настоящим адресом шарда, и тот работает. Сертификат выписан на
    /// <c>image.tmdb.org</c>, запрос постера возвращает 200. Значит отказ
    /// зависит от того, кого спрашивают, и подстановка адреса лечит его
    /// без всякого туннеля — см. <c>config\addresses.yaml</c>.
    /// </para>
    /// <para>
    /// Само по себе разрешение имени в петлю маршрутом по-прежнему
    /// не лечится: направлять её некуда. Лечится либо подстановкой адреса,
    /// либо туннелем — проксируемым именам выдаётся fakeip, а настоящее
    /// разрешение происходит на выходе, где спрашивают уже не из России.
    /// </para>
    /// </remarks>
    Sinkhole,

    /// <summary>
    /// Всё работает, но сайт отказывает по стране.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отказ не от оператора, а от самого сайта, и потому неотличим от успеха
    /// всеми прежними мерками: TCP встаёт, рукопожатие проходит, данные идут.
    /// Идёт при этом страница «нам жаль, но не в вашей стране».
    /// </para>
    /// <para>
    /// Найдено на ChatGPT. Десинк отрабатывал безупречно — рукопожатие
    /// за семь сотых секунды, — а приходил 403 от Cloudflare с московского
    /// узла. Проверка называла это «доступен», и по её же меркам была права.
    /// </para>
    /// <para>
    /// Десинку тут делать нечего: он влияет на то, как выглядит соединение
    /// для DPI, а решение принимает сайт, уже разобрав запрос. Помогает
    /// только выход в другой стране.
    /// </para>
    /// </remarks>
    GeoBlock,

    /// <summary>
    /// Имя ведёт на другое имя, а у того адреса нет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Поломка, а не свойство. <c>tr.rbxcdn.com</c> — CNAME на
    /// <c>trns1.rbxcdn.com</c>, у которого записи A нет ни у одного резолвера.
    /// На это имя ссылается сам Roblox, отдавая по нему ссылки на превью:
    /// картинки не грузятся, и починить это нельзя ничем из обычного набора.
    /// </para>
    /// <para>
    /// Ни десинк, ни туннель тут ни при чём: соединяться попросту не с чем.
    /// Лечится единственным способом — подставить адрес вручную. Тем и
    /// отличается от <see cref="NoAddress"/>, где адреса нет и не нужно.
    /// </para>
    /// </remarks>
    BrokenCname,

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

    /// <summary>
    /// Сторона не согласовала ни одну из версий TLS, которые мы умеем.
    /// </summary>
    /// <remarks>
    /// Не блокировка: отказ пришёл словами и быстро, а фильтр так не отвечает —
    /// он рвёт соединение либо молчит. Встречается у площадок, отключивших
    /// TLS 1.2, и у старых серверов без 1.3. Лечить нечем и не нужно:
    /// настоящий браузер умеет больше версий, чем проверка.
    /// </remarks>
    Handshake,
}

/// <summary>Исход одной пробы.</summary>
public sealed record ProbeOutcome
{
    public required bool Ok { get; init; }

    /// <summary>Соединение оборвано удалённой стороной — признак DPI.</summary>
    public bool Reset { get; init; }

    /// <summary>
    /// Ответ получен, но это отказ самого сайта.
    /// </summary>
    /// <remarks>
    /// Отличается от всех прочих неудач тем, что связь исправна. Отказал
    /// не канал, а собеседник, и лечится это не тем же самым.
    /// </remarks>
    public bool Refused { get; init; }

    /// <summary>
    /// Сторона отказалась от версии протокола, а не оборвала разговор.
    /// </summary>
    /// <remarks>
    /// Различие видно по тому, как именно провалилось рукопожатие. Сервер,
    /// не умеющий TLS 1.2, отвечает отказом согласования — быстро и словами.
    /// Фильтр не отвечает вовсе: он либо рвёт соединение, либо молчит до
    /// истечения срока.
    /// </remarks>
    public bool Unsupported { get; init; }

    public TimeSpan Elapsed { get; init; }

    public string? Detail { get; init; }

    public string Describe() => Ok
        ? "ок"
        : Reset ? "RST"
        : Unsupported ? "н/д"
        : Detail is null ? "—" : "нет";
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

    /// <summary>Идут ли данные после того, как рукопожатие состоялось.</summary>
    public required ProbeOutcome Data { get; init; }

    public required BlockKind Kind { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Замер вправду ушёл в туннель — по адресу, в который разрешилось имя.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Замеренный факт, а не вычитанное из правил намерение. Прежде вердикты
    /// про туннель ставились по ответу движка правил — «этому имени положено
    /// идти через VPN», — и никто не проверял, что так и вышло. Разойтись
    /// им легко: пин в hosts бьёт любой резолв, правило могло быть перекрыто
    /// более ранним, а в ветке с honest DNS проба и вовсе берёт настоящие
    /// адреса от 1.1.1.1 и стучится прямо в них, мимо fakeip.
    /// </para>
    /// <para>
    /// Во всех этих случаях измерялось прямое соединение, а подписывалось
    /// оно «туннель не доставил» — то есть человека отправляли чинить трубу,
    /// которой замер не касался.
    /// </para>
    /// </remarks>
    public bool Tunnelled { get; init; }

    /// <summary>
    /// Правила ведут имя в туннель, а замер прошёл мимо него.
    /// </summary>
    /// <remarks>
    /// Само по себе не поломка: так выглядит имя, прибитое в hosts или
    /// перекрытое более ранним правилом. Но вердикт по такому замеру
    /// описывает прямой путь, а не туннель, и об этом надо сказать —
    /// иначе «закрыт полностью» у проксируемого имени читается как беда
    /// подписки.
    /// </remarks>
    public bool ExpectedTunnel { get; init; }

    public bool TunnelMissed => ExpectedTunnel && !Tunnelled;

    public string Describe() => Kind switch
    {
        BlockKind.None => "доступен",
        BlockKind.TlsDpi => "DPI по TLS",
        BlockKind.TunnelFailed => "туннель не доставил",
        BlockKind.Stall => "обрыв после рукопожатия",
        BlockKind.HttpsPort => "порт 443 закрыт",
        BlockKind.Full => "закрыт полностью",
        BlockKind.Sinkhole => "адрес-заглушка",

        // Одним словом про два случая: имя не разрешилось вовсе либо
        // разрешилось в адрес, который молчит. Разные они только в подробности
        // под строкой; лечатся оба сменой резолвера, и делить их на два
        // вердикта значило бы предлагать одно и то же дважды.
        BlockKind.Dns => "резолвер даёт нерабочий адрес",
        BlockKind.GeoBlock => "сайт отказывает по стране",
        BlockKind.BrokenCname => "оборванный CNAME",
        BlockKind.Handshake => "сторона не даёт наш TLS",
        _ => "нет адреса у имени",
    };

    /// <summary>Нужно ли вообще о нём говорить в итоге.</summary>
    /// <remarks>
    /// Имя без адреса — свойство списка, а не сети: советовать по нему
    /// нечего, и в перечне закрытого оно только сбивает.
    /// </remarks>
    public bool Actionable => Kind is not (BlockKind.None or BlockKind.NoAddress or BlockKind.Handshake);

    /// <summary>
    /// Отчего именно вердикт таков — словами той пробы, что его решила.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Берётся стадия, а не первая непустая деталь. Прежде выбиралась первая,
    /// и у имени с убитым рукопожатием в отчёт попадал рассказ про удавшийся
    /// TCP — то есть про то, что как раз сработало. Читать это было
    /// невозможно: строка объясняла не ту стадию, на которой сломалось.
    /// </para>
    /// <para>
    /// Время добавляется к отказам рукопожатия: полсекунды и полные четыре —
    /// это разные поломки. Быстрый отказ означает ответ, четыре секунды —
    /// тишину, а тишина и есть почерк отбрасывания.
    /// </para>
    /// </remarks>
    public string? Why
    {
        get
        {
            var decisive = Kind switch
            {
                BlockKind.None => null,
                BlockKind.GeoBlock or BlockKind.Stall => Data,
                BlockKind.TlsDpi or BlockKind.Handshake => Worse(Tls13, Tls12),
                BlockKind.HttpsPort or BlockKind.Full or BlockKind.Dns
                    or BlockKind.Sinkhole or BlockKind.BrokenCname => Tcp,
                BlockKind.TunnelFailed => Data.Detail is not null ? Data : Worse(Tls13, Tls12),
                _ => null,
            };

            if (decisive?.Detail is not { Length: > 0 } detail)
                return null;

            // Время приписывается только там, где сторона ответила: обрыв
            // и отказ согласования сами по себе не говорят, быстро это было
            // или на исходе срока, а разница тут и есть весь смысл.
            //
            // К истёкшему ожиданию не приписывается: оно и так названо
            // словами — «нет ответа за 4 с», — и вторая цифра рядом
            // повторяла то же самое: «нет ответа за 4 с, …, 4,0 с».
            return (decisive.Reset || decisive.Unsupported) && decisive.Elapsed > TimeSpan.Zero
                ? $"{detail}, {decisive.Elapsed.TotalSeconds:0.0} с"
                : detail;
        }
    }

    /// <summary>Из двух проб та, что сказала больше.</summary>
    private static ProbeOutcome Worse(ProbeOutcome a, ProbeOutcome b) =>
        a.Detail is { Length: > 0 } ? a : b;

    /// <summary>
    /// Мог ли этот вердикт возникнуть от тесноты, а не от блокировки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Различие измеримое, и оно измерено: <c>whatsapp.net</c> в прогоне
    /// на семьдесят одну цель выходил «закрыт полностью», а он же в прогоне
    /// на четыре — «доступен». Сеть та же, имя то же, разница только
    /// в том, сколько проб шло рядом.
    /// </para>
    /// <para>
    /// Сюда попадают вердикты, которые ставятся по неполученному ответу:
    /// не встало соединение, не прошло рукопожатие, замер поток. Все они
    /// неотличимы от «нам не хватило полосы». А вот заглушка в DNS,
    /// оборванный CNAME и отказ по стране получены по ответу, который пришёл,
    /// — теснота такого не подделает, и перепроверять их незачем.
    /// </para>
    /// </remarks>
    public bool MayBeCrowding => Kind is BlockKind.Full or BlockKind.HttpsPort
        or BlockKind.TlsDpi or BlockKind.Stall or BlockKind.TunnelFailed;

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
        BlockKind.TunnelFailed => "другой сервер подписки — десинк тут ни при чём",
        BlockKind.Stall => "другой рецепт десинка, иначе VPN",
        BlockKind.HttpsPort => "только VPN",
        BlockKind.Full => "только VPN",
        BlockKind.Sinkhole => "только VPN",
        BlockKind.Dns => "свой DNS",
        BlockKind.GeoBlock => "только VPN — десинк не поможет",
        BlockKind.BrokenCname => "подставить адрес",
        BlockKind.Handshake => "ничего не нужно — браузер умеет больше версий",
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
    /// <summary>
    /// Сколько ждать ответа на одну пробу.
    /// </summary>
    /// <remarks>
    /// Было шесть секунд, стало четыре. Проверка идёт по сорока с лишним
    /// целям, каждая неудача повторяется, и на каждой лишней секунде набегает
    /// больше минуты общего ожидания. Четырёх достаточно: закрытое молчит
    /// вовсе, а открытое отвечает за десятые доли.
    /// </remarks>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Сколько ждать очередной порции данных, прежде чем счесть поток замершим.
    /// </summary>
    /// <remarks>
    /// Отсчёт идёт от последнего полученного байта, а не от начала. Медленная
    /// страница успевает подавать признаки жизни и проверку проходит; убитый
    /// поток замолкает совсем, и это видно за три секунды.
    /// </remarks>
    private static readonly TimeSpan StallWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Сколько данных считать достаточным доказательством, что поток идёт.
    /// </summary>
    /// <remarks>
    /// Больше качать незачем: если четверть мегабайта прошла, дело не в сети.
    /// Порог заодно ограничивает трафик самой проверки — она ходит по всему
    /// справочнику, и тянуть страницы целиком было бы расточительно.
    /// </remarks>
    private const int EnoughBytes = 256 * 1024;

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
    /// <param name="throughTunnel">
    /// Имя заведено в туннель — тогда вердикты про DPI к нему неприменимы.
    /// </param>
    public static async Task<TargetReport> CheckAsync(
        string host,
        string? service,
        CancellationToken cancellationToken,
        bool throughTunnel = false)
    {
        var first = await ProbeAsync(host, service, cancellationToken, throughTunnel);

        if (first.Kind == BlockKind.None)
            return first;

        // Прерывание проверяется здесь, а не только внутри проб. Первый заход
        // уже провалился, значит второй пойдёт по всем стадиям с их таймаутами,
        // и прерывание, поданное между заходами, ждало бы их все. В окне это
        // выглядело так, будто «Прервать» не работает вовсе.
        cancellationToken.ThrowIfCancellationRequested();

        // Второй заход только для неудач: подтверждать успех незачем,
        // а вот обвинять с одного раза нельзя.
        var second = await ProbeAsync(host, service, cancellationToken, throughTunnel);

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

    /// <summary>
    /// Одна проба без повтора — для перепроверки в тишине.
    /// </summary>
    /// <remarks>
    /// Повтор внутри <see cref="CheckAsync"/> здесь был бы лишним: тот, кто
    /// зовёт этот метод, сам и есть повтор. Двойная проба удвоила бы ожидание
    /// ровно там, где его и так больше всего — на именах, которые молчат.
    /// </remarks>
    public static Task<TargetReport> ProbeOnceAsync(
        string host,
        string? service,
        CancellationToken cancellationToken,
        bool throughTunnel = false) =>
        ProbeAsync(host, service, cancellationToken, throughTunnel);

    /// <summary>
    /// Сводит вердикт из общего прогона с вердиктом из одиночного.
    /// </summary>
    /// <remarks>
    /// Одиночному верим больше, но только когда он говорит о лучшем.
    /// Ухудшение при повторе — это разовая неудача второй пробы, и повышать
    /// по ней приговор значило бы обвинять по слабейшему из двух замеров.
    /// </remarks>
    public static TargetReport Reconcile(TargetReport crowded, TargetReport alone) =>
        alone.Kind == BlockKind.None || (crowded.Kind == BlockKind.Full && alone.Kind != BlockKind.Full)
            ? alone
            : crowded;

    /// <summary>
    /// Меряет имя нарочно через туннель, чем бы его ни вели правила.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отвечает на вопрос, которого прямая проба не задаёт: «поможет ли этому
    /// имени VPN». Прежде он решался догадкой — лесенка в советах предлагала
    /// ступень по виду блокировки, — а теперь его можно померить.
    /// </para>
    /// <para>
    /// Стадии те же и тем же кодом: разойдись прямой и туннельный замеры
    /// реализацией, сравнивать их было бы нельзя. Разница ровно одна —
    /// соединение открывает служебный вход, и потому <see cref="TargetReport
    /// .Tunnelled"/> здесь не вычисляется, а known: мы сами туда и отправили.
    /// </para>
    /// <para>
    /// Адресов в отчёте нет намеренно. Имя разрешает сам движок на своей
    /// стороне, и назвать адрес мы не можем — а выдумать его значило бы
    /// показать не тот, по которому шёл замер.
    /// </para>
    /// </remarks>
    /// <param name="port">Порт служебного входа <c>health-in</c>.</param>
    public static async Task<TargetReport> ProbeThroughTunnelAsync(
        string host,
        string? service,
        int port,
        CancellationToken cancellationToken)
    {
        var tcp = await TunnelTcpAsync(host, port, cancellationToken);

        var tls12 = tcp.Ok
            ? await TlsAsync(host, SslProtocols.Tls12, cancellationToken, through: port)
            : Failed(null);

        var tls13 = tcp.Ok
            ? await TlsAsync(host, SslProtocols.Tls13, cancellationToken, through: port)
            : Failed(null);

        var data = tls12.Ok || tls13.Ok
            ? await TransferAsync(host, cancellationToken, through: port)
            : Failed(null);

        return new TargetReport
        {
            Host = host,
            Service = service,
            Tcp = tcp,
            Tls12 = tls12,
            Tls13 = tls13,

            // Восьмидесятый через туннель не спрашиваем: он различает закрытый
            // маршрут и вмешательство в рукопожатие, а внутри туннеля ни то,
            // ни другое от нашего оператора не зависит.
            Http = Failed(null),
            Data = data,
            Kind = Classify(tcp, tls12, tls13, Failed(null), data, throughTunnel: true),
            Tunnelled = true,
            ExpectedTunnel = true,
        };
    }

    /// <summary>Открылось ли соединение до хоста через служебный вход.</summary>
    private static async Task<ProbeOutcome> TunnelTcpAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = await TunnelProbe.ConnectAsync(port, host, 443, Timeout, cancellationToken);

            return new ProbeOutcome { Ok = true, Elapsed = stopwatch.Elapsed };
        }
        catch (Exception ex)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Reset = IsReset(ex),
                Elapsed = stopwatch.Elapsed,
                Detail = ex is IOException
                    ? ex.Message
                    : "служебный вход не отозвался — движки запущены без него",
            };
        }
    }

    private static async Task<TargetReport> ProbeAsync(
        string host,
        string? service,
        CancellationToken cancellationToken,
        bool throughTunnel)
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
            // Спрашиваем у DoH, прежде чем объявить, что адреса нет. Иначе
            // два разных случая сливаются в один: у akamai.net записи A нет
            // ни у кого, и лечить там нечего, а вот имя, которое честный
            // резолвер разрешает, а системный нет, — это отказ, и решается
            // он сменой резолвера.
            var honest = await DohResolveAsync(host, cancellationToken);

            if (honest.Count > 0)
            {
                return new TargetReport
                {
                    Host = host,
                    Service = service,
                    Tcp = Failed("системный DNS не разрешает имя"),
                    Tls12 = Failed(null),
                    Tls13 = Failed(null),
                    Http = Failed(null),
                    Data = Failed(null),
                    Kind = BlockKind.Dns,
                    Addresses = honest.Take(3).ToList(),

                    // Имя не разрешилось вовсе — соединения не было, и путь
                    // ему взяться неоткуда. Правило при этом называется:
                    // по нему видно, чего от имени ждали.
                    Tunnelled = false,
                    ExpectedTunnel = throughTunnel,
                };
            }

            // Адреса нет ни у кого — и вот тут решает CNAME. Если имя ведёт
            // на другое, значит на него ссылаются и ждут ответа, а цепочка
            // оборвана: это поломка. Если же не ведёт никуда, перед нами
            // просто маркер зоны вроде akamai.net, и лечить нечего.
            var alias = await DohCanonicalNameAsync(host, cancellationToken);

            return new TargetReport
            {
                Host = host,
                Service = service,
                Tcp = Failed(alias is null
                    ? "записи A нет"
                    : $"ведёт на {alias}, а у того адреса нет"),
                Tls12 = Failed(null),
                Tls13 = Failed(null),
                Http = Failed(null),
                Data = Failed(null),
                Kind = alias is null ? BlockKind.NoAddress : BlockKind.BrokenCname,
                Tunnelled = false,
                ExpectedTunnel = throughTunnel,
            };
        }

        // Заглушка распознаётся до всяких проб: стучаться в 127.0.0.1 и потом
        // объявлять хост закрытым — значит назвать чужой отказ своим.
        var real = addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();

        if (real.All(IsStub))
        {
            return new TargetReport
            {
                Host = host,
                Service = service,
                Tcp = Failed($"адрес-заглушка {real[0]}"),
                Tls12 = Failed(null),
                Tls13 = Failed(null),
                Http = Failed(null),
                Data = Failed(null),
                Kind = BlockKind.Sinkhole,
                Addresses = real.Select(a => a.ToString()).Take(3).ToList(),

                // Заглушка — это настоящий адрес, пусть и негодный: fakeip
                // он не является, и соединение по нему пошло бы напрямую.
                Tunnelled = false,
                ExpectedTunnel = throughTunnel,
            };
        }

        // Через туннель или мимо — решает адрес, в который разрешилось имя,
        // а не правило. Fakeip выдаётся только тем именам, которые движок
        // заводит в туннель, и это единственный признак, видный снаружи:
        // настоящий адрес означает, что соединение пойдёт напрямую, какое бы
        // правило на имя ни стояло.
        //
        // Прежде сюда приходило намерение из правил, и всякий раз, когда
        // они расходились с делом — пин в hosts, перекрытое правило, честный
        // резолвер вместо fakeip, — прямое соединение получало вердикт
        // «туннель не доставил».
        bool viaTunnel = real.Any(a => TunnelHealth.IsFakeIp(a));

        var (answered, tcp) = await ConnectAnyAsync(
            host, 443, AddressFamily.InterNetwork, real, cancellationToken);

        // Пробы идут последовательно, а не разом: одновременные соединения
        // к одному хосту DPI иногда обрывает скопом, и картина смазывается.
        //
        // Все — на тот адрес, который ответил на TCP. Иначе каждая заново
        // тянет жребий среди адресов имени, и закрытый среди них сделает
        // из «маршрут до одного адреса закрыт» вывод «рукопожатие рвут».
        var tls12 = tcp.Ok ? await TlsAsync(host, SslProtocols.Tls12, cancellationToken, real, answered) : Failed(null);
        var tls13 = tcp.Ok ? await TlsAsync(host, SslProtocols.Tls13, cancellationToken, real, answered) : Failed(null);

        // Восьмидесятый порт спрашивается только тогда, когда не встал 443,
        // и лишь затем, чтобы отличить закрытый маршрут от вмешательства
        // в рукопожатие. Больше он не значит ничего: разбор вердикта смотрит
        // на него ровно в одной ветке — той, где TCP на 443 не поднялся.
        //
        // Прежде его спрашивали всегда, и это стоило дважды. Во времени:
        // современные сайты на восьмидесятом не отвечают, и каждая цель
        // с закрытым портом съедала полный срок ожидания — четыре секунды,
        // а с повтором восемь, и так на сотне с лишним имён.
        //
        // И в понятности, что хуже. В строке стояло «HTTP: RST» рядом
        // с вердиктом «доступен», и это читалось как противоречие самому
        // себе, хотя было правдой: сайт жив, просто незашифрованный порт
        // у него закрыт — как и положено.
        var http = tcp.Ok ? Failed(null) : await HttpAsync(host, cancellationToken, real, answered);

        // Передача проверяется только там, где рукопожатие состоялось: без него
        // качать нечего, а вопрос «идут ли данные» имеет смысл ровно тогда,
        // когда соединение с виду установлено.
        var data = tls12.Ok || tls13.Ok
            ? await TransferAsync(host, cancellationToken, real, answered)
            : Failed(null);

        // Разбор идёт по замеренному пути, а не по обещанному правилами.
        var kind = Classify(tcp, tls12, tls13, http, data, viaTunnel);
        var shown = real.Select(a => a.ToString()).Take(3).ToList();

        // Маркер зоны, спрятанный за fakeip.
        //
        // Имя без записи A — akamai.net, scdn.co, spotifycdn.com,
        // whatsapp.com — напрямую опознаётся сразу: адреса нет, проверять
        // нечего, строка в таблицу не идёт. Но заведённому в туннель
        // движок выдаёт fakeip, то есть адрес появляется, и опознание
        // не срабатывает вовсе.
        //
        // Дальше всё выглядит хостом, который не отвечает: TUN принимает
        // всякое соединение мгновенно, поэтому TCP «ок», а затем движок
        // не находит, к кому идти, и закрывает. В отчёте это выходило
        // «туннель не доставил» — обвинение трубе за имя, которого нет.
        //
        // Замер 2026-09-16: у scdn.co и spotifycdn.com записи A нет
        // ни у одного резолвера, whatsapp.com не существует вовсе,
        // а i.scdn.co отвечает двумя адресами. Три из четырёх строк
        // «туннель не доставил» в отчёте 21:59 были маркерами зон.
        //
        // Спрашивается только у провалившихся: на каждое проксируемое имя
        // это был бы лишний запрос к DoH, а их два десятка.
        if (viaTunnel && kind == BlockKind.TunnelFailed)
        {
            var exists = await DohResolveAsync(host, cancellationToken);

            if (exists.Count == 0)
            {
                var missing = await DohCanonicalNameAsync(host, cancellationToken);

                return new TargetReport
                {
                    Host = host,
                    Service = service,
                    Tcp = Failed(missing is null
                        ? "записи A нет ни у кого — имя покрывает зону, а не хост"
                        : $"ведёт на {missing}, а у того адреса нет"),
                    Tls12 = Failed(null),
                    Tls13 = Failed(null),
                    Http = Failed(null),
                    Data = Failed(null),
                    Kind = missing is null ? BlockKind.NoAddress : BlockKind.BrokenCname,
                    Tunnelled = viaTunnel,
                    ExpectedTunnel = throughTunnel,
                };
            }
        }

        // «Не ответило ничего» бывает двух видов, и лечатся они разным.
        // Закрыт может быть маршрут до хоста — тогда нужен туннель. А может
        // не отвечать ровно тот адрес, который выдал резолвер, тогда как
        // соседний живёт: у этого пользователя все три имени WhatsApp
        // системный DNS разрешал в 157.240.205.60, куда соединение не встаёт,
        // а 1.1.1.1 давал 57.144.249.32, который отвечает за десятые доли.
        // Первое лечится VPN, второе — сменой резолвера, и путать их дорого.
        if (kind == BlockKind.Full && !viaTunnel)
        {
            var honest = await DohResolveAsync(host, cancellationToken);

            // Сравнивается весь набор системного резолвера, а не первые три
            // из него. Обрезанный список давал ложное «резолвер виноват» там,
            // где резолверы отвечают одинаково: у updates.discord.com пять
            // адресов, и четвёртый честного набора неизбежно «отсутствовал»
            // в показанной тройке.
            var mine = real.Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
            var other = honest.FirstOrDefault(a => !mine.Contains(a));

            if (other is not null && await AnswersAsync(other, cancellationToken))
            {
                return new TargetReport
                {
                    Host = host,
                    Service = service,
                    Tcp = Failed($"{real[0]} молчит, а {other} от честного резолвера отвечает"),
                    Tls12 = tls12,
                    Tls13 = tls13,
                    Http = http,
                    Data = data,
                    Kind = BlockKind.Dns,
                    Addresses = honest.Take(3).ToList(),

                    // Адреса взяты у честного резолвера, мимо fakeip, —
                    // значит замер заведомо шёл не через туннель, что бы
                    // ни говорили правила.
                    Tunnelled = false,
                    ExpectedTunnel = throughTunnel,
                };
            }
        }

        return new TargetReport
        {
            Host = host,
            Service = service,
            Tcp = tcp,
            Tls12 = tls12,
            Tls13 = tls13,
            Http = http,
            Data = data,
            Kind = kind,
            Addresses = shown,
            Tunnelled = viaTunnel,
            ExpectedTunnel = throughTunnel,
        };
    }

    /// <summary>Встаёт ли соединение на 443 к конкретному адресу.</summary>
    /// <remarks>
    /// По адресу, а не по имени: весь вопрос в том, что имя разрешается
    /// не туда, и спрашивать снова через тот же резолвер бессмысленно.
    /// </remarks>
    private static async Task<bool> AnswersAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            await client.ConnectAsync(IPAddress.Parse(address), 443, timeout.Token);

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Ставит диагноз по совокупности проб.
    /// </summary>
    /// <remarks>
    /// Порядок проверок отражает то, насколько уверенно каждый признак говорит
    /// о вмешательстве. Живой TCP при мёртвом TLS — самый ясный след: до хоста
    /// достучались, а разговор оборвали, разобрав имя в рукопожатии.
    /// </remarks>
    /// <param name="throughTunnel">
    /// Имя заведено в туннель. Тогда вердикты про DPI неприменимы: внутрь
    /// туннеля он не заглядывает, и оборвать рукопожатие по имени не может.
    /// </param>
    internal static BlockKind Classify(
        ProbeOutcome tcp,
        ProbeOutcome tls12,
        ProbeOutcome tls13,
        ProbeOutcome http,
        ProbeOutcome data,
        bool throughTunnel = false)
    {
        var kind = Decide(tcp, tls12, tls13, http, data);

        // Переименование, а не другой разбор: измерения те же, а вот кто
        // за ними стоит — другой. Виды, говорящие о вмешательстве в наше
        // рукопожатие, через туннель означают лишь, что туннель не довёз.
        //
        // Отказ согласования сюда НЕ входит, и это отмена прежнего решения.
        // Оно было принято из соображения, что закрывшийся выход подписки
        // выглядит так же, как сайт, отвергнувший версию, — и соображение
        // оказалось неверным. Замер 2026-09-16 на четырёх видах поведения
        // собеседника (принял и закрыл; подождал и закрыл; оборвал с RST;
        // молчит) даёт IOException либо отмену по сроку, но ни разу —
        // AuthenticationException без сокетной причины, а только она
        // считается отказом согласования.
        //
        // Обратное же верно всегда: отказ согласования — это ответ, пришедший
        // словами. Чтобы он дошёл, туннель должен был доставить и запрос,
        // и ответ. Называть доставку недоставкой — прямое противоречие,
        // и оно стояло в отчёте двумя строками подряд: вердикт «туннель
        // не доставил» и причина «сторона не согласовала TLS 1.3 — это её
        // настройка, не фильтр». Так speedtest.net и nflxvideo.net числились
        // мёртвыми, работая.
        return throughTunnel && kind is BlockKind.TlsDpi or BlockKind.Stall
            or BlockKind.Full or BlockKind.HttpsPort
            ? BlockKind.TunnelFailed
            : kind;
    }

    private static BlockKind Decide(
        ProbeOutcome tcp,
        ProbeOutcome tls12,
        ProbeOutcome tls13,
        ProbeOutcome http,
        ProbeOutcome data)
    {
        // Состоявшееся рукопожатие больше не считается ответом на вопрос.
        // Раньше считалось — и проверка называла steamcommunity.com доступным,
        // пока он не открывался: рукопожатие проходило, а поток умирал
        // на четырнадцатой тысяче байт.
        if (tls12.Ok || tls13.Ok)
        {
            if (!data.Ok)
                return BlockKind.Stall;

            // Данные пришли, но это отказ самого сайта. Всё, что мы умеем
            // мерить, говорит «работает», и по своим меркам оно право —
            // работает связь, а отказывает сайт.
            return data.Refused ? BlockKind.GeoBlock : BlockKind.None;
        }

        if (tcp.Ok && (tls12.Reset || tls13.Reset))
            return BlockKind.TlsDpi;

        // Обе версии отвергнуты согласованием — это настройка стороны,
        // а не фильтр. Бывает у площадок, отключивших старый протокол:
        // они отвечают отказом, и отвечают быстро. Десинк такому не поможет,
        // и предлагать его значит посылать чинить исправное.
        if (tcp.Ok && tls12.Unsupported && tls13.Unsupported)
            return BlockKind.Handshake;

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

    /// <summary>
    /// Адрес, по которому никого нет и быть не может.
    /// </summary>
    /// <remarks>
    /// Петля и нулевой адрес — два способа сказать «сюда не ходи». Первый
    /// отдаёт BunnyCDN, отшивая регион; второй принято отдавать блокировщикам
    /// рекламы. Различать их незачем: ни туда, ни туда идти не надо.
    /// </remarks>
    internal static bool IsStub(IPAddress address) =>
        IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any);

    private static ProbeOutcome Failed(string? detail) => new()
    {
        Ok = false,
        Detail = detail,
    };

    /// <summary>
    /// Обходит адреса имени по одному, каждому давая свой срок.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Найдено на <c>updates.discord.com</c>. У него пять адресов, из которых
    /// закрыт один — <c>162.159.136.232</c>, — а четыре отвечают. Замер
    /// 2026-09-06: и Google, и Cloudflare, и Яндекс возвращают все пять,
    /// различаясь лишь порядком.
    /// </para>
    /// <para>
    /// Прежде мы звали <c>ConnectAsync(host, port)</c> с одним сроком на всю
    /// попытку. Он перебирает адреса сам, но закрытый адрес молчит, съедая
    /// весь бюджет целиком, — и до остальных дело не доходило никогда.
    /// Имя объявлялось закрытым, тогда как настоящий клиент открывает его
    /// со второй попытки и даже не замечает.
    /// </para>
    /// <para>
    /// Свой срок каждому — это и есть поведение клиента. Хост достижим, если
    /// отвечает хоть один его адрес; обратное утверждение требует опросить
    /// все, а не первый попавшийся.
    /// </para>
    /// </remarks>
    /// <returns>Адрес, который ответил, и исход пробы.</returns>
    private static async Task<(IPAddress? Answered, ProbeOutcome Outcome)> ConnectAnyAsync(
        string host,
        int port,
        AddressFamily family,
        IReadOnlyList<IPAddress>? dialled,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var targets = dialled?.Where(a => a.AddressFamily == family).ToList() ?? [];

        // Адресов не дали — спрашиваем именем, как раньше. Так работает
        // измерение сети, где имени соответствует известно что.
        if (targets.Count == 0)
        {
            try
            {
                using var client = new TcpClient(family);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);

                await client.ConnectAsync(host, port, timeout.Token);

                return (null, new ProbeOutcome { Ok = true, Elapsed = stopwatch.Elapsed });
            }
            catch (Exception ex)
            {
                return (null, new ProbeOutcome
                {
                    Ok = false,
                    Reset = IsReset(ex),
                    Elapsed = stopwatch.Elapsed,
                    Detail = Explain(ex, dialled, port),
                });
            }
        }

        Exception? last = null;
        var silent = new List<IPAddress>();

        foreach (var address in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var client = new TcpClient(family);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Timeout);

                await client.ConnectAsync(address, port, timeout.Token);

                return (address, new ProbeOutcome
                {
                    Ok = true,
                    Elapsed = stopwatch.Elapsed,

                    // Молчавшие называются и при успехе: часть адресов имени
                    // закрыта, и это стоит знать, даже когда обошлось.
                    Detail = silent.Count == 0
                        ? null
                        : $"{address}:{port} ответил; молчали {string.Join(", ", silent)}",
                });
            }
            catch (Exception ex)
            {
                last = ex;
                silent.Add(address);
            }
        }

        return (null, new ProbeOutcome
        {
            Ok = false,
            Reset = last is not null && IsReset(last),
            Elapsed = stopwatch.Elapsed,
            Detail = last is null ? null : Explain(last, targets, port),
        });
    }

    private static async Task<ProbeOutcome> TcpAsync(
        string host,
        int port,
        AddressFamily family,
        CancellationToken cancellationToken,
        IReadOnlyList<IPAddress>? dialled = null) =>
        (await ConnectAnyAsync(host, port, family, dialled, cancellationToken)).Outcome;

    /// <remarks>
    /// Проверка сертификата здесь отключена намеренно, о чём ниже. Анализатор
    /// об этом знать не может и справедливо ругается на всякий такой колбэк —
    /// подавлено адресно, чтобы предупреждение осталось живым в остальном коде,
    /// где оно означало бы настоящую дыру.
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5359:Do not disable certificate validation",
        Justification = "Измеряется доходимость рукопожатия, а не доверие. " +
            "Подлинность проверяется отдельно, в CertificateMatchesAsync, и строго.")]
    /// <param name="via">
    /// Адрес, который только что ответил на TCP. Соединяемся с ним, а имя
    /// в рукопожатии остаётся настоящим: иначе проба заново упирается
    /// в закрытый адрес того же имени и врёт про рукопожатие то, что на деле
    /// про маршрут.
    /// </param>
    /// <param name="through">
    /// Порт служебного входа: соединяться нарочно через туннель, а не по
    /// адресу. Тогда имя уезжает движку именем, и правила по доменам
    /// срабатывают так же, как для настоящего приложения.
    /// </param>
    private static async Task<ProbeOutcome> TlsAsync(
        string host,
        SslProtocols protocol,
        CancellationToken cancellationToken,
        IReadOnlyList<IPAddress>? dialled = null,
        IPAddress? via = null,
        int? through = null)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            using var client = through is { } port
                ? await TunnelProbe.ConnectAsync(port, host, 443, Timeout, cancellationToken)
                : new TcpClient(AddressFamily.InterNetwork);

            if (through is not null)
            {
                // Соединение уже открыто входом — дальше сразу рукопожатие.
            }
            else if (via is null)
                await client.ConnectAsync(host, 443, timeout.Token);
            else
                await client.ConnectAsync(via, 443, timeout.Token);

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
            bool unsupported = RefusedVersion(ex);

            return new ProbeOutcome
            {
                Ok = false,
                Reset = IsReset(ex),
                Unsupported = unsupported,
                Elapsed = stopwatch.Elapsed,
                Detail = unsupported
                    ? $"сторона не согласовала {Name(protocol)} — это её настройка, не фильтр"
                    : Explain(ex, dialled, 443),
            };
        }
    }

    private static string Name(SslProtocols protocol) =>
        protocol == SslProtocols.Tls12 ? "TLS 1.2" : "TLS 1.3";

    /// <summary>
    /// Отказ согласования, а не обрыв разговора.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Половина «нет» в таблице — вовсе не блокировка. У Cloudflare часть
    /// площадок отключила TLS 1.2, а у старых серверов нет 1.3, и обе стороны
    /// честно об этом сообщают. Прежде такая ячейка выглядела как убитое
    /// рукопожатие, и человек шёл подбирать рецепт к тому, что не сломано.
    /// </para>
    /// <para>
    /// Опознаётся по типу отказа: провал согласования приходит
    /// <see cref="System.Security.Authentication.AuthenticationException"/>
    /// и приходит быстро. Фильтр так не отвечает — он рвёт соединение
    /// или молчит до истечения срока, а это уже сокетная ошибка либо отмена.
    /// </para>
    /// </remarks>
    private static bool RefusedVersion(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is System.Security.Authentication.AuthenticationException)
                return e.InnerException is not System.Net.Sockets.SocketException;
        }

        return false;
    }

    /// <summary>
    /// Проверяет, идут ли данные после того, как рукопожатие состоялось.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Единственная проба, отвечающая на вопрос «работает ли сайт». Остальные
    /// отвечают на «устанавливается ли соединение», а это, как выяснилось,
    /// разные вопросы: <c>steamcommunity.com</c> проходил все прежние проверки
    /// и при этом не открывался.
    /// </para>
    /// <para>
    /// Замершим поток считается по молчанию, а не по объёму: отсчёт идёт от
    /// последнего полученного байта. Медленный сайт подаёт признаки жизни
    /// и проходит, убитый замолкает совсем. Порогом по объёму такое не поймать —
    /// у страниц он разный, и всякое число было бы взято с потолка.
    /// </para>
    /// </remarks>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA5359:Do not disable certificate validation",
        Justification = "Измеряется проходимость потока, а не доверие. " +
            "Подлинность проверяется отдельно, в CertificateMatchesAsync.")]
    /// <param name="through">
    /// Порт служебного входа: качать нарочно через туннель. См.
    /// <see cref="TunnelProbe"/>.
    /// </param>
    private static async Task<ProbeOutcome> TransferAsync(
        string host,
        CancellationToken cancellationToken,
        IReadOnlyList<IPAddress>? dialled = null,
        IPAddress? via = null,
        int? through = null)
    {
        var stopwatch = Stopwatch.StartNew();
        int total = 0;

        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connect.CancelAfter(Timeout);

            using var client = through is { } port
                ? await TunnelProbe.ConnectAsync(port, host, 443, Timeout, cancellationToken)
                : new TcpClient(AddressFamily.InterNetwork);

            if (through is not null)
            {
                // Соединение открыл вход — дальше всё как при прямом замере.
            }
            else if (via is null)
                await client.ConnectAsync(host, 443, connect.Token);
            else
                await client.ConnectAsync(via, 443, connect.Token);

            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, connect.Token);

            var request = System.Text.Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {host}\r\n" +
                "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n" +
                "Accept: text/html\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");

            await ssl.WriteAsync(request, connect.Token);

            var buffer = new byte[16 * 1024];
            int? status = null;
            long? promised = null;

            // Длина заголовков считается отдельно: Content-Length обещает
            // только тело, а total растёт с первого байта ответа. Без поправки
            // сравнение завышало бы полученное и объявляло страницу дочитанной
            // раньше времени.
            int header = 0;

            while (total < EnoughBytes)
            {
                using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                silence.CancelAfter(StallWindow);

                int read;

                try
                {
                    read = await ssl.ReadAsync(buffer, silence.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Тишина при живом соединении — то самое, что ищем.
                    // Пара «пришло из обещанного» опознаёт класс с одного
                    // взгляда: обрыв на десятой доле — это убитый поток,
                    // а на девяти десятых — просто медленная страница.
                    return new ProbeOutcome
                    {
                        Ok = false,
                        Elapsed = stopwatch.Elapsed,
                        Detail = promised is { } expected
                            ? $"поток замер на {total} из {expected} Б, тишина {StallWindow.TotalSeconds:0} с"
                            : $"поток замер на {total} Б, тишина {StallWindow.TotalSeconds:0} с",
                    };
                }

                // Конец ответа: сервер сказал всё, что хотел. Для короткой
                // страницы это штатный и самый частый исход.
                if (read == 0)
                    break;

                // Код ответа и обещанный объём берутся из первой порции:
                // заголовки приходят первыми, и дочитывать ради них
                // страницу незачем.
                if (status is null)
                {
                    status = ParseStatus(buffer, read);
                    promised = ParseContentLength(buffer, read);
                    header = HeaderLength(buffer, read);
                }

                total += read;

                // Тело пришло целиком — дальше ждать нечего.
                //
                // Прежде выход был только через закрытие соединения или через
                // четверть мегабайта, и сайт, отдавший всё обещанное, но
                // соединение не закрывший, попадал в тишину на три секунды
                // и объявлялся оборванным. Разница между «поток убили» и «ответ
                // закончился» — в том, дошло ли обещанное; закрытие сокета
                // сервером к этому отношения не имеет и зависит от его
                // настроек, а не от нашей связи.
                if (promised is { } body && header > 0 && total - header >= body)
                    break;
            }

            return new ProbeOutcome
            {
                Ok = true,
                Refused = status is 403 or 451,
                Elapsed = stopwatch.Elapsed,
                Detail = status is 403 or 451
                    ? $"сайт ответил {status} — связь исправна, отказывает он сам"
                    : promised is { } size ? $"{total} из {size} Б" : $"{total} Б",
            };
        }
        catch (Exception ex)
        {
            return new ProbeOutcome
            {
                Ok = false,
                Reset = IsReset(ex),
                Elapsed = stopwatch.Elapsed,
                Detail = total > 0
                    ? $"оборвано на {total} Б: {Explain(ex, dialled, 443)}"
                    : Explain(ex, dialled, 443),
            };
        }
    }

    /// <summary>
    /// Длина заголовков вместе с пустой строкой; <c>0</c> — конец не найден.
    /// </summary>
    /// <remarks>
    /// Нужна, чтобы отличить полученное тело от полученного ответа целиком.
    /// Ноль означает «заголовки не поместились в первую порцию» — случай
    /// редкий, и полагаться на длину тогда нельзя.
    /// </remarks>
    internal static int HeaderLength(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n'
                && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i + 4;
            }
        }

        return 0;
    }

    /// <summary>
    /// Код состояния из начала ответа; <c>null</c>, если это не HTTP.
    /// </summary>
    /// <remarks>
    /// Различаются только 403 и 451, и намеренно. Прочие коды говорят о самой
    /// странице — 404, 500 и им подобные встречаются у исправных сайтов
    /// на корневом пути сплошь и рядом, и объявлять их блокировкой значило бы
    /// поднимать ложную тревогу вместо диагноза. А 451 введён именно для
    /// «недоступно по правовым причинам», и 403 у сетей доставки означает
    /// то же самое на деле.
    /// </remarks>
    /// <summary>
    /// Сколько байт обещано заголовком; <c>null</c>, если не сказано.
    /// </summary>
    /// <remarks>
    /// Заголовка может не быть вовсе — при <c>chunked</c> объём заранее
    /// неизвестен, и это не повод для тревоги. Пара «пришло из обещанного»
    /// нужна ровно затем, чтобы отличить убитый поток от медленного, а без
    /// обещанного остаётся просто «пришло», как и было.
    /// </remarks>
    private static long? ParseContentLength(byte[] buffer, int length)
    {
        var head = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Min(length, 4096));

        foreach (var line in head.Split("\r\n"))
        {
            // Пустая строка — конец заголовков; дальше тело, и искать там
            // нечего.
            if (line.Length == 0)
                break;

            if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                continue;

            return long.TryParse(line[15..].Trim(), out var size) ? size : null;
        }

        return null;
    }

    private static int? ParseStatus(byte[] buffer, int length)
    {
        var head = System.Text.Encoding.ASCII.GetString(buffer, 0, Math.Min(length, 64));

        if (!head.StartsWith("HTTP/", StringComparison.Ordinal))
            return null;

        var parts = head.Split(' ');

        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : null;
    }

    /// <summary>
    /// Обычный HTTP на 80 порту.
    /// </summary>
    /// <remarks>
    /// Нужен, чтобы отличить вмешательство в TLS от закрытого маршрута:
    /// если по 80 порту хост отвечает, значит он достижим, и дело именно
    /// в рукопожатии.
    /// </remarks>
    private static async Task<ProbeOutcome> HttpAsync(
        string host,
        CancellationToken cancellationToken,
        IReadOnlyList<IPAddress>? dialled = null,
        IPAddress? via = null)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            if (via is null)
                await client.ConnectAsync(host, 80, timeout.Token);
            else
                await client.ConnectAsync(via, 80, timeout.Token);

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
                Detail = Explain(ex, dialled, 80),
            };
        }
    }

    /// <summary>
    /// Переводит отказ на язык, на котором о нём можно думать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Истёкший у нас таймаут приходит как «операция отменена», и в отчёте это
    /// выглядело сообщением от сети — будто хост так ответил. На деле ответа
    /// не было вовсе, и читающий вправе знать, что ждали мы, а не нам отказали.
    /// </para>
    /// <para>
    /// Отказ сокета называется своим именем и кодом. Прежде он приходил
    /// в отчёт системным текстом Windows — «Попытка установить соединение была
    /// безуспешной…», — где не различить отвергнутое соединение от
    /// недостижимой сети, а это разные поломки с разными виновниками.
    /// </para>
    /// <para>
    /// И то и другое сопровождается адресом, к которому шли. Без него строка
    /// говорит о хосте, а отвечал за неё, случалось, прибитый в hosts адрес
    /// или fakeip нашего же туннеля, — и человек чинил сайт, тогда как чинить
    /// надо было запись в файле.
    /// </para>
    /// </remarks>
    private static string Explain(Exception ex, IReadOnlyList<IPAddress>? dialled = null, int port = 0)
    {
        var where = At(dialled, port);

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException socket)
                return $"{Name(socket.SocketErrorCode)} ({socket.ErrorCode}){where}";
        }

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is OperationCanceledException)
                return $"нет ответа за {Timeout.TotalSeconds:0} с{where}";
        }

        return ex.GetBaseException().Message + where;
    }

    /// <summary>Куда стучались — адрес и порт, а не только имя.</summary>
    private static string At(IReadOnlyList<IPAddress>? dialled, int port)
    {
        if (dialled is null || dialled.Count == 0)
            return string.Empty;

        var first = $", {dialled[0]}:{port}";

        // Соединение пробует все адреса подряд, поэтому назвать один и умолчать
        // об остальных значило бы приписать отказ ему одному.
        return dialled.Count == 1 ? first : $"{first} — всего адресов {dialled.Count}";
    }

    /// <summary>
    /// Как называется отказ сокета.
    /// </summary>
    /// <remarks>
    /// Имя из перечисления оставлено в скобках намеренно: по нему ищется
    /// объяснение, а перевод нужен, чтобы понять, не ища.
    /// </remarks>
    private static string Name(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "соединение отвергнуто, ConnectionRefused",
        SocketError.ConnectionReset => "соединение сброшено, ConnectionReset",
        SocketError.ConnectionAborted => "соединение прервано, ConnectionAborted",
        SocketError.TimedOut => "истекло время у системы, TimedOut",
        SocketError.HostUnreachable => "хост недостижим, HostUnreachable",
        SocketError.NetworkUnreachable => "сеть недостижима, NetworkUnreachable",
        SocketError.HostNotFound => "имя не разрешается, HostNotFound",
        SocketError.TryAgain => "имя не разрешилось с первого раза, TryAgain",
        SocketError.NoData => "у имени нет записи нужного типа, NoData",
        SocketError.AddressNotAvailable => "адрес недоступен, AddressNotAvailable",
        SocketError.NetworkDown => "сеть выключена, NetworkDown",
        SocketError.AccessDenied => "доступ запрещён, AccessDenied",
        _ => $"отказ сокета {error}",
    };

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

    /// <summary>
    /// Имя, на которое ведёт CNAME; <c>null</c>, если не ведёт никуда.
    /// </summary>
    /// <remarks>
    /// Спрашивается только тогда, когда адреса не нашлось, и служит ровно
    /// одному различию: поломанная цепочка против пустой зоны. Ответ берётся
    /// у DoH, потому что системный резолвер на имя без адреса часто отвечает
    /// «нет такого хоста», не показав CNAME вовсе.
    /// </remarks>
    private static async Task<string?> DohCanonicalNameAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.Accept.Add(new("application/dns-json"));

            var json = await http.GetStringAsync(
                $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(host)}&type=A",
                cancellationToken);

            using var document = System.Text.Json.JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Answer", out var answers))
                return null;

            foreach (var answer in answers.EnumerateArray())
            {
                // Тип 5 — CNAME. Числом, а не именем: в ответе формата
                // dns-json тип приходит числом.
                if (answer.TryGetProperty("type", out var type) && type.GetInt32() == 5
                    && answer.TryGetProperty("data", out var data))
                {
                    return data.GetString()?.TrimEnd('.');
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
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
