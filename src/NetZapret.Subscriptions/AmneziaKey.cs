using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetZapret.Subscriptions;

/// <summary>
/// Ключ AmneziaVPN <c>vpn://…</c> — в серверы WireGuard и AmneziaWG.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №40 (27.10): «Поделиться» в AmneziaVPN по умолчанию выдаёт
/// не файл .conf, а свой ключ <c>vpn://</c>, и вставить его было некуда —
/// схемы такой разбор ссылок не знал. Внутри — base64url от <c>qCompress</c>
/// Qt (четыре байта длины big-endian и поток zlib), под ним JSON сервера:
/// <c>containers</c> по протоколу, у каждого <c>last_config</c> строкой.
/// Клиент Amnezia читает и несжатый JSON — так же и здесь.
/// </para>
/// <para>
/// Берутся WireGuard и AmneziaWG: у них в <c>last_config</c> лежит готовый
/// текст .conf (поле <c>config</c>), и дальше он идёт тем же путём, что
/// файл (<see cref="WireGuardConf"/>). Без текста — собирается из полей
/// ключа (<c>client_priv_key</c>, <c>server_pub_key</c>…). XRay, OpenVPN,
/// Cloak в ключе пропускаются с объяснением: ссылки для них из ключа
/// не построить, а для XRay в «Поделиться» есть свой формат.
/// </para>
/// <para>
/// Ключ — пароль: в сообщениях об ошибках его текста нет.
/// </para>
/// </remarks>
public static class AmneziaKey
{
    public const string Scheme = "vpn://";

    /// <summary>
    /// Предел распакованного ключа. Настоящий — несколько килобайт; предел
    /// не даёт вставленному мусору с огромной длиной в заголовке съесть память.
    /// </summary>
    private const int MaxUnpacked = 4 * 1024 * 1024;

    /// <summary>Протоколы, которые в ключе бывают, а серверами у нас не становятся.</summary>
    private static readonly Dictionary<string, string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xray"] = "XRay",
        ["openvpn"] = "OpenVPN",
        ["cloak"] = "Cloak",
        ["shadowsocks"] = "Shadowsocks",
        ["ikev2"] = "IKEv2",
        ["sftp"] = "SFTP",
        ["socks5proxy"] = "SOCKS5",
    };

    private static readonly string[] AmneziaFields =
        ["Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5"];

    /// <summary>Есть ли во вставленном ключ Amnezia.</summary>
    public static bool Looks(string text) => text.Contains(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ключи <c>vpn://</c> из текста — по одному на строку или через пробел.</summary>
    public static IReadOnlyList<string> Find(string text) =>
        text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <param name="key">Ключ целиком, со схемой.</param>
    /// <param name="name">Имя сервера; <c>null</c> — из описания ключа.</param>
    public static bool TryParse(string key, string? name, out IReadOnlyList<ProxyServer> servers, out string? error)
    {
        servers = [];
        error = null;

        try
        {
            servers = Parse(key, name);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static List<ProxyServer> Parse(string key, string? name)
    {
        var root = Decode(key);
        var title = string.IsNullOrWhiteSpace(name) ? Text(root, "description") ?? Text(root, "hostName") : name.Trim();

        if (root["containers"] is not JsonArray { Count: > 0 } containers)
        {
            // У ключей Amnezia Premium и Free серверов внутри нет: клиент
            // получает их от сервиса Amnezia по выданному доступу.
            throw new FormatException(root.ContainsKey("api_config") || root.ContainsKey("auth_data")
                ? "это ключ сервиса Amnezia (Premium или Free): серверы он получает у Amnezia, внутри их нет"
                : "в ключе vpn:// нет ни одного сервера");
        }

        var servers = new List<ProxyServer>();
        var skipped = new SortedSet<string>(StringComparer.Ordinal);
        string? problem = null;

        foreach (var container in containers.OfType<JsonObject>())
        {
            foreach (var (protocol, value) in container)
            {
                if (value is not JsonObject settings)
                    continue;

                if (protocol is "wireguard" or "awg")
                {
                    if (Server(settings, root, title, out var server, out var why))
                        servers.Add(server!);
                    else
                        problem ??= why;
                }
                else if (Skipped.TryGetValue(protocol, out var shown))
                {
                    skipped.Add(shown);
                }
            }
        }

        if (servers.Count > 1)
        {
            // Два протокола на одном сервере — одно имя у обоих путало бы.
            servers = servers.Select(s => s with { Tag = $"{s.Tag} — {s.ProtocolName}" }).ToList();
        }

        if (servers.Count > 0)
            return servers;

        if (problem is not null)
            throw new FormatException(problem);

        throw new FormatException(skipped.Count > 0
            ? $"в ключе Amnezia только {string.Join(", ", skipped)} — из ключа NetZapret берёт WireGuard и AmneziaWG"
            : "в ключе vpn:// нет ни WireGuard, ни AmneziaWG");
    }

    private static bool Server(JsonObject settings, JsonObject root, string? title, out ProxyServer? server, out string? error)
    {
        server = null;
        error = "в ключе нет настроек подключения (last_config)";

        if (Text(settings, "last_config") is not { Length: > 0 } last)
            return false;

        // Старые ключи кладут в last_config сам текст .conf, новые — JSON
        // с ним в поле config и теми же значениями отдельными полями.
        // JSON отличается по первой скобке: «[Interface]» внутри него тоже
        // есть, и проверка на текст .conf приняла бы его за файл.
        if (!last.TrimStart().StartsWith('{') && WireGuardConf.Looks(last))
            return WireGuardConf.TryParse(last, title, out server, out error);

        JsonObject fields;

        try
        {
            fields = JsonNode.Parse(last) as JsonObject
                ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            error = "настройки подключения в ключе не читаются";
            return false;
        }

        if (Text(fields, "config") is { } conf && WireGuardConf.Looks(conf))
            return WireGuardConf.TryParse(conf, title, out server, out error);

        return WireGuardConf.TryParse(Conf(fields, settings, root), title, out server, out error);
    }

    /// <summary>
    /// Текст .conf из полей ключа — когда готового текста в нём нет.
    /// </summary>
    /// <remarks>
    /// Недостающее поле не подставляется: разбор файла сам скажет, чего нет.
    /// Параметры маскировки ищутся и в <c>last_config</c>, и уровнем выше —
    /// клиент Amnezia хранит их в обоих местах.
    /// </remarks>
    private static string Conf(JsonObject fields, JsonObject settings, JsonObject root)
    {
        string? Field(string name) => Text(fields, name) ?? Text(settings, name);

        var text = new StringBuilder();
        text.AppendLine("[Interface]");
        Line(text, "PrivateKey", Field("client_priv_key"));
        Line(text, "Address", Field("client_ip"));
        Line(text, "MTU", Field("mtu"));

        foreach (var key in AmneziaFields)
            Line(text, key, Field(key));

        // Незнакомую движку маскировку — тоже: разбор файла её назовёт, а не потеряет.
        foreach (var (key, _) in fields)
        {
            if (AmneziaSettings.Unsupported([key]).Count > 0)
                Line(text, key, Text(fields, key));
        }

        text.AppendLine("[Peer]");
        Line(text, "PublicKey", Field("server_pub_key"));
        Line(text, "PresharedKey", Field("psk_key"));
        Line(text, "PersistentKeepalive", Field("persistent_keep_alive"));

        var host = Field("hostName") ?? Text(root, "hostName");
        var port = Field("port");

        if (host is not null && port is not null)
            Line(text, "Endpoint", (host.Contains(':') ? $"[{host}]" : host) + ":" + port);

        return text.ToString();
    }

    private static void Line(StringBuilder text, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            text.Append(key).Append(" = ").AppendLine(value.Trim());
    }

    private static JsonObject Decode(string key)
    {
        var body = key.Trim();

        if (body.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
            body = body[Scheme.Length..];

        var text = body.TrimEnd('=').Replace('-', '+').Replace('_', '/');

        if (text.Length % 4 == 1)
            throw new FormatException("ключ vpn:// обрезан: так base64 не кончается");

        if (text.Length % 4 > 0)
            text = text.PadRight(text.Length + 4 - text.Length % 4, '=');

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            throw new FormatException("ключ vpn:// не читается: внутри не base64");
        }

        try
        {
            return JsonNode.Parse(Unpack(bytes) ?? bytes) as JsonObject
                ?? throw new FormatException("ключ vpn:// не читается: внутри не объект JSON");
        }
        catch (JsonException)
        {
            throw new FormatException("ключ vpn:// не читается: внутри ни сжатого, ни простого JSON");
        }
    }

    /// <summary>
    /// <c>qUncompress</c> Qt: длина big-endian и поток zlib; <c>null</c> — не сжато.
    /// </summary>
    private static byte[]? Unpack(byte[] bytes)
    {
        if (bytes.Length < 6)
            return null;

        long length = (long)bytes[0] << 24 | (long)bytes[1] << 16 | (long)bytes[2] << 8 | bytes[3];

        if (length is 0 or > MaxUnpacked)
            return null;

        try
        {
            using var zlib = new ZLibStream(new MemoryStream(bytes, 4, bytes.Length - 4), CompressionMode.Decompress);
            var output = new byte[length];
            int read = 0;

            while (read < output.Length)
            {
                int got = zlib.Read(output, read, output.Length - read);

                if (got == 0)
                    break;

                read += got;
            }

            return read == output.Length ? output : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>Строка или число поля как текст; пустое — <c>null</c>.</summary>
    private static string? Text(JsonObject from, string name)
    {
        var node = from[name];

        if (node is not JsonValue value)
            return null;

        var text = value.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
