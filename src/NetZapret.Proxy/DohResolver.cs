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
    /// <remarks>
    /// Порядок не случаен. Обращение по имени идёт первым, хотя выглядит
    /// хуже: имя надо сперва разрешить. Зато адрес <c>1.1.1.1</c> при
    /// работающих движках перехватываем мы сами — DoH к системным резолверам
    /// закрыт внутри туннеля, — и запрос уходит к тому, кто отвечает петлёй.
    /// Именно так в файл однажды попал адрес, который молчит.
    /// </remarks>
    private static readonly string[] Endpoints =
    [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.quad9.net:5053/dns-query",
        "https://1.1.1.1/dns-query",
        "https://8.8.8.8/resolve",
    ];

    /// <summary>Ответ-заглушка: петля или нулевой адрес.</summary>
    /// <remarks>
    /// Такой ответ — это и есть та неисправность, ради которой сюда пришли,
    /// и принимать его за адрес нельзя.
    /// </remarks>
    private static bool IsStub(string address) =>
        address.StartsWith("127.", StringComparison.Ordinal) || address == "0.0.0.0";

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

                // Заглушка — не ответ, а та самая неисправность. Прежде первый
                // же успех принимался за истину, и когда движок закрывал DoH
                // к 1.1.1.1, опрос доходил до Google — того самого резолвера,
                // который отдаёт по image.tmdb.org петлю. Проверка честно
                // отбрасывала её и сообщала «набор не покрывает ни одного
                // имени», хотя честный ответ существовал этажом выше.
                if (FirstAddress(json) is { } address && !IsStub(address))
                    return address;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Один резолвер не отвечает — спрашиваем следующего.
            }
        }

        return null;
    }

    /// <summary>
    /// Все адреса имени, какие удалось собрать у разных резолверов.
    /// </summary>
    /// <remarks>
    /// Перебор нужен потому, что «честный ответ» — не одно число. Сеть
    /// доставки держит десятки шардов, часть их отвечает, часть нет, и какой
    /// достанется — зависит от того, кого спросили. Ровно так нашёлся рабочий
    /// адрес для <c>image.tmdb.org</c>: один резолвер отдал петлю, другой —
    /// шард, который вернул постер с кодом 200.
    /// </remarks>
    public static async Task<IReadOnlyList<string>> CandidatesAsync(
        string host,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
            return [literal.ToString()];

        var found = new List<string>();

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

                foreach (var address in AllAddresses(json))
                {
                    if (!IsStub(address) && !found.Contains(address, StringComparer.Ordinal))
                        found.Add(address);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return found;
    }

    /// <summary>Все записи A из ответа.</summary>
    internal static IEnumerable<string> AllAddresses(string json)
    {
        JsonDocument document;

        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { yield break; }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("Answer", out var answers)
                || answers.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var answer in answers.EnumerateArray())
            {
                if (answer.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.Number
                    && type.GetInt32() == 1
                    && answer.TryGetProperty("data", out var data)
                    && IPAddress.TryParse(data.GetString(), out var address))
                {
                    yield return address.ToString();
                }
            }
        }
    }

    /// <summary>
    /// Отвечает ли адрес за это имя на самом деле.
    /// </summary>
    /// <remarks>
    /// Порт мало что доказывает: у сетей доставки он открыт и у шардов,
    /// которые ничего не отдают. Проверяется рукопожатие с нужным именем
    /// и ответ на запрос — только это отличает рабочий адрес от живого.
    /// </remarks>
    public static async Task<bool> ServesAsync(
        string address,
        string host,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(6));

            await client.ConnectAsync(address, 443, budget.Token);

            using var ssl = new System.Net.Security.SslStream(
                client.GetStream(), leaveInnerStreamOpen: false, (_, _, _, _) => true);

            await ssl.AuthenticateAsClientAsync(
                new System.Net.Security.SslClientAuthenticationOptions { TargetHost = host },
                budget.Token);

            var request = System.Text.Encoding.ASCII.GetBytes(
                $"HEAD / HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");

            await ssl.WriteAsync(request, budget.Token);

            var buffer = new byte[128];
            int read = await ssl.ReadAsync(buffer, budget.Token);

            return read > 0
                && System.Text.Encoding.ASCII.GetString(buffer, 0, read).StartsWith("HTTP/", StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
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
