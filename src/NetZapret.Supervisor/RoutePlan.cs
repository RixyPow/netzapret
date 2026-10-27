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
    public static RoutePlan For(TargetReport report, RoutingMode route, bool desync, bool tunnel)
    {
        var steps = new List<RoutePlanStep>();
        bool vpn = tunnel && route != RoutingMode.Proxy;

        void Vpn(string why)
        {
            if (vpn)
                steps.Add(new(RouteTry.Vpn, [], why));
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

                Vpn("десинк не помог");
                return new(steps, null);

            case BlockKind.TlsByAddress or BlockKind.Full or BlockKind.HttpsPort:
                Vpn("режут адрес — десинку тут нечего прятать");
                return new(steps, "Без VPN — только другой адрес того же сервиса, если он на CDN вроде Cloudflare: "
                    + "подстановкой адреса.");

            case BlockKind.GeoBlock:
                Vpn("сайт отказывает по стране — нужен выход в другой стране");
                return new(steps, "Без VPN — посредник: пин из каталога на вкладке «Файл hosts» или Zapret DNS "
                    + "(dns.zapret.moe) для сервисов из его списка.");

            case BlockKind.TunnelFailed:
                return new([], "Имя уже идёт через VPN, а туннель до него не дотянулся: другой сервер подписки или пул.");

            default:
                // DNS, заглушка, оборванный CNAME, чужой туннель, своё приветствие:
                // маршрут тут ни при чём.
                return new([], $"Маршрут тут не поможет: {report.Remedy()}.");
        }
    }
}
