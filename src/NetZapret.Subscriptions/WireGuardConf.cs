namespace NetZapret.Subscriptions;

/// <summary>
/// Файл <c>.conf</c> WireGuard и AmneziaWG — в сервер.
/// </summary>
/// <remarks>
/// <para>
/// Ключи WireGuard обычно раздают файлом, а не ссылкой: так их выдаёт
/// сам WireGuard, AmneziaVPN и большинство продавцов. Формат — INI:
/// <c>[Interface]</c> со своим ключом и адресом, <c>[Peer]</c> с сервером.
/// Параметры AmneziaWG (Jc, S1, H1, I1…) лежат в <c>[Interface]</c>.
/// </para>
/// <para>
/// Пиров бывает несколько; берётся первый — у клиентского файла он один,
/// а несколько бывает у файла сервера, который нам не нужен.
/// </para>
/// </remarks>
public static class WireGuardConf
{
    /// <summary>Похож ли текст на файл WireGuard — чтобы вставка из буфера знала, кому его отдать.</summary>
    public static bool Looks(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
        && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    /// <param name="text">Содержимое файла.</param>
    /// <param name="name">Имя сервера; <c>null</c> — по адресу сервера.</param>
    public static bool TryParse(string text, string? name, out ProxyServer? server, out string? error)
    {
        server = null;
        error = null;

        try
        {
            server = Parse(text, name);
            return true;
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static ProxyServer Parse(string text, string? name)
    {
        var face = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? section = null;
        int peers = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            if (line.StartsWith('['))
            {
                section = line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase) ? face
                    : line.Equals("[Peer]", StringComparison.OrdinalIgnoreCase) && ++peers == 1 ? peer
                    : null;
                continue;
            }

            int equals = line.IndexOf('=');

            if (section is null || equals <= 0)
                continue;

            // Значение — всё после первого «=»: в ключах base64 свои «=» на конце.
            section[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        string Need(Dictionary<string, string> from, string key, string what) =>
            from.TryGetValue(key, out var value) && value.Length > 0
                ? value
                : throw new FormatException($"в файле нет {what} ({key})");

        var endpoint = Need(peer, "Endpoint", "адреса сервера");
        var (host, port) = HostPort(endpoint);
        var addresses = Addresses(Need(face, "Address", "своего адреса в туннеле"));

        return new ProxyServer
        {
            Protocol = ProxyProtocol.Wireguard,
            Tag = string.IsNullOrWhiteSpace(name) ? $"{host}:{port}" : name.Trim(),
            Host = host,
            Port = port,
            Credential = Need(face, "PrivateKey", "закрытого ключа"),
            Transport = "udp",
            PeerPublicKey = Need(peer, "PublicKey", "открытого ключа сервера"),
            PreSharedKey = peer.TryGetValue("PresharedKey", out var psk) && psk.Length > 0 ? psk : null,
            LocalAddresses = addresses,
            Mtu = face.TryGetValue("MTU", out var mtuText) && int.TryParse(mtuText, out var mtu) && mtu >= 576
                ? mtu
                : 1280,
            KeepaliveSeconds = peer.TryGetValue("PersistentKeepalive", out var keepText)
                && int.TryParse(keepText, out var keep) && keep > 0
                    ? keep
                    : 30,
            AmneziaOptions = AmneziaSettings.From(key => face.TryGetValue(key, out var value) ? value : null),
        };
    }

    /// <summary>
    /// Сервер WireGuard обратно в ссылку — чтобы файл .conf жил обычным ключом.
    /// </summary>
    /// <remarks>
    /// Ключи экранируются целиком: в base64 есть «/», «+» и «=», и сырыми
    /// они разрезали бы ссылку. Разбирает её <see cref="ProxyUriParser"/>,
    /// и проверено это круговым тестом: файл → ссылка → тот же сервер.
    /// </remarks>
    public static string ToLink(ProxyServer server)
    {
        var query = new List<string>
        {
            "publickey=" + Uri.EscapeDataString(server.PeerPublicKey ?? string.Empty),
            "address=" + Uri.EscapeDataString(string.Join(",", server.LocalAddresses)),
            "mtu=" + server.Mtu,
            "keepalive=" + server.KeepaliveSeconds,
        };

        if (!string.IsNullOrEmpty(server.PreSharedKey))
            query.Add("presharedkey=" + Uri.EscapeDataString(server.PreSharedKey));

        if (server.AmneziaOptions is { } amnezia
            && System.Text.Json.Nodes.JsonNode.Parse(amnezia) is System.Text.Json.Nodes.JsonObject block)
        {
            foreach (var (key, value) in block)
            {
                var text = value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s)
                    ? s
                    : value?.ToJsonString() ?? string.Empty;

                query.Add(key + "=" + Uri.EscapeDataString(text));
            }
        }

        var host = server.Host.Contains(':') ? $"[{server.Host}]" : server.Host;

        return $"wireguard://{Uri.EscapeDataString(server.Credential)}@{host}:{server.Port}"
            + "?" + string.Join("&", query)
            + "#" + Uri.EscapeDataString(server.Tag);
    }

    /// <summary>
    /// Свои адреса в туннеле, через запятую; без маски — как одиночный адрес.
    /// </summary>
    /// <remarks>
    /// Без маски движок адрес не примет, а в файлах её иногда опускают.
    /// </remarks>
    public static IReadOnlyList<string> Addresses(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.Contains('/') ? a : a + (a.Contains(':') ? "/128" : "/32"))
            .ToList();
    }

    /// <summary>«host:port» и «[v6]:port».</summary>
    private static (string Host, ushort Port) HostPort(string endpoint)
    {
        var text = endpoint.Trim();

        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');

            if (close > 0 && text[(close + 1)..].StartsWith(':')
                && ushort.TryParse(text[(close + 2)..], out var v6Port))
            {
                return (text[1..close], v6Port);
            }

            throw new FormatException($"адрес сервера «{endpoint}» не разбирается");
        }

        int colon = text.LastIndexOf(':');

        if (colon <= 0 || !ushort.TryParse(text[(colon + 1)..], out var port))
            throw new FormatException($"в адресе сервера «{endpoint}» нет порта");

        return (text[..colon], port);
    }
}
