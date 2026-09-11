using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NetZapret.Subscriptions;

/// <summary>
/// Заводит учётную запись Cloudflare WARP.
/// </summary>
/// <remarks>
/// <para>
/// Тот же порядок действий, что у официального клиента: отдать открытый ключ,
/// получить назначенные адреса и ключ узла, затем отдельным запросом включить
/// у записи режим WARP. Без второго шага запись существует, но туннель через
/// неё не поднимается — Cloudflare считает её просто зарегистрированным
/// устройством.
/// </para>
/// <para>
/// Ни почты, ни оплаты, ни согласия на что-либо, кроме условий использования,
/// которые клиент подтверждает отметкой времени. Ограничение у бесплатной
/// записи одно и оно не в трафике: скорость ниже, чем у WARP+.
/// </para>
/// <para>
/// <b>Домен закрыт у российских операторов.</b> Замер 2026-09-11: TCP до
/// 104.16.192.82:443 устанавливается, тот же адрес с посторонним SNI отдаёт
/// страницу, а с <c>api.cloudflareclient.com</c> рукопожатие не доходит вовсе.
/// Поэтому запрос идёт через <see cref="SplitTls"/> — с разрезанным
/// приветствием, — и обходится без туннеля. Это важнее, чем кажется:
/// запасной выход нужен тогда, когда обычные пути уже не работают.
/// </para>
/// <para>
/// Туннель при этом остаётся вторым путём: домен внесён в маршруты как
/// проксируемый (см. <c>NetZapret.Proxy.SingBoxConfigCompiler</c>), так что
/// при поднятом VPN запрос дойдёт и без разреза.
/// </para>
/// </remarks>
public sealed class WarpClient : IDisposable
{
    /// <summary>Версия протокола в пути; менялась вместе с клиентом Cloudflare.</summary>
    private const string Version = "v0a2158";

    /// <summary>
    /// Куда обращаться за записью — по порядку предпочтения.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Первым стоит не тот узел, который называют в документации. Замер
    /// 2026-09-11 на российском операторе: <c>api.cloudflareclient.com</c>
    /// закрыт по имени в TLS — TCP устанавливается, рукопожатие не доходит, —
    /// а <c>zero-trust-client.cloudflareclient.com</c> отвечает с первой
    /// попытки и обслуживает ровно тот же путь. Порядок отсюда: сперва
    /// работающий, а канонический остаётся запасным на случай, когда закроют
    /// и этот.
    /// </para>
    /// <para>
    /// Различие узкое и держится на том, что списки блокировок ведут
    /// по именам, а не по назначению узла. Продержится оно ровно до тех пор,
    /// пока кто-нибудь не дополнит список, поэтому перебор здесь не роскошь.
    /// </para>
    /// </remarks>
    private static readonly string[] Hosts =
    {
        "zero-trust-client.cloudflareclient.com",
        "api.cloudflareclient.com",
    };

    /// <summary>Домены, без которых запись не завести.</summary>
    /// <remarks>
    /// Вынесены сюда, чтобы сборка конфига брала их отсюда, а не повторяла
    /// строкой у себя: разъехавшись, эти два места дали бы раздел, который
    /// показывает кнопку и не может ею воспользоваться.
    /// </remarks>
    public static IReadOnlyList<string> ApiDomains { get; } = new[]
    {
        "api.cloudflareclient.com",
        "zero-trust-client.cloudflareclient.com",
        "engage.cloudflareclient.com",
    };

    /// <summary>Узел WARP по умолчанию — на случай, если ответ его не назовёт.</summary>
    private const string FallbackHost = "162.159.192.1";
    private const int FallbackPort = 2408;

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public WarpClient(HttpClient? http = null)
    {
        _ownsClient = http is null;

        _http = http ?? new HttpClient(SplitTls.Handler())
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        // Заголовки официального клиента. Посторонним Cloudflare отвечает
        // отказом — проверено на своём имени, как и у панелей подписок.
        if (!_http.DefaultRequestHeaders.Contains("CF-Client-Version"))
            _http.DefaultRequestHeaders.Add("CF-Client-Version", "a-6.30-3596");

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.TryParseAdd("okhttp/3.12.1");
    }

    /// <summary>
    /// Регистрирует пару ключей и включает у записи режим WARP.
    /// </summary>
    /// <param name="keys">Пара ключей WireGuard; закрытый останется у нас.</param>
    /// <exception cref="HttpRequestException">Если Cloudflare ответил отказом.</exception>
    /// <exception cref="InvalidOperationException">Если ответ не разобрался.</exception>
    public async Task<WarpAccount> RegisterAsync(WireGuardKeyPair keys, CancellationToken cancellationToken)
    {
        var request = new JsonObject
        {
            ["key"] = keys.PublicKey,
            ["install_id"] = string.Empty,
            ["fcm_token"] = string.Empty,

            // Отметка о принятии условий: клиент ставит своё время,
            // сервер её только сохраняет.
            ["tos"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["model"] = "PC",
            ["serial_number"] = string.Empty,
            ["locale"] = "en_US",
        };

        var (registration, host) = await RegisterAtAnyHostAsync(request, cancellationToken);

        var id = registration["id"]?.GetValue<string>();
        var token = registration["token"]?.GetValue<string>();

        // Второй запрос переводит запись в режим WARP. Его ответ полнее
        // первого, поэтому разбираем именно его, а первый оставляем
        // запасным на случай, что включение не прошло.
        JsonNode source = registration;

        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(token))
        {
            try
            {
                source = await PatchAsync(
                    $"https://{host}/{Version}/reg/{id}",
                    new JsonObject { ["warp_enabled"] = true },
                    token,
                    cancellationToken);
            }
            catch (Exception)
            {
                // Запись уже заведена, и терять её из-за неудачи на втором шаге
                // нельзя: повтор завёл бы вторую. Разбираем, что есть.
            }
        }

        return Parse(source, keys.PrivateKey, id, token);
    }

    /// <summary>
    /// Заводит запись на первом узле, который отзовётся.
    /// </summary>
    /// <remarks>
    /// Возвращает и узел: второй запрос обязан уйти туда же, где завели
    /// запись. Отправленный на соседний, он получил бы отказ — ключ доступа
    /// у записи общий, а вот до самой записи оттуда не дотянуться.
    /// </remarks>
    private async Task<(JsonNode Response, string Host)> RegisterAtAnyHostAsync(
        JsonObject request,
        CancellationToken cancellationToken)
    {
        Exception? last = null;

        foreach (var host in Hosts)
        {
            try
            {
                var response = await PostAsync(
                    $"https://{host}/{Version}/reg", request, null, cancellationToken);

                return (response, host);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new HttpRequestException("Cloudflare недоступен");
    }

    /// <summary>
    /// Собирает учётную запись из ответа Cloudflare.
    /// </summary>
    internal static WarpAccount Parse(JsonNode response, string privateKey, string? id, string? token)
    {
        var config = response["config"]
            ?? throw new InvalidOperationException("в ответе Cloudflare нет раздела config");

        var peer = config["peers"]?.AsArray().FirstOrDefault()
            ?? throw new InvalidOperationException("в ответе Cloudflare нет ни одного узла");

        var peerKey = peer["public_key"]?.GetValue<string>()
            ?? throw new InvalidOperationException("узел Cloudflare пришёл без открытого ключа");

        var addresses = config["interface"]?["addresses"];

        var v4 = addresses?["v4"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Cloudflare не выдал адрес IPv4");

        var (host, port) = Endpoint(peer["endpoint"]);

        return new WarpAccount
        {
            PrivateKey = privateKey,
            PeerPublicKey = peerKey,
            EndpointHost = host,
            EndpointPort = port,
            AddressV4 = v4,
            AddressV6 = addresses?["v6"]?.GetValue<string>(),
            ClientId = config["client_id"]?.GetValue<string>(),
            AccountId = id ?? response["id"]?.GetValue<string>(),
            Token = token ?? response["token"]?.GetValue<string>(),
            License = response["account"]?["license"]?.GetValue<string>(),
            RegisteredAt = DateTimeOffset.Now,
        };
    }

    /// <summary>
    /// Достаёт адрес и порт узла из раздела <c>endpoint</c>.
    /// </summary>
    /// <remarks>
    /// Поле <c>v4</c> приходит адресом с портом 0, а настоящий порт назван
    /// только в <c>host</c>, где вместо адреса стоит имя. Берём по половине
    /// от каждого: имя <c>engage.cloudflareclient.com</c> закрыто тем же
    /// оператором, что и api, и разрешать его при поднятии туннеля значило бы
    /// упереться в ту же стену.
    /// </remarks>
    private static (string Host, int Port) Endpoint(JsonNode? endpoint)
    {
        var host = FallbackHost;
        int port = FallbackPort;

        if (endpoint?["v4"]?.GetValue<string>() is { Length: > 0 } v4)
        {
            var (address, parsed) = SplitHostPort(v4);

            if (address.Length > 0)
                host = address;

            if (parsed > 0)
                port = parsed;
        }

        if (port == FallbackPort && endpoint?["host"]?.GetValue<string>() is { Length: > 0 } named)
        {
            var (_, parsed) = SplitHostPort(named);

            if (parsed > 0)
                port = parsed;
        }

        return (host, port);
    }

    private static (string Host, int Port) SplitHostPort(string value)
    {
        int colon = value.LastIndexOf(':');

        if (colon < 0)
            return (value, 0);

        var host = value[..colon].Trim('[', ']');

        return int.TryParse(value[(colon + 1)..], out int port) ? (host, port) : (host, 0);
    }

    private Task<JsonNode> PostAsync(
        string url,
        JsonObject body,
        string? token,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, url, body, token, cancellationToken);

    private Task<JsonNode> PatchAsync(
        string url,
        JsonObject body,
        string? token,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Patch, url, body, token, cancellationToken);

    /// <summary>
    /// Отправляет запрос, повторяя попытку при обрыве связи.
    /// </summary>
    /// <remarks>
    /// Повтор безопасен именно здесь: исключение из <c>SendAsync</c> означает,
    /// что ответа не было вовсе. Причина у обрыва обычно одна — после
    /// неудачного приветствия оператор перестаёт пускать к тому адресу, и вторая
    /// попытка уходит уже к другому, благо у Cloudflare их несколько.
    /// </remarks>
    private async Task<JsonNode> SendAsync(
        HttpMethod method,
        string url,
        JsonObject body,
        string? token,
        CancellationToken cancellationToken)
    {
        const int attempts = 3;

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await OnceAsync(method, url, body, token, cancellationToken);
            }
            // Повторяется только обрыв: у отказа с кодом состояния причина
            // не в связи, и три одинаковых «400» подряд — просто трата минуты.
            catch (Exception ex) when (attempt < attempts
                && !cancellationToken.IsCancellationRequested
                && ex is HttpRequestException { StatusCode: null } or IOException)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private async Task<JsonNode> OnceAsync(
        HttpMethod method,
        string url,
        JsonObject body,
        string? token,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(method, url) { Content = Json(body) };

        if (!string.IsNullOrEmpty(token))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _http.SendAsync(message, cancellationToken);

        var answer = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Тело ответа в жалобу не попадает: у неудачного PATCH там
            // лежит содержимое записи вместе с ключом доступа.
            throw new HttpRequestException(
                $"Cloudflare ответил {(int)response.StatusCode} {response.ReasonPhrase}",
                null,
                response.StatusCode);
        }

        return JsonNode.Parse(answer)
            ?? throw new InvalidOperationException("Cloudflare ответил пустотой");
    }

    private static StringContent Json(JsonObject body) =>
        new(body.ToJsonString(), Encoding.UTF8, "application/json");

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
