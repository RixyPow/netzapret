using System.Text.Json;

namespace NetZapret.Subscriptions;

/// <summary>
/// Достаёт серверы из готового конфига Xray.
/// </summary>
/// <remarks>
/// <para>
/// Часть поставщиков отдаёт по ссылке подписки не список <c>vless://</c>,
/// а целиком настроенный конфиг Xray — со своей маршрутизацией, балансировщиком
/// и списком отечественных доменов. Серверы там лежат в <c>outbounds</c>,
/// и ссылок на них нет вовсе.
/// </para>
/// <para>
/// Прежде такая подписка разбиралась в ноль серверов и одну строку ошибки
/// «неподдерживаемая схема», за которой стоял весь документ. Со стороны это
/// выглядело как мёртвая подписка, хотя квота и срок читались верно: заголовки
/// приходят отдельно от тела.
/// </para>
/// <para>
/// Берутся только исходящие соединения. <c>freedom</c> и <c>blackhole</c> —
/// это «напрямую» и «в никуда», сервером ни то, ни другое не является;
/// маршрутизация и балансировщик игнорируются намеренно: свои правила у нас
/// свои, и чужие поверх них означали бы два источника правды.
/// </para>
/// </remarks>
public static class XrayConfigParser
{
    /// <summary>Похоже ли тело на конфиг, а не на список ссылок.</summary>
    public static bool LooksLikeConfig(string body)
    {
        var text = body.TrimStart();

        return text.StartsWith('{') || text.StartsWith('[');
    }

    public static (IReadOnlyList<ProxyServer> Servers, IReadOnlyList<string> Errors) Parse(string body)
    {
        var servers = new List<ProxyServer>();
        var errors = new List<string>();

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            errors.Add("конфиг не разбирается как JSON: " + ex.Message);
            return (servers, errors);
        }

        using (document)
        {
            // Документ бывает и объектом, и массивом конфигов: поставщики
            // отдают то одно, то другое, а различие ни на что не влияет.
            var roots = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];

            foreach (var root in roots)
            {
                if (!root.TryGetProperty("outbounds", out var outbounds)
                    || outbounds.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var outbound in outbounds.EnumerateArray())
                {
                    if (TryRead(outbound, out var server, out var problem))
                        servers.Add(server!);
                    else if (problem is not null)
                        errors.Add(problem);
                }
            }
        }

        if (servers.Count == 0 && errors.Count == 0)
            errors.Add("в конфиге нет ни одного сервера: раздел outbounds пуст или состоит из direct и block");

        return (servers, errors);
    }

    private static bool TryRead(JsonElement outbound, out ProxyServer? server, out string? problem)
    {
        server = null;
        problem = null;

        var protocol = Text(outbound, "protocol")?.ToLowerInvariant();

        if (protocol is null or "freedom" or "blackhole" or "dns" or "loopback")
            return false;

        var kind = protocol switch
        {
            "vless" => ProxyProtocol.Vless,
            "vmess" => ProxyProtocol.Vmess,
            "trojan" => ProxyProtocol.Trojan,
            "shadowsocks" => ProxyProtocol.Shadowsocks,
            _ => (ProxyProtocol?)null,
        };

        if (kind is null)
        {
            problem = $"outbound «{Text(outbound, "tag") ?? protocol}»: протокол {protocol} не поддерживается";
            return false;
        }

        if (!outbound.TryGetProperty("settings", out var settings))
            return false;

        string? host = null;
        ushort port = 0;
        string? credential = null;
        string? flow = null;
        string? method = null;

        // vless и vmess держат узлы в vnext, trojan и shadowsocks — в servers.
        // Различие историческое и смысла не несёт.
        if (settings.TryGetProperty("vnext", out var vnext) && vnext.ValueKind == JsonValueKind.Array
            && vnext.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } node)
        {
            host = Text(node, "address");
            port = Port(node);

            if (node.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array
                && users.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } user)
            {
                credential = Text(user, "id");
                flow = Empty(Text(user, "flow"));
            }
        }
        else if (settings.TryGetProperty("servers", out var list) && list.ValueKind == JsonValueKind.Array
            && list.EnumerateArray().FirstOrDefault() is { ValueKind: JsonValueKind.Object } entry)
        {
            host = Text(entry, "address");
            port = Port(entry);
            credential = Text(entry, "password");
            method = Empty(Text(entry, "method"));
        }

        if (string.IsNullOrWhiteSpace(host) || port == 0 || string.IsNullOrWhiteSpace(credential))
        {
            problem = $"outbound «{Text(outbound, "tag") ?? protocol}»: не хватает адреса, порта или ключа";
            return false;
        }

        var stream = outbound.TryGetProperty("streamSettings", out var s) ? s : default;

        var transport = Empty(Text(stream, "network")) ?? "tcp";
        var security = Empty(Text(stream, "security")) ?? "none";

        string? sni = null;
        string? fingerprint = null;
        string? publicKey = null;
        string? shortId = null;
        var alpn = Array.Empty<string>().ToList();

        var tls = security == "reality"
            ? Section(stream, "realitySettings")
            : Section(stream, "tlsSettings");

        if (tls.ValueKind == JsonValueKind.Object)
        {
            sni = Empty(Text(tls, "serverName"));
            fingerprint = Empty(Text(tls, "fingerprint"));
            publicKey = Empty(Text(tls, "publicKey"));
            shortId = Empty(Text(tls, "shortId"));

            if (tls.TryGetProperty("alpn", out var names) && names.ValueKind == JsonValueKind.Array)
                alpn = names.EnumerateArray().Select(a => a.GetString() ?? string.Empty).Where(a => a.Length > 0).ToList();
        }

        var (path, hostHeader, serviceName) = Stream(stream, transport);

        server = new ProxyServer
        {
            Protocol = kind.Value,

            // Тег конфига — единственное имя, какое есть. Названий стран
            // в таком виде подписки не бывает: поставщик их не пишет, потому
            // что клиент показывает свой список, а не этот.
            Tag = Empty(Text(outbound, "tag")) ?? $"{protocol} {host}",
            Host = host!,
            Port = port,
            Credential = credential!,
            Transport = transport,
            Security = security,
            Sni = sni,
            Fingerprint = fingerprint,
            Alpn = alpn,
            Flow = flow,
            RealityPublicKey = publicKey,
            RealityShortId = shortId,
            Path = path,
            HostHeader = hostHeader,
            ServiceName = serviceName,
            Method = method,
        };

        return true;
    }

    private static (string? Path, string? Host, string? Service) Stream(JsonElement stream, string transport)
    {
        switch (transport)
        {
            case "ws":
            {
                var section = Section(stream, "wsSettings");
                return (Empty(Text(section, "path")), Header(section), null);
            }

            case "xhttp":
            {
                var section = Section(stream, "xhttpSettings");
                return (Empty(Text(section, "path")), Empty(Text(section, "host")), null);
            }

            case "httpupgrade":
            {
                var section = Section(stream, "httpupgradeSettings");
                return (Empty(Text(section, "path")), Empty(Text(section, "host")), null);
            }

            case "grpc":
            {
                var section = Section(stream, "grpcSettings");
                return (null, null, Empty(Text(section, "serviceName")));
            }

            default:
                return (null, null, null);
        }
    }

    /// <summary>Заголовок Host лежит в headers, а не рядом с путём.</summary>
    private static string? Header(JsonElement section) =>
        section.ValueKind == JsonValueKind.Object
            && section.TryGetProperty("headers", out var headers)
            && headers.ValueKind == JsonValueKind.Object
                ? Empty(Text(headers, "Host"))
                : null;

    private static JsonElement Section(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var section)
            ? section
            : default;

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    /// <summary>Порт бывает и числом, и строкой — поставщики пишут по-разному.</summary>
    private static ushort Port(JsonElement parent)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty("port", out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt16(out var number) => number,
            JsonValueKind.String when ushort.TryParse(value.GetString(), out var text) => text,
            _ => 0,
        };
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
