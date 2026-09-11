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

    public SubscriptionClient(HttpClient? http = null, string userAgent = "sing-box/1.14.0")
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
        // WARP — подписка без подписки: скачивать нечего, сервер собирается
        // из учётной записи, лежащей рядом с настройками. Сделан отдельной
        // строкой в списке намеренно: так его видно, он выключается и
        // выбирается теми же кнопками, что и остальные.
        if (IsWarp(subscriptionUrl))
            return Warp();

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

    /// <summary>Ссылка, под которой в списке подписок живёт WARP.</summary>
    public const string WarpUrl = "warp://free";

    /// <summary>Ведёт ли ссылка к учётной записи WARP, а не в сеть.</summary>
    public static bool IsWarp(Uri url) =>
        url.Scheme.Equals("warp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Собирает «подписку» из учётной записи WARP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Выходов два, и они разные не страной, а транспортом: WireGuard
    /// на ключах, которые заводим мы, и MASQUE поверх QUIC, где запись движок
    /// заводит себе сам. Второй показывается всегда — ему от нас ключей
    /// не нужно; первый появляется, когда нажали «Подключить».
    /// </para>
    /// <para>
    /// Показаны оба намеренно, а не выбраны за человека. Который из них
    /// пройдёт, зависит от оператора, и узнаётся это только пробой: где
    /// WireGuard глохнет после рукопожатия, MASQUE может пройти, и наоборот.
    /// Автоподбор при этом разберётся сам — он опрашивает обоих.
    /// </para>
    /// </remarks>
    private static SubscriptionInfo Warp()
    {
        var account = WarpAccount.Load();
        var servers = new List<ProxyServer>();

        if (account is not null)
            servers.Add(account.ToServer());

        servers.Add(WarpAccount.MasqueServer());

        return new SubscriptionInfo
        {
            Servers = servers,
            Title = "Cloudflare WARP",

            Errors = account is null
                ? new[] { "ключи WireGuard не заведены — остаётся только MASQUE" }
                : Array.Empty<string>(),
        };
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
