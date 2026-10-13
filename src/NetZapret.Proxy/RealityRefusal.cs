namespace NetZapret.Proxy;

/// <summary>
/// Сервер REALITY не принял клиента — узнать и назвать обе возможные причины.
/// </summary>
/// <remarks>
/// <para>
/// Xray с 26.7.28 по умолчанию пускает только клиентов Xray не старше
/// 26.3.27 (<c>minClientVer</c>), а с 26.9.8 ещё и требует в ClientHello долю
/// X25519MLKEM768, которой sing-box не шлёт (SagerNet/sing-box#4520, открыта).
/// Отвергнутого клиента сервер молча уводит на сайт маскировки, и sing-box
/// видит чужой сертификат: «reality verification failed».
/// </para>
/// <para>
/// Те же слова sing-box пишет при любом отказе REALITY: сервер, не узнавший
/// клиента по ключу, shortId или SNI, точно так же уводит его на сайт
/// маскировки. Устаревший после смены ключей у поставщика ключ по журналу
/// от нового Xray не отличить — поэтому объяснение называет обе причины,
/// а не уверяет, что подписка ни при чём (так было в первой версии, 13.10).
/// </para>
/// <para>
/// Замер 13.10 на подписке autoXRAY: тот же конфиг через Xray 26.7.28 —
/// ответ за 0,25–0,30 с, через sing-box 1.14.1-extended-2.7.2 — отказ за
/// 0,1 с при любом отпечатке uTLS. Свой сервер Xray 26.7.28 на 127.0.0.1
/// с одноразовыми ключами — так же; <c>minClientVer: "1.0.0"</c> не помогает.
/// До этого человек видел только «мёртв выход подписки».
/// </para>
/// </remarks>
public static class RealityRefusal
{
    /// <summary>Слова sing-box в журнале.</summary>
    public const string Marker = "reality verification failed";

    /// <summary>Что сказать человеку.</summary>
    public const string Explanation =
        "сервер REALITY не принял подключение: либо ключ в подписке устарел, либо на сервере Xray 26.7.28 "
        + "или новее, а он пускает только клиентов Xray. Обновите подписку; не помогло — напишите поставщику: "
        + "нужен Xray 26.6.27 на сервере или TLS вместо REALITY";

    /// <summary>
    /// Сколько отказ считается нынешним.
    /// </summary>
    /// <remarks>
    /// Автоподбор мог уйти с сервера REALITY на другой, и тогда старый отказ
    /// объяснял бы чужую беду. Две минуты — четыре проверки сторожа.
    /// </remarks>
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(2);

    /// <summary>Говорит ли строка об отказе REALITY.</summary>
    public static bool In(string? text) =>
        text is not null && text.Contains(Marker, StringComparison.OrdinalIgnoreCase);

    /// <summary>Объясняет ли отказ нынешнюю неудачу туннеля.</summary>
    /// <param name="refusedAt">Когда движок последний раз написал об отказе; <c>null</c> — не писал.</param>
    /// <param name="now">Сейчас.</param>
    public static bool Recent(DateTimeOffset? refusedAt, DateTimeOffset now) =>
        refusedAt is { } at && now - at <= Fresh;
}
