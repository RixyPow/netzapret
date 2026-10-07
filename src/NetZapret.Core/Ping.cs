namespace NetZapret.Core;

/// <summary>
/// Как меряется задержка серверов — «Настройки туннеля» → «Проверка серверов».
/// </summary>
/// <remarks>
/// <para>
/// Владелец 07.10, по образцу настроек пинга в Happ: адрес проверки на выбор.
/// До того адрес был зашит в шести местах, и двумя разными: автоподбор движка
/// ходил на https://www.gstatic.com, а замер вкладки, сторож и пробник —
/// на http://cp.cloudflare.com. Теперь один адрес на всех.
/// </para>
/// <para>
/// Типов TCP и ICMP, как в Happ, нет нарочно: они говорят, что сервер
/// достижим, а не что через него идёт трафик, и автоподбор по ним садился бы
/// на серверы, которые отвечают, но ничего не пропускают.
/// </para>
/// <para>
/// «Лучшего из двух» (double у Happ) нет: был 07.10 и убран в тот же день
/// (владелец: «давай уберем»). Замер показал, что цифр он не снижает —
/// Бельгия 167 мс против 161–164 одним запросом, Нидерланды 109 против
/// 100–108: каждая проверка через движок — новое соединение, а имя адреса
/// проверки разрешает сам сервер, так что второму запросу нечего ускорять.
/// Низкие цифры Happ даёт keepalive по уже открытому соединению, а Clash API
/// так мерить не умеет. Зато запросов и времени — вдвое: «Замерить все» шло
/// 92 с вместо 57–60, и на вход Trust, где все 20 стран на одном адресе, ушло
/// ещё около 40 подключений подряд — вход закрылся, туннель ушёл в обход.
/// </para>
/// </remarks>
public static class Ping
{
    /// <summary>Cloudflare — по умолчанию: узел у выхода, пустой ответ, без TLS внутри туннеля.</summary>
    public const string Cloudflare = "http://cp.cloudflare.com/generate_204";

    /// <summary>Google — тот же пустой ответ; адрес, которым меряет Happ.</summary>
    public const string Google = "http://www.gstatic.com/generate_204";

    /// <summary>Адрес проверки из настроек; пустой или негодный — Cloudflare.</summary>
    public static string UrlOf(AppSettings settings) =>
        IsUrl(settings.PingUrl) ? settings.PingUrl!.Trim() : Cloudflare;

    /// <summary>Годится ли строка в адрес проверки: http или https, абсолютный.</summary>
    public static bool IsUrl(string? text) =>
        Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

    /// <summary>
    /// Сколько замер ждёт ответа сервера (<see cref="AppSettings.PingTimeoutSeconds"/>):
    /// не меньше секунды и не больше полуминуты, что бы ни стояло в файле.
    /// </summary>
    public static TimeSpan TimeoutOf(AppSettings settings) =>
        TimeSpan.FromSeconds(Math.Clamp(settings.PingTimeoutSeconds, 1, 30));
}

/// <summary>
/// Фрагментация рукопожатия TLS с VPN-сервером — «Настройки туннеля».
/// </summary>
/// <remarks>
/// Владелец 07.10. Нужна, когда оператор закрывает соединение именно с сервером,
/// узнавая его по имени в приветствии TLS: приветствие режется, и фильтр,
/// читающий его одним куском, имени не видит. Выключена по умолчанию —
/// включать после замера на сервере, который и правда режут.
/// </remarks>
public enum TlsFragment
{
    /// <summary>Рукопожатие как есть.</summary>
    Off,

    /// <summary>Приветствие несколькими записями TLS (<c>record_fragment</c>) — дешевле.</summary>
    Records,

    /// <summary>Приветствие несколькими пакетами TCP (<c>fragment</c>) — сильнее, но медленнее.</summary>
    Packets,
}
