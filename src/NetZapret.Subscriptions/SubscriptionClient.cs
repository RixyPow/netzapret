namespace NetZapret.Subscriptions;

/// <summary>
/// Скачивает подписку по ссылке.
/// </summary>
/// <remarks>
/// <para>
/// Заголовок User-Agent имеет значение: многие панели отдают разный формат
/// в зависимости от клиента, а некоторые вовсе отказывают неизвестным.
/// </para>
/// <para>
/// Представляемся движком, который ведём. Замер 2026-09-11 на двух живых
/// подписках: под видом Happ одна панель отдаёт готовый конфиг Xray — без имён
/// серверов, с тегами proxy, proxy-2 и повторами, двадцать один вместо
/// двенадцати. Она же под видом sing-box отдаёт обычный список ссылок
/// с флагами и названиями стран. Вторая подписка на оба заголовка отвечает
/// одинаково, так что терять нечего.
/// </para>
/// <para>
/// Своё имя пробовать нельзя: на NetZapret/0.5.3 первая панель ответила
/// отказом. Незнакомых там не любят.
/// </para>
/// </remarks>
public sealed class SubscriptionClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    /// <summary>
    /// Сколько ждать панель подписки.
    /// </summary>
    /// <remarks>
    /// Было тридцать секунд, и это много для действия, на которое смотрят.
    /// Подписки читаются по очереди, и при трёх ссылках зависшая панель
    /// держала надпись «Читаю подписки…» полторы минуты без единого признака
    /// жизни. Хуже того, запрос уходит через туннель, и когда выход мёртв —
    /// а мёртвым он бывает ровно тогда, когда человек лезет за серверами, —
    /// ждать полную минуту не за чем: ответа не будет.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(12);

    public SubscriptionClient(HttpClient? http = null, string userAgent = "sing-box/1.14.0")
    {
        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = DefaultTimeout };

        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
            _http.DefaultRequestHeaders.Add("User-Agent", userAgent);

        // Номер устройства — всем панелям, а не только тем, что его просят:
        // заранее не узнать, какая с привязкой к устройству, а лишний заголовок
        // обычной панели ничего не стоит (DeviceIdentity).
        foreach (var (name, value) in DeviceIdentity.Headers())
            _http.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
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
