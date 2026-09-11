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

    public ClashApi(string listen = "127.0.0.1:9090", HttpClient? http = null)
    {
        Address = listen;
        _ownsClient = http is null;

        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
    }

    public string Address { get; }

    private string Root => $"http://{Address}";

    /// <summary>Отзывается ли движок вообще.</summary>
    public async Task<bool> AliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"{Root}/version", cancellationToken);

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

            using var response = await _http.GetAsync(query, cancellationToken);

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
            var body = new StringContent(
                new JsonObject { ["name"] = tag }.ToJsonString(),
                Encoding.UTF8,
                "application/json");

            using var response = await _http.PutAsync(
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
            using var response = await _http.GetAsync(
                $"{Root}/proxies/{Uri.EscapeDataString(group)}", cancellationToken);

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
