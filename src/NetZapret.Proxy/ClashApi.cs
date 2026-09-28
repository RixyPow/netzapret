using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace NetZapret.Proxy;

/// <summary>
/// Разговор с работающим движком через его Clash API.
/// </summary>
/// <remarks>
/// <para>
/// Нужен там, где пробник бессилен. Тот поднимает свой экземпляр sing-box
/// и проверяет сервер в одиночку — способ честный, но применимый не ко всем:
/// у MASQUE учётная запись лежит в кэше работающего движка, а файл кэша занят
/// им же. Пробник обязан регистрироваться заново, и ему для этого не через что
/// дозвониться. Отсюда подпись «только в работе», которая ничего не измеряла
/// и выглядела отговоркой.
/// </para>
/// <para>
/// Движок, однако, умеет замерить свой выход сам — и именно тот, который
/// поднят и работает. Это лучше пробника, а не хуже: меряется то, через что
/// пойдёт трафик, а не его копия.
/// </para>
/// <para>
/// Доступен только пока движок работает. Остановлен — здесь ничего нет,
/// и это не сбой, а отсутствие собеседника.
/// </para>
/// </remarks>
public sealed class ClashApi : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly EngineKeys? _keys;

    /// <param name="keys">
    /// Пароли движка; <c>null</c> — взять из конфига работающего движка
    /// (<see cref="EngineKeys.Current"/>). С 27.09 Clash API под секретом,
    /// и запрос без него получает 401.
    /// </param>
    public ClashApi(string listen = "127.0.0.1:9090", HttpClient? http = null, EngineKeys? keys = null)
    {
        Address = listen;
        _ownsClient = http is null;
        _keys = keys ?? EngineKeys.Current();

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
    }

    public string Address { get; }

    private string Root => $"http://{Address}";

    /// <summary>
    /// Отправляет запрос с секретом. Заголовок — на запрос, а не на клиент:
    /// клиент у сторожа общий на все разговоры с движком.
    /// </summary>
    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        EngineKeys.Authorize(request, _keys);
        return _http.SendAsync(request, cancellationToken);
    }

    /// <summary>Отзывается ли движок вообще.</summary>
    public async Task<bool> AliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, $"{Root}/version", null, cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Просит движок замерить свой выход; <c>null</c> — не отозвался.
    /// </summary>
    /// <param name="tag">Тег выхода ровно как в конфиге.</param>
    public async Task<TimeSpan?> MeasureAsync(
        string tag,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = $"{Root}/proxies/{Uri.EscapeDataString(tag)}/delay"
                + $"?timeout={(int)timeout.TotalMilliseconds}"
                + $"&url={Uri.EscapeDataString(url)}";

            using var response = await SendAsync(HttpMethod.Get, query, null, cancellationToken);

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // Неудачный замер приходит с кодом ошибки и телом {"message":"Timeout"}.
            // Отличать его от отказа сети не нужно: и то и другое означает
            // «через этот выход не прошло».
            if (!response.IsSuccessStatusCode)
                return null;

            return JsonNode.Parse(body)?["delay"]?.GetValue<int>() is { } ms
                ? TimeSpan.FromMilliseconds(ms)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Из каких выходов состоит группа; <c>null</c> — движок не отозвался.</summary>
    /// <remarks>
    /// Замер всей группы одним запросом (/group/…/delay) здесь нарочно
    /// не используется: движок бьёт по всем выходам разом. 28.09 я так
    /// замерил селектор из 42 выходов, 26 из них — один вход Trust с одним
    /// ключом, и владелец увидел, что Trust лёг. Меряем по одному, с потолком
    /// на вход (<see cref="MeasureGentlyAsync"/>).
    /// </remarks>
    public async Task<IReadOnlyList<string>?> MembersAsync(string group, CancellationToken cancellationToken)
    {
        try
        {
            using var info = await SendAsync(
                HttpMethod.Get, $"{Root}/proxies/{Uri.EscapeDataString(group)}", null, cancellationToken);

            if (!info.IsSuccessStatusCode)
                return null;

            return (JsonNode.Parse(await info.Content.ReadAsStringAsync(cancellationToken))?["all"] as JsonArray)?
                .Select(n => n?.GetValue<string>())
                .OfType<string>()
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Меряет выходы руками движка, бережно к продавцам.
    /// </summary>
    /// <remarks>
    /// Не больше <paramref name="perEntry"/> проверок разом на один вход
    /// (адрес и порт сервера) и не больше <paramref name="total"/> всего.
    /// По умолчанию на вход — строго по одному (владелец 28.09: «поочерёдная
    /// проверка была бы логичнее»); разные продавцы друг другу не мешают.
    /// У многих продавцов ограничено число одновременных подключений на ключ,
    /// а у Trust все страны сидят на одном входе с одним ключом — залп
    /// из двух десятков проверок для него выглядит как злоупотребление.
    /// Процесса на сервер, как у пробника, при этом не нужно — это и есть
    /// выигрыш во времени.
    /// </remarks>
    /// <param name="servers">Тег выхода и вход, к которому он подключается.</param>
    public async Task MeasureGentlyAsync(
        IReadOnlyList<(string Tag, string Entry)> servers,
        string url,
        TimeSpan timeout,
        Action<string, TimeSpan?> onResult,
        CancellationToken cancellationToken,
        int perEntry = 1,
        int total = 8)
    {
        using var all = new SemaphoreSlim(total);

        var byEntry = servers
            .GroupBy(s => s.Entry, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, _ => new SemaphoreSlim(perEntry), StringComparer.OrdinalIgnoreCase);

        try
        {
            await Task.WhenAll(servers.Select(async server =>
            {
                var entry = byEntry[server.Entry];

                await entry.WaitAsync(cancellationToken);

                try
                {
                    await all.WaitAsync(cancellationToken);

                    try
                    {
                        onResult(server.Tag, await MeasureAsync(server.Tag, url, timeout, cancellationToken));
                    }
                    finally
                    {
                        all.Release();
                    }
                }
                finally
                {
                    entry.Release();
                }
            }));
        }
        finally
        {
            foreach (var semaphore in byEntry.Values)
                semaphore.Dispose();
        }
    }

    /// <summary>
    /// Переключает группу на указанный выход.
    /// </summary>
    /// <remarks>
    /// Мгновенно и без перезапуска: это и есть способ попробовать выход,
    /// не трогая настроек. Правка <c>PreferredServer</c> требует пересборки
    /// конфига и перезапуска движков, то есть обрыва всего трафика на полминуты
    /// — цена, несоразмерная слову «попробовать».
    /// </remarks>
    public async Task<bool> SelectAsync(string group, string tag, CancellationToken cancellationToken)
    {
        try
        {
            // Байтами, а не строкой с кодировкой. StringContent дописывает
            // к типу «; charset=utf-8», и Clash API движка отвечал на это
            // кодом 400 — при совершенно верном теле. Ловилось на тегах
            // с флагами стран: «🇫🇮 Финляндия» не выбирался вовсе, а причина
            // выглядела как «сервер не existует».
            var body = new ByteArrayContent(Encoding.UTF8.GetBytes(
                new JsonObject { ["name"] = tag }.ToJsonString()));

            body.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            using var response = await SendAsync(HttpMethod.Put,
                $"{Root}/proxies/{Uri.EscapeDataString(group)}", body, cancellationToken);

            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>На что сейчас указывает группа; <c>null</c> — не выяснилось.</summary>
    public async Task<string?> SelectedAsync(string group, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"{Root}/proxies/{Uri.EscapeDataString(group)}", null, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return JsonNode.Parse(body)?["now"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}