using System.Net;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>
/// Разрешает имена через DNS поверх HTTPS.
/// </summary>
/// <remarks>
/// <para>
/// Нужен там, где обычным DNS пользоваться нельзя, — а при работающем движке
/// это вся система. TUN перехватывает запросы к любому резолверу, включая
/// названный явно: замер показал, что <c>8.8.8.8</c> отвечает адресом
/// из диапазона fakeip, то есть отвечает не он.
/// </para>
/// <para>
/// Обращение идёт по адресу, а не по имени: резолвер, которому надо сперва
/// разрешить собственное имя, бесполезен ровно в тех случаях, ради которых
/// он здесь и заведён.
/// </para>
/// </remarks>
public static class DohResolver
{
    /// <summary>Резолверы по адресу; спрашиваются по очереди.</summary>
    private static readonly string[] Endpoints =
    [
        "https://1.1.1.1/dns-query",
        "https://8.8.8.8/resolve",
    ];

    /// <summary>
    /// Первый адрес IPv4 для имени; <c>null</c> — не выяснилось.
    /// </summary>
    /// <remarks>
    /// Имя, уже являющееся адресом, возвращается как есть: спрашивать о нём
    /// нечего, а неудача резолвера не должна лишать нас того, что и так известно.
    /// </remarks>
    public static async Task<string?> ResolveAsync(
        string host,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return literal.ToString();

        foreach (var endpoint in Endpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A");

                request.Headers.Accept.Add(new("application/dns-json"));

                using var response = await http.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                    continue;

                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                if (FirstAddress(json) is { } address)
                    return address;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Один резолвер не отвечает — спрашиваем следующего.
            }
        }

        return null;
    }

    /// <summary>Первая запись A из ответа; тип 1 по RFC 1035.</summary>
    internal static string? FirstAddress(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("Answer", out var answers)
                || answers.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var answer in answers.EnumerateArray())
            {
                // Ответ бывает цепочкой из CNAME и лишь в конце — адресом.
                // Берём первую запись типа A, а не первую вообще: иначе
                // в конфиг уехало бы имя вместо адреса.
                if (answer.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.Number
                    && type.GetInt32() == 1
                    && answer.TryGetProperty("data", out var data)
                    && IPAddress.TryParse(data.GetString(), out var address))
                {
                    return address.ToString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
