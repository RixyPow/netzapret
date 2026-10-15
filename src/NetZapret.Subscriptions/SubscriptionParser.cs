using System.Globalization;

namespace NetZapret.Subscriptions;

/// <summary>
/// Разбирает тело подписки и её заголовки.
/// </summary>
public static class SubscriptionParser
{
    /// <summary>
    /// Разбирает тело: либо список ссылок по строке на каждую, либо base64
    /// от такого списка. Формат определяется автоматически.
    /// </summary>
    public static (IReadOnlyList<ProxyServer> Servers, IReadOnlyList<string> Errors) ParseBody(string body)
    {
        var servers = new List<ProxyServer>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(body))
            return (servers, errors);

        var text = body.Trim();

        // Конфиг распознаётся до всего остального. Часть поставщиков отдаёт
        // готовый документ Xray, и серверы в нём лежат в outbounds: ссылок нет
        // вовсе. Ставить проверку ниже нельзя — документ без «://» внутри
        // уходит в разбор base64, не проходит его и объявляется испорченным.
        if (XrayConfigParser.LooksLikeConfig(text))
            return XrayConfigParser.Parse(text);

        // Признак открытого вида — наличие схемы. Пробовать base64 первым нельзя:
        // список ссылок иногда случайно проходит проверку на base64-алфавит.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            var decoded = Base64.DecodeToString(text);
            if (decoded is null)
            {
                errors.Add("тело подписки не разбирается: ни ссылок, ни корректного base64");
                return (servers, errors);
            }

            text = decoded;

            // И ещё раз после раскодирования: конфиг попадается и запакованным
            // в base64, как обычный список ссылок.
            if (XrayConfigParser.LooksLikeConfig(text))
                return XrayConfigParser.Parse(text);
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (ProxyUriParser.TryParse(line, out var server, out var error))
                servers.Add(server!);
            else
                errors.Add($"{Preview(line)}: {error}");
        }

        return (servers, errors);
    }

    /// <summary>
    /// Собирает метаданные подписки из заголовков ответа.
    /// </summary>
    /// <param name="userInfo">Заголовок <c>subscription-userinfo</c>.</param>
    /// <param name="title">Заголовок <c>profile-title</c>.</param>
    /// <param name="updateInterval">Заголовок <c>profile-update-interval</c>.</param>
    /// <param name="extra">
    /// Остальные заголовки поставщика по имени (<c>support-url</c>, <c>announce</c>…);
    /// <c>null</c> — их нет. Те же, что читает Happ (владелец 15.10: «делай»).
    /// </param>
    public static SubscriptionInfo BuildInfo(
        IReadOnlyList<ProxyServer> servers,
        IReadOnlyList<string> errors,
        string? userInfo,
        string? title,
        string? updateInterval,
        Func<string, string?>? extra = null)
    {
        var fields = ParseUserInfo(userInfo);

        long Field(string name) =>
            fields.TryGetValue(name, out var value) && long.TryParse(value, out var parsed) ? parsed : 0;

        long expire = Field("expire");

        return new SubscriptionInfo
        {
            Servers = servers,
            Errors = errors,
            Title = DecodeTitle(title),
            UploadBytes = Field("upload"),
            DownloadBytes = Field("download"),
            TotalBytes = Field("total"),
            ExpiresAt = expire > 0 ? DateTimeOffset.FromUnixTimeSeconds(expire) : null,
            UpdateIntervalHours = double.TryParse(updateInterval, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
                ? hours
                : null,
            SupportUrl = WebLink(extra?.Invoke("support-url")),
            WebPageUrl = WebLink(extra?.Invoke("profile-web-page-url")),
            Announce = DecodeAnnounce(extra?.Invoke("announce")),
            FallbackUrl = PanelLink(extra?.Invoke("fallback-url")),
            NewUrl = PanelLink(extra?.Invoke("new-url")),
        };
    }

    /// <summary>
    /// Адрес панели от самой панели — только https.
    /// </summary>
    /// <remarks>
    /// По нему уйдёт сама подписка, то есть пароль: по открытому http его
    /// прочитал бы любой на пути, а переносом подписки панель могла бы
    /// незаметно понизить защиту навсегда.
    /// </remarks>
    internal static string? PanelLink(string? value) =>
        WebLink(value) is { } link && link.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? link : null;

    /// <summary>Сколько знаков объявления показывать — столько же пускает Happ.</summary>
    public const int AnnounceLimit = 200;

    /// <summary>
    /// Ссылка от панели — только http или https.
    /// </summary>
    /// <remarks>
    /// Кнопки «Поддержка» и «Сайт» открывают её системой, а система откроет
    /// и <c>file:</c>, и <c>ms-settings:</c>, и путь к программе. Панель —
    /// чужой сервер, и запускать по её слову что-то, кроме страницы, нельзя.
    /// Ссылки Telegram у поставщиков — <c>https://t.me/…</c>, их это пропускает.
    /// </remarks>
    internal static string? WebLink(string? value)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text) || text.Length > 2048 || text.Any(char.IsWhiteSpace))
            return null;

        return Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && !string.IsNullOrEmpty(uri.Host)
                ? text
                : null;
    }

    /// <summary>
    /// Объявление: открытым текстом или с <c>base64:</c>, одной строкой и не длиннее <see cref="AnnounceLimit"/>.
    /// </summary>
    /// <remarks>
    /// Управляющие знаки вычищаются: строка встаёт в карточку подписки,
    /// и перевод строки или знак направления письма из чужого ответа
    /// разламывал бы её.
    /// </remarks>
    internal static string? DecodeAnnounce(string? value)
    {
        var text = DecodeTitle(value);

        if (text is null)
            return null;

        var clean = new string(text.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c).ToArray());
        clean = string.Join(' ', clean.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        if (clean.Length == 0)
            return null;

        return clean.Length > AnnounceLimit ? clean[..(AnnounceLimit - 1)] + "…" : clean;
    }

    /// <summary>
    /// Разбирает <c>upload=0; download=123; total=456; expire=1790693959</c>.
    /// </summary>
    private static Dictionary<string, string> ParseUserInfo(string? header)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(header))
            return result;

        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            if (equals < 0)
                continue;

            result[part[..equals].Trim()] = part[(equals + 1)..].Trim();
        }

        return result;
    }

    /// <summary>
    /// Название приходит либо открытым текстом, либо с префиксом <c>base64:</c>.
    /// </summary>
    private static string? DecodeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var text = title.Trim();

        const string prefix = "base64:";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return Base64.DecodeToString(text[prefix.Length..]) ?? text;

        return text;
    }

    /// <summary>
    /// Обрезает строку для сообщения об ошибке: в ней учётные данные,
    /// и печатать её целиком в лог не стоит.
    /// </summary>
    private static string Preview(string line)
    {
        int schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        var scheme = schemeEnd > 0 ? line[..schemeEnd] : "?";

        return line.Length <= 24 ? line : $"{scheme}://… ({line.Length} симв.)";
    }
}
