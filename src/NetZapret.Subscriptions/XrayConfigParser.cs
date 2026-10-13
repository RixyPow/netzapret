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

                var fromThis = new List<ProxyServer>();

                foreach (var outbound in outbounds.EnumerateArray())
                {
                    // Конфиг sing-box устроен так же — outbounds в корне, —
                    // но сервер описан другими полями. Панель отдаёт его
                    // в ответ на наш заголовок sing-box/1.14.0 (issue #3).
                    var read = SingBoxOutboundReader.Is(outbound)
                        ? SingBoxOutboundReader.TryRead(outbound, out var server, out var problem)
                        : TryRead(outbound, out server, out problem);

                    if (read)
                        fromThis.Add(server!);
                    else if (problem is not null)
                        errors.Add(problem);
                }

                // Имя — из подписи конфига (remarks), а не из внутреннего тега
                // выхода. Тег у всех один — «proxy», «proxy-2», — и подписка
                // Trust (28.09) выходила списком из «proxy» без стран, а движок
                // дописывал им «#22». Подпись и есть то, что показывает Happ:
                // «🇩🇪 Германия 🚀». 
                //
                // Один конфиг — один сервер, как в Happ (владелец, 28.09: «в Happ
                // они норм выглядят»). Остальные выходы конфига — запасные
                // для его балансира: Happ их не показывает, а у нас запасом
                // служит сам пул. Показанные строкой каждый, они превращали
                // 48 серверов Trust в 123 и замедляли запуск движка.
                if (Text(root, "remarks") is { Length: > 0 } remarks && fromThis.Count > 0)
                {
                    servers.Add(fromThis[0] with { Tag = remarks });
                    continue;
                }

                servers.AddRange(fromThis);
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

        // Hysteria2 в конфиге Xray — не отдельное имя протокола, а hysteria
        // версии 2: адрес в settings, пароль в hysteriaSettings.auth.
        // autoXRAY отдаёт его так, а не ссылкой hy2:// (13.10).
        if (protocol is "hysteria" or "hysteria2")
            return TryReadHysteria(outbound, protocol, out server, out problem);

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

        // Xray переименовал tcp в raw. У sing-box такого транспорта нет:
        // отсутствие секции и есть обычный TCP.
        var transport = Empty(Text(stream, "network")) ?? "tcp";
        if (transport.Equals("raw", StringComparison.OrdinalIgnoreCase))
            transport = "tcp";

        var security = (Empty(Text(stream, "security")) ?? "none").ToLowerInvariant();

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
            // Старые конфиги пишут publicKey. Xray с 2025 и autoXRAY кладут
            // тот же открытый ключ в password: xray x25519 печатает его так.
            // Пустой ключ sing-box отвергает («invalid public_key») и не
            // поднимает службу целиком.
            publicKey = Empty(Text(tls, "publicKey"));
            if (publicKey is null && security == "reality")
                publicKey = Empty(Text(tls, "password"));
            shortId = Empty(Text(tls, "shortId"));

            if (tls.TryGetProperty("alpn", out var names) && names.ValueKind == JsonValueKind.Array)
                alpn = names.EnumerateArray().Select(a => a.GetString() ?? string.Empty).Where(a => a.Length > 0).ToList();
        }

        var (path, hostHeader, serviceName) = Stream(stream, transport);

        var xhttp = transport == "xhttp" ? Section(stream, "xhttpSettings") : default;

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
            AllowInsecure = Flag(tls, "allowInsecure") || Flag(tls, "insecure"),
            RealityPublicKey = publicKey,
            RealityShortId = shortId,
            Path = path,
            HostHeader = hostHeader,
            ServiceName = serviceName,
            XhttpMode = XhttpSettings.Mode(Empty(Text(xhttp, "mode"))),
            XhttpOptions = XhttpSettings.FromXraySection(xhttp),
            Method = method,
        };

        return true;
    }

    /// <summary>
    /// Исходящее Hysteria из конфига Xray — это Hysteria2: другой версии
    /// у Xray нет (в документации: «version: must be 2»).
    /// </summary>
    /// <remarks>
    /// Адрес бывает и полем <c>settings.address</c> (autoXRAY, v2rayN),
    /// и в <c>settings.servers</c>. Пароль — в <c>hysteriaSettings.auth</c>.
    /// <c>finalmask</c> целиком в sing-box не переносится: незнакомое поле
    /// валит весь конфиг. Из него берётся только salamander — без пароля
    /// обфускации сервер молчит. Отпечаток uTLS тоже не берётся: это
    /// подделка ClientHello у TCP, а Hysteria2 идёт по QUIC.
    /// </remarks>
    private static bool TryReadHysteria(JsonElement outbound, string protocol, out ProxyServer? server, out string? problem)
    {
        server = null;
        problem = null;

        var settings = Section(outbound, "settings");
        var stream = Section(outbound, "streamSettings");
        var hysteria = Section(stream, "hysteriaSettings");
        var tag = Text(outbound, "tag") ?? protocol;

        int version = Number(hysteria, "version");
        if (version == 0)
            version = Number(settings, "version");

        if (protocol == "hysteria" && version is not (0 or 2))
        {
            problem = $"outbound «{tag}»: Hysteria версии {version} движок из конфига Xray не собирает";
            return false;
        }

        var entry = First(settings, "servers");
        var host = Empty(Text(settings, "address")) ?? Empty(Text(entry, "address"));
        var port = Port(settings);
        if (port == 0)
            port = Port(entry);

        var auth = Empty(Text(hysteria, "auth"))
            ?? Empty(Text(settings, "auth"))
            ?? Empty(Text(entry, "auth"))
            ?? Empty(Text(entry, "password"));

        if (string.IsNullOrWhiteSpace(host) || port == 0 || string.IsNullOrWhiteSpace(auth))
        {
            problem = $"outbound «{tag}»: не хватает адреса, порта или ключа";
            return false;
        }

        var tls = Section(stream, "tlsSettings");
        var alpn = new List<string>();
        if (tls.ValueKind == JsonValueKind.Object
            && tls.TryGetProperty("alpn", out var names)
            && names.ValueKind == JsonValueKind.Array)
        {
            alpn = names.EnumerateArray().Select(a => a.GetString() ?? string.Empty).Where(a => a.Length > 0).ToList();
        }

        var (obfsType, obfsPassword) = Salamander(stream);

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Hysteria2,
            Tag = Empty(Text(outbound, "tag")) ?? $"{protocol} {host}",
            Host = host!,
            Port = port,
            Credential = auth!,
            Transport = "udp",
            Security = "tls",
            Sni = Empty(Text(tls, "serverName")),
            Alpn = alpn,
            AllowInsecure = Flag(tls, "allowInsecure") || Flag(tls, "insecure"),
            ObfsType = obfsType,
            ObfsPassword = obfsPassword,
        };

        return true;
    }

    /// <summary>Обфускация salamander из <c>finalmask.udp</c>, как её пишет v2rayN.</summary>
    private static (string? Type, string? Password) Salamander(JsonElement stream)
    {
        var mask = Section(stream, "finalmask");
        if (mask.ValueKind != JsonValueKind.Object
            || !mask.TryGetProperty("udp", out var udp)
            || udp.ValueKind != JsonValueKind.Array)
        {
            return (null, null);
        }

        foreach (var item in udp.EnumerateArray())
        {
            if (!string.Equals(Text(item, "type"), "salamander", StringComparison.OrdinalIgnoreCase))
                continue;

            return ("salamander", Empty(Text(Section(item, "settings"), "password")));
        }

        return (null, null);
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

    private static JsonElement First(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(name, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return default;
        }

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
                return item;
        }

        return default;
    }

    private static bool Flag(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static int Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), out var text) => text,
            _ => 0,
        };
    }

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
