namespace NetZapret.Proxy;

/// <summary>
/// Сервер REALITY не принял клиента — узнать и назвать возможные причины.
/// </summary>
/// <remarks>
/// <para>
/// Xray с 26.7.28 по умолчанию проверяет версию клиента (<c>minClientVer</c>),
/// а с 26.9.8 (XTLS/REALITY, 08.09.2026) требует в ClientHello долю ключа
/// X25519MLKEM768 перед X25519. Отвергнутого клиента сервер молча уводит на
/// сайт маскировки: если сертификат маскировки подходит под SNI, sing-box пишет
/// «reality verification failed»; если маскировка и SNI различаются, раньше
/// срабатывает проверка сертификата (<c>x509: certificate is valid for …</c>).
/// </para>
/// <para>
/// Замер 27.11 (sing-box 1.14.3-extended-2.7.3 против локальных Xray 26.7.28
/// и 26.11.9, в том числе с <c>mldsa65Seed</c>): движок шлёт версию клиента
/// 26.7.11 и при <c>support_x25519mlkem768: true</c> с отпечатком <c>chrome</c>
/// (<c>AppSettings.RealityMlkem</c>) проходит рукопожатие и везёт трафик.
/// Отказ остаётся, если в подписке устарели ключ/shortId/SNI, если выключен
/// «Постквантовый REALITY (Xray 26.9+)» или если владелец сервера вручную
/// поднял <c>minClientVer</c> выше 26.7.11.
/// </para>
/// <para>
/// По журналу устаревший ключ от отказа версии не отличить — поэтому объяснение
/// называет все три причины и не уверяет, что подписка ни при чём (так было
/// в первой версии, 13.10).
/// </para>
/// </remarks>
public static class RealityRefusal
{
    /// <summary>Слова sing-box в журнале.</summary>
    public const string Marker = "reality verification failed";

    /// <summary>Что сказать человеку.</summary>
    public const string Explanation =
        "сервер REALITY не принял подключение: возможно, ключ в подписке устарел, "
        + "в «Настройках туннеля» выключен «Постквантовый REALITY (Xray 26.9+)» или сервер требует "
        + "клиента Xray новее 26.7.11. Обновите подписку и проверьте эту настройку; не помогло — "
        + "напишите поставщику или выберите сервер с TLS вместо REALITY";

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
