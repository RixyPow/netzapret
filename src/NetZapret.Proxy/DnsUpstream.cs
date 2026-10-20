using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>
/// Резолвер движка (<c>remote</c> в конфиге) — для проверки прямого пути к нему.
/// </summary>
/// <remarks>
/// <para>
/// Нужен DNS «авто» (19.10, владелец: «„авто“ напрямую с уходом в туннель только
/// при отказе прямого DoH»). Прежде «авто» уводило запросы в туннель, как только
/// туннель пропускал трафик, и сайты мимо VPN получали адреса рядом с выходом:
/// замер 06.10 — TLS 141–151 мс против 65–73 от прямого DoH.
/// </para>
/// <para>
/// Спрашивается тот резолвер, что в конфиге, а не тот, что в настройках: движок
/// собран с ним, и мерить другой значило бы решать за движок по чужому замеру.
/// Запрос идёт через физический адаптер (<see cref="DnsSurvey.PhysicalInterface"/>),
/// мимо TUN, — тем же путём, каким его делает сам движок при «напрямую».
/// </para>
/// </remarks>
public sealed record DnsUpstream(string Type, string Server, string? Name, string? Path)
{
    /// <summary>
    /// В конфиге есть ступень «по TCP» (<see cref="SingBoxOptions.DnsTcpTag"/>):
    /// при отказе прямого DoH надзор сперва пробует её, и лишь потом туннель.
    /// </summary>
    public bool TcpFallback { get; init; }

    /// <summary>Резолвер <c>remote</c> из конфига; <c>null</c> — конфига нет или резолвера в нём нет.</summary>
    public static DnsUpstream? FromConfig(string configPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));

            if (!document.RootElement.TryGetProperty("dns", out var dns)
                || !dns.TryGetProperty("servers", out var servers))
            {
                return null;
            }

            DnsUpstream? remote = null;
            bool tcp = false;

            foreach (var server in servers.EnumerateArray())
            {
                if (Text(server, "tag") == SingBoxOptions.DnsTcpTag)
                    tcp = true;

                if (Text(server, "tag") != "remote" || Text(server, "server") is not { Length: > 0 } address)
                    continue;

                string? name = server.TryGetProperty("tls", out var tls) ? Text(tls, "server_name") : null;

                remote = new DnsUpstream(Text(server, "type") ?? "udp", address, name, Text(server, "path"));
            }

            return remote is null ? null : remote with { TcpFallback = tcp };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Отвечает ли резолвер напрямую, мимо туннеля.</summary>
    /// <remarks>
    /// Тип, которого проверка не знает, считается отвечающим: тогда «авто» остаётся
    /// «напрямую», как собран конфиг, — а не уходит в туннель по незнанию.
    /// </remarks>
    public async Task<bool> AnswersDirectlyAsync(CancellationToken cancellationToken)
    {
        var nic = DnsSurvey.PhysicalInterface();
        var provider = new DnsProvider("движок", [Server], Server, Name ?? Server, Path ?? "/dns-query");

        switch (Type)
        {
            case "https":
                return (await DnsSurvey.DohAsync(provider, nic, cancellationToken)).Ms is not null;

            case "tls":
                return (await DnsSurvey.DotAsync(provider, nic, cancellationToken)).Ms is not null;

            case "udp":
                return (await DnsSurvey.UdpAsync(Server, "example.com", DnsWire.TypeA, nic, cancellationToken)).Answer is not null;

            default:
                return true;
        }
    }

    /// <summary>
    /// Отвечает ли тот же резолвер по TCP, порт 53, честно — закрытое имя
    /// с адресами, а не отказом.
    /// </summary>
    /// <remarks>
    /// Имя закрытое нарочно (<see cref="DnsSurvey.SpoofProbes"/>): заворот
    /// на НСДИ отдаёт на него NXDOMAIN, и такой «ответ» ступени не годится —
    /// ради закрытых имён она и нужна. Замер 20.10 у владельца: TCP к 8.8.4.4
    /// и 1.1.1.1 — rutracker.org с адресами за 11–59 мс, UDP — отказ.
    /// </remarks>
    public async Task<bool> AnswersByTcpAsync(CancellationToken cancellationToken)
    {
        if (!TcpFallback)
            return false;

        var answer = await DnsSurvey.TcpAsync(Server, DnsSurvey.SpoofProbes[0], DnsWire.TypeA, DnsSurvey.PhysicalInterface(), cancellationToken);

        return answer is { Code: 0, Addresses.Count: > 0 };
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
