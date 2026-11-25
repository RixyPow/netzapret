using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace NetZapret.Subscriptions;

/// <summary>
/// Достаёт серверы из подписки формата mihomo / Clash Meta (YAML с разделом <c>proxies:</c>).
/// </summary>
/// <remarks>
/// Обсуждение №87 (25.11): часть поставщиков отдаёт подписку готовым YAML-конфигом
/// mihomo / Clash Meta. Отдельный движок mihomo для этого не нужен — серверы из
/// <c>proxies:</c> приводятся к общему <see cref="ProxyServer"/> и идут в <c>sing-box</c>,
/// а чужие <c>proxy-groups</c> и <c>rules</c> намеренно пропускаются (как в
/// <see cref="XrayConfigParser"/>).
/// </remarks>
public static class MihomoConfigParser
{
    /// <summary>Похоже ли тело на YAML-конфиг mihomo / Clash (корневой раздел <c>proxies:</c>).</summary>
    public static bool LooksLikeConfig(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == '#' || char.IsWhiteSpace(line[0]))
                continue;

            if (line.StartsWith("proxies:", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static (IReadOnlyList<ProxyServer> Servers, IReadOnlyList<string> Errors) Parse(string body)
    {
        var servers = new List<ProxyServer>();
        var errors = new List<string>();

        YamlStream stream;
        try
        {
            stream = new YamlStream();
            stream.Load(new StringReader(body));
        }
        catch (YamlException ex)
        {
            errors.Add("конфиг mihomo не разбирается как YAML: " + ex.Message);
            return (servers, errors);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            errors.Add("конфиг mihomo пуст или не является YAML-объектом");
            return (servers, errors);
        }

        if (!TryGet(root, "proxies", out var proxiesNode) || proxiesNode is not YamlSequenceNode proxies)
        {
            errors.Add("в конфиге mihomo нет раздела proxies");
            return (servers, errors);
        }

        foreach (var item in proxies.Children)
        {
            if (item is not YamlMappingNode rawMap)
                continue;

            var map = Flatten(rawMap);
            if (TryRead(map, out var server, out var problem))
                servers.Add(server!);
            else if (problem is not null)
                errors.Add(problem);
        }

        if (servers.Count == 0 && errors.Count == 0)
            errors.Add("в конфиге mihomo нет ни одного сервера: раздел proxies пуст");

        return (servers, errors);
    }

    private static bool TryRead(Dictionary<string, YamlNode> map, out ProxyServer? server, out string? problem)
    {
        server = null;
        problem = null;

        var type = Text(map, "type")?.ToLowerInvariant();
        if (string.IsNullOrEmpty(type))
            return false;

        // Служебные псевдо-выходы Clash/mihomo — не серверы.
        if (type is "direct" or "reject" or "dns")
            return false;

        var tag = Text(map, "name") ?? Text(map, "server") ?? type;
        var host = Text(map, "server") ?? string.Empty;
        ushort port = PortOf(map, "port");

        switch (type)
        {
            case "vless":
                return ReadVless(map, tag, host, port, out server, out problem);
            case "vmess":
                return ReadVmess(map, tag, host, port, out server, out problem);
            case "trojan":
                return ReadTrojan(map, tag, host, port, out server, out problem);
            case "ss":
            case "shadowsocks":
                return ReadShadowsocks(map, tag, host, port, out server, out problem);
            case "hysteria2":
                return ReadHysteria2(map, tag, host, port, out server, out problem);
            case "hysteria":
                return ReadHysteria(map, tag, host, port, out server, out problem);
            case "tuic":
                return ReadTuic(map, tag, host, port, out server, out problem);
            case "anytls":
                return ReadAnyTls(map, tag, host, port, out server, out problem);
            case "wireguard":
                return ReadWireGuard(map, tag, host, port, out server, out problem);
            case "socks5":
                return ReadSocks(map, tag, host, port, out server, out problem);
            case "http":
                return ReadHttp(map, tag, host, port, out server, out problem);
            default:
                problem = $"{tag}: неподдерживаемый тип mihomo «{type}»";
                return false;
        }
    }

    private static bool ReadVless(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var uuid = Text(map, "uuid");
        if (string.IsNullOrEmpty(uuid))
        {
            server = null;
            problem = $"{tag}: у vless нет uuid";
            return false;
        }

        var (security, sni, pbk, sid) = ReadTlsAndReality(map, defaultTls: false);
        var (transport, path, hostHeader, serviceName, xhttpMode) = ReadTransport(map);

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Vless,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = uuid,
            Flow = Text(map, "flow"),
            Encryption = Text(map, "encryption"),
            Transport = transport,
            Path = path,
            HostHeader = hostHeader,
            ServiceName = serviceName,
            XhttpMode = xhttpMode,
            Security = security,
            Sni = sni,
            RealityPublicKey = pbk,
            RealityShortId = sid,
            Fingerprint = Text(map, "client-fingerprint"),
            Alpn = List(map, "alpn"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
        };
        problem = null;
        return true;
    }

    private static bool ReadVmess(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var uuid = Text(map, "uuid");
        if (string.IsNullOrEmpty(uuid))
        {
            server = null;
            problem = $"{tag}: у vmess нет uuid";
            return false;
        }

        var (security, sni, pbk, sid) = ReadTlsAndReality(map, defaultTls: false);
        var (transport, path, hostHeader, serviceName, xhttpMode) = ReadTransport(map);

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Vmess,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = uuid,
            AlterId = Int(map, "alterId") ?? Int(map, "alter-id") ?? 0,
            Method = Text(map, "cipher"),
            Transport = transport,
            Path = path,
            HostHeader = hostHeader,
            ServiceName = serviceName,
            XhttpMode = xhttpMode,
            Security = security,
            Sni = sni,
            RealityPublicKey = pbk,
            RealityShortId = sid,
            Fingerprint = Text(map, "client-fingerprint"),
            Alpn = List(map, "alpn"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
        };
        problem = null;
        return true;
    }

    private static bool ReadTrojan(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var password = Text(map, "password");
        if (string.IsNullOrEmpty(password))
        {
            server = null;
            problem = $"{tag}: у trojan нет password";
            return false;
        }

        var (security, sni, pbk, sid) = ReadTlsAndReality(map, defaultTls: true);
        var (transport, path, hostHeader, serviceName, xhttpMode) = ReadTransport(map);

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Trojan,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = password,
            Transport = transport,
            Path = path,
            HostHeader = hostHeader,
            ServiceName = serviceName,
            XhttpMode = xhttpMode,
            Security = security,
            Sni = sni,
            RealityPublicKey = pbk,
            RealityShortId = sid,
            Fingerprint = Text(map, "client-fingerprint"),
            Alpn = List(map, "alpn"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
        };
        problem = null;
        return true;
    }

    private static bool ReadShadowsocks(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var method = Text(map, "cipher");
        var password = Text(map, "password") ?? string.Empty;
        if (string.IsNullOrEmpty(method))
        {
            server = null;
            problem = $"{tag}: у ss нет cipher";
            return false;
        }

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Shadowsocks,
            Tag = tag,
            Host = host,
            Port = port,
            Method = method,
            Credential = password,
            Transport = "tcp",
            Security = "none",
        };
        problem = null;
        return true;
    }

    private static bool ReadHysteria2(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var password = Text(map, "password") ?? Text(map, "auth");
        if (string.IsNullOrEmpty(password))
        {
            server = null;
            problem = $"{tag}: у hysteria2 нет password";
            return false;
        }

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Hysteria2,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = password,
            Transport = "udp",
            Security = "tls",
            Sni = Text(map, "sni") ?? Text(map, "servername"),
            Alpn = List(map, "alpn"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
            ObfsType = Text(map, "obfs"),
            ObfsPassword = Text(map, "obfs-password"),
            HopPorts = PortHopping.Normalize(Text(map, "ports")),
            UpMbps = ParseMbps(Text(map, "up")),
            DownMbps = ParseMbps(Text(map, "down")),
        };
        problem = null;
        return true;
    }

    private static bool ReadHysteria(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var auth = Text(map, "auth-str") ?? Text(map, "auth_str") ?? Text(map, "auth") ?? string.Empty;
        int up = ParseMbps(Text(map, "up"));
        int down = ParseMbps(Text(map, "down"));

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Hysteria,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = auth,
            Transport = "udp",
            Security = "tls",
            Sni = Text(map, "sni") ?? Text(map, "servername"),
            Alpn = List(map, "alpn"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
            ObfsPassword = Text(map, "obfs"),
            UpMbps = up > 0 ? up : 50,
            DownMbps = down > 0 ? down : 200,
        };
        problem = null;
        return true;
    }

    private static bool ReadTuic(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var uuid = Text(map, "uuid");
        if (string.IsNullOrEmpty(uuid))
        {
            server = null;
            problem = $"{tag}: у tuic нет uuid";
            return false;
        }

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Tuic,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = uuid,
            Password = Text(map, "password"),
            Transport = "udp",
            Security = "tls",
            Sni = Text(map, "sni") ?? Text(map, "servername"),
            Alpn = List(map, "alpn"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
            CongestionControl = Text(map, "congestion-controller"),
            UdpRelayMode = Text(map, "udp-relay-mode"),
        };
        problem = null;
        return true;
    }

    private static bool ReadAnyTls(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var password = Text(map, "password");
        if (string.IsNullOrEmpty(password))
        {
            server = null;
            problem = $"{tag}: у anytls нет password";
            return false;
        }

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.AnyTls,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = password,
            Transport = "tcp",
            Security = "tls",
            Sni = Text(map, "sni") ?? Text(map, "servername"),
            Fingerprint = Text(map, "client-fingerprint"),
            Alpn = List(map, "alpn"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
        };
        problem = null;
        return true;
    }

    private static bool ReadWireGuard(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        var privateKey = Text(map, "private-key");
        var publicKey = Text(map, "public-key");
        if (string.IsNullOrEmpty(privateKey) || string.IsNullOrEmpty(publicKey))
        {
            server = null;
            problem = $"{tag}: у wireguard нет private-key или public-key";
            return false;
        }

        var local = new List<string>();
        if (Text(map, "ip") is { Length: > 0 } ipv4)
            local.Add(ipv4.Contains('/') ? ipv4 : ipv4 + "/32");
        if (Text(map, "ipv6") is { Length: > 0 } ipv6)
            local.Add(ipv6.Contains('/') ? ipv6 : ipv6 + "/128");

        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Wireguard,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = privateKey,
            PeerPublicKey = publicKey,
            PreSharedKey = Text(map, "pre-shared-key") ?? Text(map, "preshared-key"),
            LocalAddresses = local,
            Mtu = Int(map, "mtu") ?? 1280,
            Transport = "udp",
            Security = "none",
        };
        problem = null;
        return true;
    }

    private static bool ReadSocks(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Socks,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = Text(map, "username") ?? string.Empty,
            Password = Text(map, "password"),
            SocksVersion = "5",
            Transport = "tcp",
            Security = Bool(map, "tls") ? "tls" : "none",
            Sni = Text(map, "sni") ?? Text(map, "servername"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
        };
        problem = null;
        return true;
    }

    private static bool ReadHttp(Dictionary<string, YamlNode> map, string tag, string host, ushort port,
        out ProxyServer? server, out string? problem)
    {
        server = new ProxyServer
        {
            Protocol = ProxyProtocol.Http,
            Tag = tag,
            Host = host,
            Port = port,
            Credential = Text(map, "username") ?? string.Empty,
            Password = Text(map, "password"),
            Transport = "tcp",
            Security = Bool(map, "tls") ? "tls" : "none",
            Sni = Text(map, "sni") ?? Text(map, "servername"),
            AllowInsecure = Bool(map, "skip-cert-verify"),
        };
        problem = null;
        return true;
    }

    private static (string Security, string? Sni, string? PublicKey, string? ShortId) ReadTlsAndReality(
        Dictionary<string, YamlNode> map, bool defaultTls)
    {
        string? sni = Text(map, "servername") ?? Text(map, "sni");

        if (SubMap(map, "reality-opts") is { } reality && Text(reality, "public-key") is { Length: > 0 } pbk)
            return ("reality", sni, pbk, Text(reality, "short-id"));

        bool tls = map.ContainsKey("tls") ? Bool(map, "tls") : defaultTls;
        return (tls ? "tls" : "none", sni, null, null);
    }

    private static (string Transport, string? Path, string? HostHeader, string? ServiceName, string? XhttpMode)
        ReadTransport(Dictionary<string, YamlNode> map)
    {
        var transport = StreamTransports.Normalize(Text(map, "network"));
        string? path = null;
        string? hostHeader = null;
        string? serviceName = null;
        string? xhttpMode = null;

        switch (transport)
        {
            case "ws":
                if (SubMap(map, "ws-opts") is { } ws)
                {
                    path = Text(ws, "path");
                    hostHeader = HeaderHost(ws);
                }
                break;

            case "grpc":
                if (SubMap(map, "grpc-opts") is { } grpc)
                    serviceName = Text(grpc, "grpc-service-name");
                break;

            case "http":
                if (SubMap(map, "h2-opts") is { } h2)
                {
                    path = Text(h2, "path");
                    hostHeader = FirstOf(h2, "host") ?? HeaderHost(h2);
                }
                else if (SubMap(map, "http-opts") is { } http)
                {
                    path = FirstOf(http, "path");
                    hostHeader = HeaderHost(http);
                }
                break;

            case "httpupgrade":
                if (SubMap(map, "httpupgrade-opts") is { } hu)
                {
                    path = Text(hu, "path");
                    hostHeader = Text(hu, "host") ?? HeaderHost(hu);
                }
                break;

            case "xhttp":
                if (SubMap(map, "xhttp-opts") is { } xhttp)
                {
                    path = Text(xhttp, "path");
                    hostHeader = Text(xhttp, "host") ?? HeaderHost(xhttp);
                    xhttpMode = Text(xhttp, "mode");
                }
                break;
        }

        return (transport, path, hostHeader, serviceName, xhttpMode);
    }

    private static string? HeaderHost(Dictionary<string, YamlNode> opts) =>
        SubMap(opts, "headers") is { } headers
            ? FirstOf(headers, "Host") ?? FirstOf(headers, "host")
            : null;

    /// <summary>Разворачивает YAML-слияние <c>&lt;&lt;: *anchor</c>, если оно есть в записи.</summary>
    private static Dictionary<string, YamlNode> Flatten(YamlMappingNode node)
    {
        var result = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);

        if (TryGet(node, "<<", out var merged))
        {
            if (merged is YamlMappingNode single)
            {
                foreach (var kv in Flatten(single))
                    result[kv.Key] = kv.Value;
            }
            else if (merged is YamlSequenceNode seq)
            {
                foreach (var child in seq.Children.OfType<YamlMappingNode>())
                {
                    foreach (var kv in Flatten(child))
                        result[kv.Key] = kv.Value;
                }
            }
        }

        foreach (var entry in node.Children)
        {
            if (entry.Key is YamlScalarNode { Value: { Length: > 0 } key } && key != "<<")
                result[key] = entry.Value;
        }

        return result;
    }

    private static bool TryGet(YamlMappingNode map, string key, out YamlNode? value)
    {
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode { Value: { } k } && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static Dictionary<string, YamlNode>? SubMap(Dictionary<string, YamlNode> map, string key) =>
        map.TryGetValue(key, out var node) && node is YamlMappingNode sub ? Flatten(sub) : null;

    private static string? Text(Dictionary<string, YamlNode> map, string key) =>
        map.TryGetValue(key, out var node) && node is YamlScalarNode { Value: { } val } && val.Trim().Length > 0
            ? val.Trim()
            : null;

    private static string? FirstOf(Dictionary<string, YamlNode> map, string key)
    {
        if (!map.TryGetValue(key, out var node))
            return null;

        if (node is YamlScalarNode { Value: { } scalar } && scalar.Trim().Length > 0)
            return scalar.Trim();

        if (node is YamlSequenceNode seq)
        {
            foreach (var child in seq.Children)
            {
                if (child is YamlScalarNode { Value: { } s } && s.Trim().Length > 0)
                    return s.Trim();
            }
        }

        return null;
    }

    private static IReadOnlyList<string> List(Dictionary<string, YamlNode> map, string key)
    {
        if (!map.TryGetValue(key, out var node))
            return Array.Empty<string>();

        if (node is YamlSequenceNode seq)
        {
            return seq.Children
                .OfType<YamlScalarNode>()
                .Select(n => n.Value?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Cast<string>()
                .ToList();
        }

        if (node is YamlScalarNode { Value: { } scalar } && scalar.Trim().Length > 0)
            return [scalar.Trim()];

        return Array.Empty<string>();
    }

    private static bool Bool(Dictionary<string, YamlNode> map, string key) =>
        Text(map, key) is { } val && (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1");

    private static int? Int(Dictionary<string, YamlNode> map, string key) =>
        Text(map, key) is { } val && int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
            ? n
            : null;

    private static ushort PortOf(Dictionary<string, YamlNode> map, string key) =>
        Text(map, key) is { } val && ushort.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out ushort p)
            ? p
            : (ushort)0;

    /// <summary>Из строк вида <c>100 Mbps</c> или <c>50</c> берёт число Мбит/с.</summary>
    private static int ParseMbps(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var match = Regex.Match(value, @"\d+");
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
            ? n
            : 0;
    }
}
