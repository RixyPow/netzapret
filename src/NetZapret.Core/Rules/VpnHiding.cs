namespace NetZapret.Core.Rules;

/// <summary>
/// Имена, по которым российские приложения узнают, что на машине VPN, —
/// всегда напрямую, мимо туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 01.10, после разбора Zapret KVN: «делай». Список — из шаблона
/// конфига KVN (правило 4): сервисы «узнать свой адрес» и адреса, к которым
/// ходят приложения Яндекса, VK, MAX и 2ГИС.
/// </para>
/// <para>
/// KVN эти имена блокирует. Мы пускаем их напрямую — и вот почему. Чтобы
/// приложение не увидело зарубежный адрес выхода, достаточно, чтобы оно
/// спросило свой адрес с домашнего. Блокировка же ломает лишнее:
/// <c>mobileproxy.passport.yandex.net</c> — адрес, через который приложения
/// Яндекса получают токены входа, а <c>oneme.ru</c> — домен MAX. Что
/// блокировка этих имён ломает вход, не проверено, но и проверять это на
/// людях незачем, когда «напрямую» прячет так же.
/// </para>
/// <para>
/// Правило «напрямую» доживает до режима «всё через VPN, кроме РФ» и
/// пропадает в «без исключений» вместе со всеми прочими — это смысл того
/// режима, и он не нарушается.
/// </para>
/// </remarks>
public static class VpnHiding
{
    public static readonly IReadOnlyList<string> Names =
    [
        "api.ipify.org",
        "checkip.amazonaws.com",
        "ifconfig.me",
        "ip.mail.ru",
        "ipv4-internet.yandex.net",
        "ipv6-internet.yandex.net",
        "mobileproxy.passport.yandex.net",
        "relay-api.eu.2gis.com",
        "trace-flow.ru",
        "api.oneme.ru",
        "vk-analytics.ru",
        "apptracer.ru",
    ];

    /// <summary>
    /// Правила для движка: имя вместе с поддоменами — напрямую.
    /// </summary>
    /// <remarks>
    /// Маска <c>*.имя</c> покрывает и само имя (<see cref="GlobMatcher"/>),
    /// как <c>domain_suffix</c> у KVN.
    /// </remarks>
    public static IReadOnlyList<RoutingRule> Rules() =>
        Names.Select(name => new RoutingRule
        {
            Match = MatchKind.Domain,
            Value = "*." + name,
            Mode = RoutingMode.Direct,
            Source = RuleSource.Setting,
        }).ToList();
}
