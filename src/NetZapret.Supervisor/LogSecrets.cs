using System.Text.RegularExpressions;

namespace NetZapret.Supervisor;

/// <summary>
/// Ключи и прочие секреты в строке журнала: значение прячется, имя поля и строка остаются.
/// </summary>
/// <remarks>
/// <para>
/// Применяется при записи — в <see cref="RollingLog"/> (вывод движков и строки надзора)
/// и в <see cref="SharedLogWriter"/> (журнал окна), — чтобы ключ не ложился на диск
/// вовсе, а не только вычищался из отчёта (<see cref="SupportReport.Redact"/> зовёт
/// то же поверх своего). Решение владельца 05.11: структура в журнале остаётся,
/// секрет — нет.
/// </para>
/// <para>
/// Пути профиля и адреса страниц здесь не трогаются: секретом они не являются,
/// а для разбора на своей машине нужны. Их прячет только отчёт.
/// </para>
/// </remarks>
public static class LogSecrets
{
    /// <summary>Ссылки прокси целиком: в них ключ стоит до адреса.</summary>
    private static readonly Regex ProxyLink = new(
        @"\b(?:vless|vmess|trojan|ss|ssr|hysteria2?|hy2|tuic|wireguard|wg|socks5?|anytls|naive\+https)://\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Секрет прокси Telegram: прокси печатает его при запуске («Secret: …»)
    /// и в ссылке («secret=dd…», в том числе tg://proxy).
    /// </summary>
    /// <remarks>
    /// Снаружи он бесполезен — прокси слушает только 127.0.0.1, — но чужой
    /// секрет в присланном архиве незачем, а со сменой адреса прослушивания
    /// он перестал бы быть безобидным.
    /// </remarks>
    private static readonly Regex TelegramSecret = new(
        @"((?:Secret:\s+|secret=)(?:dd|ee)?)[0-9a-fA-F]{32}",
        RegexOptions.Compiled);

    private static readonly Regex Uuid = new(
        @"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",
        RegexOptions.Compiled);

    /// <summary>Поля с ключами в JSON, YAML, .conf и дампе WireGuard: значение прячется, имя поля остаётся.</summary>
    /// <remarks>
    /// Не справился с настройкой WireGuard — sing-box печатает в журнал весь дамп
    /// устройства строками «имя=значение» (опыт 05.11, AmneziaWG 3 с малым S3):
    /// <c>private_key</c>, <c>public_key</c>, <c>preshared_key</c> (без второго «_»,
    /// как в UAPI WireGuard) и <c>header_protection_key</c> — шестнадцатеричными.
    /// Структура дампа — jc, s3, endpoint, allowed_ip — остаётся: по ней и разбирают.
    /// </remarks>
    private static readonly Regex KeyField = new(
        @"(""?(?:password|passwd|private_?key|public_?key|pre_?shared_?key|header_?protection_?key|psk|short_id|shortId|uuid|token|secret|auth_str|obfs_password)""?\s*[:=]\s*)(""[^""]*""|[^\s,}\]]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Без этих кусков секретов в строке нет — дешёвая проверка до регулярок.
    /// </summary>
    /// <remarks>
    /// winws2 пишет строку на соединение, и прогонять каждую через четыре
    /// регулярки незачем: почти все отсеиваются здесь.
    /// </remarks>
    private static readonly string[] Hints =
    [
        "://", "key", "secret", "pass", "psk", "short", "uuid", "token", "auth", "-",
    ];

    /// <summary>Строка (или кусок журнала) без секретов; где их нет — та же строка.</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text) || !Hints.Any(h => text.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return text;

        var result = ProxyLink.Replace(text, "<ссылка прокси скрыта>");
        result = TelegramSecret.Replace(result, "$1<скрыт>");
        result = KeyField.Replace(result, "$1\"<скрыто>\"");
        result = Uuid.Replace(result, "<uuid>");
        return result;
    }
}
