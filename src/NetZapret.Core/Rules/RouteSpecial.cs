namespace NetZapret.Core.Rules;

/// <summary>Особый маршрут: путь, которому мало одного режима движков.</summary>
/// <remarks>
/// <para>
/// Владелец 12.11: пины сервиса («Пины напрямую»), пины посредников и прокси
/// Telegram — особыми режимами в окне пути, пробуемыми автоподбором, с пометкой
/// в <c>rules.user.yaml</c> и в отчёте. Прежде пин жил в hosts, маршрут —
/// в файле правил, и по файлу нельзя было понять, что Instagram держится на пине.
/// </para>
/// <para>
/// Режим движков (<see cref="RoutingRule.Mode"/>) у особого маршрута остаётся
/// обычным — им и работает: пин сервиса идёт десинком, пин посредника —
/// напрямую (щитом), прокси Telegram — десинком. Пометка — слой сверху: что
/// ещё поставлено ради этого пути. Сами пины по-прежнему стоят в hosts
/// (решение владельца 13.11: «пины должны стоять в hosts»); запись в файле
/// говорит, какие именно, и по ней их ставит загрузка чужого файла маршрутов.
/// </para>
/// </remarks>
public enum RouteSpecial
{
    None,

    /// <summary>Пин на адреса самого сервиса (<c>config/pin-solutions.yaml</c>) — с десинком поверх.</summary>
    ServicePin,

    /// <summary>Пин на адрес посредника или любой другой, который держит щит, — напрямую.</summary>
    RelayPin,

    /// <summary>Приложение Telegram — через свой прокси (tg-ws-proxy).</summary>
    TgProxy,
}

/// <summary>Слова и правила особых маршрутов.</summary>
public static class RouteSpecials
{
    /// <summary>Режим движков, которым особый маршрут работает.</summary>
    public static RoutingMode ModeOf(RouteSpecial special) => special switch
    {
        RouteSpecial.RelayPin => RoutingMode.Direct,
        _ => RoutingMode.Desync,
    };

    /// <summary>Слово в файле правил.</summary>
    public static string? Word(RouteSpecial special) => special switch
    {
        RouteSpecial.ServicePin => "service-pin",
        RouteSpecial.RelayPin => "relay-pin",
        RouteSpecial.TgProxy => "tg-proxy",
        _ => null,
    };

    /// <summary>Разбирает слово файла; незнакомое — <see cref="RouteSpecial.None"/>.</summary>
    /// <remarks>
    /// Незнакомое не роняет файл: его могла записать программа новее этой,
    /// а файл маршрутов целиком дороже одной пометки.
    /// </remarks>
    public static RouteSpecial Parse(string? word) => word?.Trim().ToLowerInvariant() switch
    {
        "service-pin" => RouteSpecial.ServicePin,
        "relay-pin" => RouteSpecial.RelayPin,
        "tg-proxy" => RouteSpecial.TgProxy,
        _ => RouteSpecial.None,
    };

    /// <summary>Название для людей — в окне, в «Маршрутах» и в отчёте.</summary>
    public static string Title(RouteSpecial special) => special switch
    {
        RouteSpecial.ServicePin => "пин сервиса",
        RouteSpecial.RelayPin => "пин посредника",
        RouteSpecial.TgProxy => "TG Proxy",
        _ => string.Empty,
    };

    /// <summary>Пометка с подробностями: «пин посредника Zapret DNS».</summary>
    public static string Describe(RouteSpecial special, string? pin) =>
        Title(special) + (string.IsNullOrWhiteSpace(pin) ? string.Empty : " " + pin.Trim());
}
