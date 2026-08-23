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
    public static SubscriptionInfo BuildInfo(
        IReadOnlyList<ProxyServer> servers,
        IReadOnlyList<string> errors,
        string? userInfo,
        string? title,
        string? updateInterval)
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
        };
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
