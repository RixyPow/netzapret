using System.Text;
using System.Text.Json;

namespace NetZapret.Subscriptions;

/// <summary>
/// Разбирает ссылки на прокси-серверы.
/// </summary>
/// <remarks>
/// Единый разборщик вместо интерфейса на протокол: различия между схемами
/// сводятся к тому, как трактовать часть до <c>@</c>, и городить ради этого
/// пять классов с фабрикой было бы дороже, чем польза.
/// </remarks>
public static class ProxyUriParser
{
    public static bool TryParse(string uri, out ProxyServer? server, out string? error)
    {
        server = null;
        error = null;

        if (string.IsNullOrWhiteSpace(uri))
        {
            error = "пустая строка";
            return false;
        }

        var text = uri.Trim();
        int schemeEnd = text.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd <= 0)
        {
            error = "не похоже на ссылку: нет '://'";
            return false;
        }

        var scheme = text[..schemeEnd].ToLowerInvariant();
        var rest = text[(schemeEnd + 3)..];

        try
        {
            server = scheme switch
            {
                "vless" => ParseUserInfoForm(rest, ProxyProtocol.Vless),
                "trojan" => ParseUserInfoForm(rest, ProxyProtocol.Trojan),
                "hysteria2" or "hy2" => ParseHysteria2(rest),
                "vmess" => ParseVmess(rest),
                "ss" => ParseShadowsocks(rest),
                _ => null,
            };

            if (server is null)
                error = $"неподдерживаемая схема '{scheme}'";
        }
        catch (Exception ex)
        {
            error = ex.Message;
            server = null;
        }

        return server is not null;
    }

    /// <summary>
    /// Общая форма <c>схема://credential@host:port?params#name</c> — vless и trojan.
    /// </summary>
    private static ProxyServer ParseUserInfoForm(string rest, ProxyProtocol protocol)
    {
        var parts = UriParts.Split(rest);

        if (parts.UserInfo is null)
            throw new FormatException("отсутствует часть до '@' с учётными данными");

        var q = parts.Query;

        return new ProxyServer
        {
            Protocol = protocol,
            Tag = parts.Fragment ?? $"{parts.Host}:{parts.Port}",
            Host = parts.Host,
            Port = parts.Port,
            Credential = Uri.UnescapeDataString(parts.UserInfo),
            Transport = Get(q, "type") ?? "tcp",
            Security = Get(q, "security") ?? "none",
            Sni = Get(q, "sni") ?? Get(q, "peer"),
            Fingerprint = Get(q, "fp"),
            Alpn = SplitAlpn(Get(q, "alpn")),
            Flow = Get(q, "flow"),
            RealityPublicKey = Get(q, "pbk"),
            RealityShortId = Get(q, "sid"),
            Path = Get(q, "path"),
            HostHeader = Get(q, "host"),
            ServiceName = Get(q, "serviceName") ?? Get(q, "servicename"),
            AllowInsecure = IsTruthy(Get(q, "allowInsecure")),
            Extra = q,
        };
    }

    /// <summary>
    /// Hysteria2 — та же форма, но транспорт и TLS у него встроенные,
    /// а между портом и query почти всегда стоит '/'.
    /// </summary>
    private static ProxyServer ParseHysteria2(string rest)
    {
        var parts = UriParts.Split(rest);

        if (parts.UserInfo is null)
            throw new FormatException("отсутствует пароль до '@'");

        var q = parts.Query;

        return new ProxyServer
        {
            Protocol = ProxyProtocol.Hysteria2,
            Tag = parts.Fragment ?? $"{parts.Host}:{parts.Port}",
            Host = parts.Host,
            Port = parts.Port,
            Credential = Uri.UnescapeDataString(parts.UserInfo),
            Transport = "udp",
            Security = "tls",
            Sni = Get(q, "sni") ?? Get(q, "peer"),
            Alpn = SplitAlpn(Get(q, "alpn")),
            AllowInsecure = IsTruthy(Get(q, "insecure")) || IsTruthy(Get(q, "allowInsecure")),

            // Обфускация читается наравне с паролем, а не складывается в Extra.
            // Сервер, который её ждёт, на обычные пакеты не отвечает ничем —
            // ни отказом, ни сбросом, — и подписка выглядит мёртвой целиком.
            ObfsType = Get(q, "obfs"),
            ObfsPassword = Get(q, "obfs-password") ?? Get(q, "obfsPassword") ?? Get(q, "obfs_password"),
            Extra = q,
        };
    }

    /// <summary>vmess — base64 от JSON, без query-параметров вовсе.</summary>
    private static ProxyServer ParseVmess(string rest)
    {
        var json = Base64.DecodeToString(StripFragment(rest, out var fragment))
            ?? throw new FormatException("тело vmess не декодируется из base64");

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string? Field(string name) =>
            root.TryGetProperty(name, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
                : null;

        var host = Field("add") ?? throw new FormatException("в vmess нет поля 'add'");
        var portText = Field("port") ?? throw new FormatException("в vmess нет поля 'port'");

        if (!ushort.TryParse(portText, out var port))
            throw new FormatException($"порт '{portText}' не разбирается");

        var tls = Field("tls");

        return new ProxyServer
        {
            Protocol = ProxyProtocol.Vmess,
            Tag = fragment ?? Field("ps") ?? $"{host}:{port}",
            Host = host,
            Port = port,
            Credential = Field("id") ?? throw new FormatException("в vmess нет поля 'id'"),
            Transport = Field("net") ?? "tcp",
            Security = string.IsNullOrEmpty(tls) ? "none" : tls,
            Sni = Field("sni"),
            Path = Field("path"),
            HostHeader = Field("host"),
            Fingerprint = Field("fp"),
            Alpn = SplitAlpn(Field("alpn")),
            AlterId = int.TryParse(Field("aid"), out var aid) ? aid : 0,
            Extra = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    /// <summary>
    /// Shadowsocks. Две формы: <c>ss://base64(method:pass)@host:port</c> и
    /// полностью закодированная <c>ss://base64(method:pass@host:port)</c>.
    /// </summary>
    private static ProxyServer ParseShadowsocks(string rest)
    {
        var body = StripFragment(rest, out var fragment);

        // Отделяем query, если он есть (плагины).
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int queryStart = body.IndexOf('?');
        if (queryStart >= 0)
        {
            query = UriParts.ParseQuery(body[(queryStart + 1)..]);
            body = body[..queryStart];
        }

        string method;
        string password;
        string hostPort;

        int at = body.LastIndexOf('@');
        if (at >= 0)
        {
            var userInfo = Base64.DecodeToString(body[..at]) ?? Uri.UnescapeDataString(body[..at]);
            hostPort = body[(at + 1)..];
            (method, password) = SplitMethodPassword(userInfo);
        }
        else
        {
            var decoded = Base64.DecodeToString(body)
                ?? throw new FormatException("тело ss не декодируется из base64");

            int decodedAt = decoded.LastIndexOf('@');
            if (decodedAt < 0)
                throw new FormatException("в декодированном ss нет '@'");

            (method, password) = SplitMethodPassword(decoded[..decodedAt]);
            hostPort = decoded[(decodedAt + 1)..];
        }

        var (host, port) = UriParts.SplitHostPort(hostPort);

        return new ProxyServer
        {
            Protocol = ProxyProtocol.Shadowsocks,
            Tag = fragment ?? $"{host}:{port}",
            Host = host,
            Port = port,
            Credential = password,
            Method = method,
            Transport = "tcp",
            Security = "none",
            Extra = query,
        };
    }

    private static (string Method, string Password) SplitMethodPassword(string userInfo)
    {
        int colon = userInfo.IndexOf(':');
        if (colon < 0)
            throw new FormatException("ожидается 'метод:пароль'");

        return (userInfo[..colon], userInfo[(colon + 1)..]);
    }

    private static string StripFragment(string text, out string? fragment)
    {
        int hash = text.IndexOf('#');
        if (hash < 0)
        {
            fragment = null;
            return text;
        }

        fragment = Uri.UnescapeDataString(text[(hash + 1)..]).Trim();
        if (fragment.Length == 0)
            fragment = null;

        return text[..hash];
    }

    private static string? Get(IReadOnlyDictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

    private static IReadOnlyList<string> SplitAlpn(string? alpn) =>
        string.IsNullOrWhiteSpace(alpn)
            ? Array.Empty<string>()
            : alpn.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>Разложенная на части ссылка.</summary>
    private readonly record struct UriParts(
        string? UserInfo,
        string Host,
        ushort Port,
        Dictionary<string, string> Query,
        string? Fragment)
    {
        public static UriParts Split(string rest)
        {
            var body = StripFragment(rest, out var fragment);

            var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int queryStart = body.IndexOf('?');
            if (queryStart >= 0)
            {
                query = ParseQuery(body[(queryStart + 1)..]);
                body = body[..queryStart];
            }

            // Путь отрезаем ПОСЛЕ query: у hysteria2 ссылка выглядит как
            // hysteria2://pass@host:443/?sni=... — без этого порт получится "443/".
            int pathStart = body.IndexOf('/');
            if (pathStart >= 0)
                body = body[..pathStart];

            string? userInfo = null;
            int at = body.LastIndexOf('@');
            if (at >= 0)
            {
                userInfo = body[..at];
                body = body[(at + 1)..];
            }

            var (host, port) = SplitHostPort(body);
            return new UriParts(userInfo, host, port, query, fragment);
        }

        public static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');

                if (equals < 0)
                {
                    result[Uri.UnescapeDataString(pair)] = string.Empty;
                    continue;
                }

                var key = Uri.UnescapeDataString(pair[..equals]);
                result[key] = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }

            return result;
        }

        public static (string Host, ushort Port) SplitHostPort(string hostPort)
        {
            var text = hostPort.Trim();

            if (text.Length == 0)
                throw new FormatException("пустой адрес сервера");

            // IPv6 в квадратных скобках: [::1]:443
            if (text.StartsWith('['))
            {
                int close = text.IndexOf(']');
                if (close < 0)
                    throw new FormatException("незакрытая скобка в адресе IPv6");

                var address = text[1..close];
                var tail = text[(close + 1)..];

                if (tail.StartsWith(':') && ushort.TryParse(tail[1..], out var v6Port))
                    return (address, v6Port);

                throw new FormatException("у адреса IPv6 не указан порт");
            }

            int colon = text.LastIndexOf(':');
            if (colon < 0)
                throw new FormatException($"в '{text}' не указан порт");

            if (!ushort.TryParse(text[(colon + 1)..], out var port))
                throw new FormatException($"порт '{text[(colon + 1)..]}' не разбирается");

            return (text[..colon], port);
        }
    }
}

/// <summary>Base64 в вариантах, которые встречаются в подписках.</summary>
public static class Base64
{
    /// <summary>
    /// Декодирует строку, терпимо относясь к URL-safe алфавиту и отсутствию
    /// выравнивающих символов — подписки то и другое допускают.
    /// Возвращает <c>null</c>, если строка не base64.
    /// </summary>
    public static string? DecodeToString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var text = value.Trim().Replace('-', '+').Replace('_', '/');

        int padding = text.Length % 4;
        if (padding == 1)
            return null;
        if (padding > 0)
            text = text.PadRight(text.Length + (4 - padding), '=');

        try
        {
            var bytes = Convert.FromBase64String(text);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
