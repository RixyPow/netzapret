namespace NetZapret.Subscriptions;

/// <summary>
/// Скачивает подписку по ссылке.
/// </summary>
/// <remarks>
/// Заголовок User-Agent имеет значение: многие панели отдают разный формат
/// в зависимости от клиента, а некоторые вовсе отказывают неизвестным.
/// </remarks>
public sealed class SubscriptionClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public SubscriptionClient(HttpClient? http = null, string userAgent = "Happ/2.18.3")
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
            _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
    }

    /// <summary>
    /// Скачивает и разбирает подписку.
    /// </summary>
    /// <exception cref="HttpRequestException">Если сервер вернул ошибку.</exception>
    public async Task<SubscriptionInfo> FetchAsync(Uri subscriptionUrl, CancellationToken cancellationToken)
    {
        // Ссылки вида happ://add/https://... — обёртка клиента вокруг обычного URL.
        var target = Unwrap(subscriptionUrl);

        using var response = await _http.GetAsync(target, cancellationToken);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var (servers, errors) = SubscriptionParser.ParseBody(body);

        return SubscriptionParser.BuildInfo(
            servers,
            errors,
            Header(response, "subscription-userinfo"),
            Header(response, "profile-title"),
            Header(response, "profile-update-interval"));
    }

    /// <summary>
    /// Разворачивает ссылку-обёртку клиента в обычный HTTP-адрес.
    /// </summary>
    public static Uri Unwrap(Uri url)
    {
        var text = url.ToString();

        foreach (var prefix in new[] { "happ://add/", "clash://install-config?url=", "sn://subscription?url=" })
        {
            if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var inner = text[prefix.Length..];
            int amp = inner.IndexOf('&');
            if (amp >= 0)
                inner = inner[..amp];

            return new Uri(Uri.UnescapeDataString(inner));
        }

        return url;
    }

    private static string? Header(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
            return values.FirstOrDefault();

        if (response.Content.Headers.TryGetValues(name, out var contentValues))
            return contentValues.FirstOrDefault();

        return null;
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
