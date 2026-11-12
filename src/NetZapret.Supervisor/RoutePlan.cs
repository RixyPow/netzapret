using NetZapret.Core.Rules;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>Что пробовать.</summary>
public enum RouteTry
{
    /// <summary>Напрямую — имя на время в щит: не мешает ли ему десинк.</summary>
    Direct,

    /// <summary>Десинк пресета целиком — у имени, которое сейчас идёт напрямую.</summary>
    Preset,

    /// <summary>Рецепты каталога по одному, пробным winws2.</summary>
    Recipes,

    /// <summary>Через VPN — вход проверки движка.</summary>
    Vpn,

    /// <summary>
    /// Запасной адрес Cloudflare (<see cref="CloudflareNet.Spares"/>) — пином в hosts, если на нём откроется.
    /// </summary>
    /// <remarks>
    /// Любой сайт Cloudflare отвечает на любом его адресе (замер 05.11: 20 сайтов
    /// на 8.47.69.6 и 8.6.112.6 — тот же ответ до байта), а режут — по адресу или
    /// подсети. Дешевле VPN: ни трафика подписки, ни выхода в другой стране.
    /// </remarks>
    CloudflareSpare,

    /// <summary>
    /// Пин сервиса — имена на адресах самого сервиса (pin-solutions.yaml), с десинком поверх.
    /// </summary>
    /// <remarks>
    /// Особый путь (владелец 12.11): у сервиса бывают узлы, которые провайдер
    /// не режет по адресу, — каталог Zapret GUI держит их в разделе «Напрямую».
    /// Пробуется под рецептом своей секции — так он и пойдёт после записи.
    /// </remarks>
    ServicePin,

    /// <summary>
    /// Пин посредника — имя на прокси посредника (Zapret DNS, Comss…), напрямую.
    /// </summary>
    /// <remarks>
    /// Особый путь (владелец 12.11): обходит и отказ по стране, и блок имени —
    /// посредник везёт приветствие из-за границы. Пробуется мимо десинка, с портов
    /// пробы: пин посредника держит щит, а под рецептом он не живёт (замер 06.10:
    /// со щитом 20 из 20, под рецептом 0 из 10).
    /// </remarks>
    RelayPin,

    /// <summary>Приложение Telegram через свой прокси — не шаг плана, а подбор «Приложения» (<see cref="SiteFixer.PickTelegramAsync"/>).</summary>
    TgProxy,

    /// <summary>
    /// Мимо десинка — с портов пробы, которые основной winws2 не перехватывает.
    /// </summary>
    /// <remarks>Не шаг плана: так подбор узнаёт, не закрыл ли имя сам десинк (<see cref="SiteFixer"/>).</remarks>
    Around,
}

/// <summary>Один шаг плана: что пробовать и почему.</summary>
/// <param name="Recipes">Для <see cref="RouteTry.Recipes"/> — рецепты каталога по порядку.</param>
public sealed record RoutePlanStep(RouteTry Kind, IReadOnlyList<string> Recipes, string Why);

/// <summary>
/// План подбора маршрута: по симптому из проверки блокировок — что пробовать и в каком порядке.
/// </summary>
/// <remarks>
/// <para>
/// Таблица «симптом — маршрут», сверенная с вики zapret.moe 27.10 («Инспекторы
/// ТСПУ», 23 июня, QUIC). Симптом только задаёт порядок: решает замер — каждый
/// шаг пробуется вживую, и берётся первый открывший (<see cref="SiteFixer"/>).
/// </para>
/// <para>
/// От дешёвого к дорогому: напрямую, десинк, туннель. Туннель последним —
/// он тратит трафик подписки и прибавляет задержку. Где маршрут не поможет
/// (DNS, заглушка, чужой туннель, пин), шагов нет, а есть совет.
/// </para>
/// </remarks>
/// <param name="Advice">Что сказать сверх шагов; <c>null</c> — нечего.</param>
public sealed record RoutePlan(IReadOnlyList<RoutePlanStep> Steps, string? Advice)
{
    /// <summary>
    /// Рецепты для рвущегося рукопожатия: сперва разрез и подделка по имени.
    /// </summary>
    public static IReadOnlyList<string> ClientHelloOrder { get; } =
    [
        "multidisorder", "fake-multidisorder", "tls-multisplit-sni", "hostfakesplit",
        "hostfakesplit-multi", "hostfakesplit-stealth", "seqovl-4pda", "syndata",
        "fake-seqovl-awswaf", "fake-seqovl-steam-chat", "fake-seqovl-steamstatic", "fake-seqovl-teamspeak",
        "seqovl-white", "white-sandwich", "flood-white", "ttl-ladder",
    ];

    /// <summary>
    /// Рецепты для белого списка и заморозки: сперва подделка разрешённого имени.
    /// </summary>
    /// <remarks>
    /// Ковровый блок подсети снимается приветствием с разрешённым именем
    /// (вики: «16к-блок» Cloudflare обходится подделкой живого разрешённого
    /// домена; у нас 7tv — наложение 4pda, 19.10). Заморозка опознаётся
    /// по рукопожатию, и рецепт на нём отменяет и её (skinsrestorer.net).
    /// </remarks>
    public static IReadOnlyList<string> WhiteNameOrder { get; } =
    [
        "seqovl-4pda", "hostfakesplit", "hostfakesplit-multi", "fake-seqovl-awswaf",
        "fake-seqovl-steam-chat", "fake-seqovl-steamstatic", "fake-seqovl-teamspeak", "seqovl-white",
        "white-sandwich", "hostfakesplit-stealth", "multidisorder", "fake-multidisorder",
        "tls-multisplit-sni", "flood-white", "ttl-ladder", "syndata",
    ];

    /// <param name="route">Каким маршрутом имя идёт сейчас.</param>
    /// <param name="desync">Десинк работает: рецептам и щиту есть где действовать.</param>
    /// <param name="tunnel">Туннель работает: VPN есть чем проверить.</param>
    /// <param name="cloudflare">Имя на адресе Cloudflare (<see cref="CloudflareNet"/>): есть запасные адреса.</param>
    /// <param name="servicePin">У имени есть пин сервиса (<see cref="RouteTry.ServicePin"/>).</param>
    /// <param name="relay">Имя можно прибить к посреднику (<see cref="RouteTry.RelayPin"/>): не подсеть.</param>
    public static RoutePlan For(
        TargetReport report,
        RoutingMode route,
        bool desync,
        bool tunnel,
        bool cloudflare = false,
        bool servicePin = false,
        bool relay = false)
    {
        var steps = new List<RoutePlanStep>();
        bool vpn = tunnel && route != RoutingMode.Proxy;

        // Пины — дешевле VPN (ни трафика подписки, ни выхода в чужой стране) и
        // встают перед ним. Пину сервиса нужен десинк: его имена чинит рецепт.
        void Service(string why)
        {
            if (servicePin && desync)
                steps.Add(new(RouteTry.ServicePin, [], why));
        }

        void Relay(string why)
        {
            if (relay)
                steps.Add(new(RouteTry.RelayPin, [], why));
        }

        void Vpn(string why)
        {
            if (vpn)
                steps.Add(new(RouteTry.Vpn, [], why));
        }

        // Мимо туннеля — пин уводит имя на другой адрес, но идёт оно по-прежнему
        // своим маршрутом; у имени «через VPN» адрес решает выход.
        void Spare(string why)
        {
            if (cloudflare && route != RoutingMode.Proxy)
                steps.Add(new(RouteTry.CloudflareSpare, [], why));
        }

        switch (report.Kind)
        {
            case BlockKind.None:
                return new([], report.Quic is { Ok: false }
                    ? "По TCP имя открывается, но QUIC с тем же адресом молчит — браузер ждёт HTTP/3. "
                        + "Поможет подделка QUIC по имени либо выключить QUIC в браузере (chrome://flags/#enable-quic)."
                    : null);

            case BlockKind.TlsDpi or BlockKind.Stall or BlockKind.TlsWhiteOnly:
                if (desync)
                {
                    // Белому списку щит не поможет: подсеть не пускает чужие имена
                    // и без всякого десинка.
                    if (route == RoutingMode.Desync && report.Kind != BlockKind.TlsWhiteOnly)
                        steps.Add(new(RouteTry.Direct, [], "вдруг мешает сам десинк"));

                    if (route == RoutingMode.Direct)
                        steps.Add(new(RouteTry.Preset, [], "десинк пресета"));

                    steps.Add(new(RouteTry.Recipes,
                        report.Kind == BlockKind.TlsDpi ? ClientHelloOrder : WhiteNameOrder,
                        report.Kind == BlockKind.TlsWhiteOnly ? "подделка разрешённого имени" : "рецепт на рукопожатие"));
                }

                Service("у сервиса есть узлы, которые не режут, — с десинком поверх");
                Relay("посредник везёт имя мимо провайдера");
                Vpn("десинк не помог");
                return new(steps, null);

            case BlockKind.TlsByAddress or BlockKind.Full or BlockKind.HttpsPort:
                Spare("режут адрес, а сайт на Cloudflare — он отвечает и на запасном");
                Service("режут адрес — у сервиса есть другие узлы");
                Relay("режут адрес — посредник ходит к сервису сам");
                Vpn("режут адрес — десинку тут нечего прятать");
                return new(steps, "Без VPN — только другой адрес того же сервиса, если он на CDN вроде Cloudflare: "
                    + "подстановкой адреса.");

            // Режут длину приветствия, а не имя: десинк пресета, разбивающий
            // приветствие по-своему, мог бы помочь (на ntc.party с запущенным
            // zapret2 открывалось всё); рецептов под это у нас не подобрано.
            case BlockKind.LongHello:
                if (desync && route == RoutingMode.Direct)
                    steps.Add(new(RouteTry.Preset, [], "десинк пресета"));

                Spare("на ntc.party 8.6.112.x и 8.47.69.x этот блок не трогал");
                Relay("посредник получает приветствие уже за границей");
                Vpn("короткое приветствие проходит, браузерное — нет");
                return new(steps, "Режут длинное приветствие браузера — с постквантовым ключом, двумя пакетами; "
                    + "короткое проходит. Так резали сайты Cloudflare 29.10–02.11.");

            // Нейросеть без пина: сперва пин — он дешевле VPN (решение владельца 05.11).
            // Подбор пина — своё окно, сам «Сайт не открывается?» его не ставит.
            case BlockKind.GeoBlock when report.PinFirst:
                Relay("сайт отказывает по стране — посредник отвечает из-за границы, VPN не нужен");
                Vpn("сайт отказывает по стране, а пина у имени нет — без пина нужен выход в другой стране");
                return new(steps, "Сначала пин на посредника: «Маршруты» → путь маршрута → «Пин посредника» — посредник отвечает "
                    + "из-за границы, VPN не нужен. Не нашёлся рабочий — VPN.");

            case BlockKind.GeoBlock:
                Relay("сайт отказывает по стране — посредник отвечает из-за границы");
                Vpn("сайт отказывает по стране — нужен выход в другой стране");
                return new(steps, "Без VPN — посредник: пин из каталога на вкладке «Файл hosts» или Zapret DNS "
                    + "(dns.zapret.moe) для сервисов из его списка.");

            // Почему отказали, проба не знает (робот, адрес, страна без слов),
            // но сюда приходят с «не открывается» в браузере — значит отказ
            // и там. Выход в другой стране проверяется замером, как и везде.
            case BlockKind.Forbidden:
                Relay("сайт отказал, причину не назвал — проверить посредником из-за границы");
                Vpn("сайт отказал, причину не назвал — проверить выходом в другой стране");
                return new(steps, "Связь исправна, отказал сам сайт. В браузере открывается — "
                    + "отказали только проверке, чинить нечего.");

            // Резолвер движка даёт нерабочий адрес. Лучшее лекарство — другой
            // резолвер (его ищет подбор, SiteFixer), а маршрутом помогает VPN:
            // имя «через VPN» получает подставной адрес и разрешается уже на
            // стороне выхода, мёртвый узел резолвера ему не достаётся (08.11,
            // картинки Steam на Akamai). Пин в hosts так не обойти — его подбор
            // отсекает раньше.
            case BlockKind.Dns:
                Vpn("резолвер даёт нерабочий адрес — через VPN имя разрешает выход");
                return new(steps, $"Без VPN — {report.Remedy()}.");

            case BlockKind.TunnelFailed:
                return new([], "Имя уже идёт через VPN, а туннель до него не дотянулся: другой сервер подписки или пул.");

            default:
                // DNS, заглушка, оборванный CNAME, чужой туннель, своё приветствие:
                // маршрут тут ни при чём.
                return new([], $"Маршрут тут не поможет: {report.Remedy()}.");
        }
    }
}
