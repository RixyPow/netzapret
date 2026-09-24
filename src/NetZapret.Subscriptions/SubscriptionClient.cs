using System.Net;

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
/// <para>
/// Отказ под именем sing-box — повод спросить ещё раз под именем Happ.
/// Замер 24.09 на подписке пользователя, которую поддержка провайдера
/// назвала «работает только в Happ»: sing-box с номером устройства — 404,
/// Happ без номера — 404, Happ с номером — 200 и подписка на 175 КБ.
/// Первым остаётся sing-box: панели, что отвечают обоим, под ним отдают
/// список лучше (замер 11.09 выше).
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

    /// <summary>Каким именем назваться панели, если первым она отказала.</summary>
    public const string FallbackUserAgent = "Happ/3.4.0";

    private readonly string[] _userAgents;
    private readonly string _memory;

    /// <param name="memoryPath">Файл памяти имён (<see cref="AgentMemory"/>); <c>null</c> — обычный.</param>
    public SubscriptionClient(HttpClient? http = null, string userAgent = "sing-box/1.14.0", string? memoryPath = null)
    {
        _memory = memoryPath ?? AgentMemory.DefaultPath;

        _ownsClient = http is null;
        _http = http ?? new HttpClient { Timeout = DefaultTimeout };

        // Имя — на каждый запрос, а не в заголовки клиента: при отказе
        // спрашиваем ещё раз под другим.
        _userAgents = string.Equals(userAgent, FallbackUserAgent, StringComparison.OrdinalIgnoreCase)
            ? [userAgent]
            : [userAgent, FallbackUserAgent];

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

        HttpResponseMessage? response = null;

        try
        {
            // Запомненное имя — первым: подписка «только для Happ» иначе
            // начинала каждое чтение с заведомого 404 под именем sing-box.
            var remembered = AgentMemory.Get(target, _memory);
            var agents = remembered is not null && _userAgents.Contains(remembered)
                ? _userAgents.Where(a => a == remembered).Concat(_userAgents.Where(a => a != remembered)).ToArray()
                : _userAgents;

            string? used = null;

            foreach (var agent in agents)
            {
                used = agent;

                response?.Dispose();

                using var request = new HttpRequestMessage(HttpMethod.Get, target);

                if (!request.Headers.UserAgent.TryParseAdd(agent))
                    request.Headers.TryAddWithoutValidation("User-Agent", agent);

                response = await _http.SendAsync(request, cancellationToken);

                // Отказ «такого нет» или «не для тебя» — пробуем другое имя;
                // сбой самой панели (5xx) другим именем не лечится.
                if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized))
                    break;
            }

            response!.EnsureSuccessStatusCode();

            // Сработало не имя по умолчанию — запомнить; сработало оно —
            // забыть: панель могла снова начать отвечать обоим.
            AgentMemory.Set(target, used == _userAgents[0] ? null : used, _memory);
            return await ParseAsync(response, cancellationToken);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private static async Task<SubscriptionInfo> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
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
