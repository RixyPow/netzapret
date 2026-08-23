using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using NetZapret.Core.Rules;
using NetZapret.Subscriptions;

namespace NetZapret.Proxy;

public sealed class SingBoxOptions
{
    public string TunInterfaceName { get; init; } = "netzapret0";

    /// <summary>Адрес TUN-интерфейса.</summary>
    public string TunAddress { get; init; } = "172.19.0.1/30";

    /// <summary>Диапазон fakeip для IPv4 — по умолчанию зарезервированный под тесты.</summary>
    public string FakeIpV4Range { get; init; } = "198.18.0.0/15";

    public string FakeIpV6Range { get; init; } = "fc00::/18";

    public string ClashApiListen { get; init; } = "127.0.0.1:9090";

    /// <summary>
    /// Апстрим DNS. Указывается адресом, а не именем: имя потребовало бы
    /// отдельного резолвера для его собственного разрешения.
    /// </summary>
    public string DnsServer { get; init; } = "8.8.8.8";

    public string DnsServerType { get; init; } = "https";

    /// <summary>Тег селектора, к которому обращаются правила с <c>server: auto</c>.</summary>
    public string SelectorTag { get; init; } = "auto";

    public string LogLevel { get; init; } = "warn";
}

/// <summary>
/// Собирает конфиг sing-box из правил NetZapret и списка серверов.
/// </summary>
/// <remarks>
/// <para>
/// Здесь встречаются две половины системы: правила, которыми владеет NetZapret,
/// и транспорт, которым владеет sing-box. Соответствие режимов:
/// </para>
/// <list type="bullet">
/// <item><see cref="RoutingMode.Proxy"/> — outbound с тегом сервера либо селектор.</item>
/// <item><see cref="RoutingMode.Direct"/> — outbound <c>direct</c>.</item>
/// <item><see cref="RoutingMode.Desync"/> — тоже <c>direct</c>: трафик выходит
/// обычным сокетом на реальном интерфейсе, где его и видит WinDivert.
/// Различие между ними станет содержательным, когда потребуется уводить
/// такой трафик мимо TUN целиком.</item>
/// </list>
/// <para>
/// Серверы с транспортом xhttp пропускаются: sing-box его не реализует.
/// Их список возвращается в <see cref="CompilationResult.SkippedServers"/>.
/// </para>
/// </remarks>
public sealed class SingBoxConfigCompiler
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // Кириллица и флаги стран в тегах серверов должны остаться читаемыми,
        // а не превратиться в \uXXXX.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Обязателен: инициализатор коллекции JsonArray выбирает обобщённый
        // Add<T>, а он оборачивает даже строки в JsonValueCustomized<T>,
        // сериализация которого без резолвера типов падает.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    public CompilationResult Compile(RuleSet ruleSet, IReadOnlyList<ProxyServer> servers, SingBoxOptions options)
    {
        var usable = servers.Where(s => s.IsSupportedBySingBox).ToList();
        var skipped = servers.Where(s => !s.IsSupportedBySingBox).ToList();

        var tags = AssignUniqueTags(usable);

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = options.LogLevel },
            ["dns"] = BuildDns(options),
            ["inbounds"] = BuildInbounds(options),
            ["outbounds"] = BuildOutbounds(usable, tags, options),
            ["route"] = BuildRoute(ruleSet, tags, options),
            ["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject { ["external_controller"] = options.ClashApiListen },
            },
        };

        return new CompilationResult
        {
            Json = root.ToJsonString(SerializerOptions),
            UsedServers = usable,
            SkippedServers = skipped,
            ServerTags = tags,
        };
    }

    /// <summary>
    /// Пишет конфиг на диск в UTF-8 <b>без BOM</b>.
    /// </summary>
    /// <remarks>
    /// Существенно: разборщик JSON в Go спотыкается о метку порядка байт и
    /// падает с «invalid character 'ï' looking for beginning of value».
    /// </remarks>
    public static void WriteToFile(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Теги должны быть уникальны — sing-box адресует outbound'ы по ним, а имена
    /// серверов в подписках повторяются.
    /// </summary>
    private static Dictionary<ProxyServer, string> AssignUniqueTags(IReadOnlyList<ProxyServer> servers)
    {
        var tags = new Dictionary<ProxyServer, string>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "direct", "auto" };

        foreach (var server in servers)
        {
            var baseTag = string.IsNullOrWhiteSpace(server.Tag) ? $"{server.Host}:{server.Port}" : server.Tag.Trim();
            var tag = baseTag;

            for (int suffix = 2; !used.Add(tag); suffix++)
                tag = $"{baseTag} #{suffix}";

            tags[server] = tag;
        }

        return tags;
    }

    private static JsonObject BuildDns(SingBoxOptions options) => new()
    {
        ["servers"] = new JsonArray
        {
            new JsonObject
            {
                ["tag"] = "remote",
                ["type"] = options.DnsServerType,
                ["server"] = options.DnsServer,
            },
            new JsonObject
            {
                ["tag"] = "fake",
                ["type"] = "fakeip",
                ["inet4_range"] = options.FakeIpV4Range,
                ["inet6_range"] = options.FakeIpV6Range,
            },
        },
        // Fakeip выдаёт каждому домену собственный адрес, поэтому соответствие
        // «адрес → домен» становится точным 1:1 и доменные правила работают
        // без догадок по реальным адресам CDN.
        ["rules"] = new JsonArray
        {
            new JsonObject
            {
                ["query_type"] = new JsonArray { "A", "AAAA" },
                ["server"] = "fake",
            },
        },
        ["independent_cache"] = true,
    };

    private static JsonArray BuildInbounds(SingBoxOptions options) => new()
    {
        new JsonObject
        {
            ["type"] = "tun",
            ["tag"] = "tun-in",
            ["interface_name"] = options.TunInterfaceName,
            ["address"] = new JsonArray { options.TunAddress },
            ["auto_route"] = true,
            // strict_route выключен намеренно: он добавляет фильтры WFP,
            // блокирующие трафик мимо туннеля, а нам нужно, чтобы direct
            // и desync спокойно уходили на реальный интерфейс к WinDivert.
            ["strict_route"] = false,
            ["stack"] = "gvisor",
        },
    };

    private static JsonArray BuildOutbounds(
        IReadOnlyList<ProxyServer> servers,
        IReadOnlyDictionary<ProxyServer, string> tags,
        SingBoxOptions options)
    {
        var outbounds = new JsonArray
        {
            new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
        };

        foreach (var server in servers)
            outbounds.Add(BuildOutbound(server, tags[server]));

        if (servers.Count > 0)
        {
            var members = new JsonArray();
            foreach (var server in servers)
                members.Add(tags[server]);

            outbounds.Add(new JsonObject
            {
                ["type"] = "selector",
                ["tag"] = options.SelectorTag,
                ["outbounds"] = members,
                ["default"] = tags[servers[0]],
            });
        }

        return outbounds;
    }

    private static JsonObject BuildOutbound(ProxyServer server, string tag)
    {
        var outbound = new JsonObject
        {
            ["tag"] = tag,
            ["server"] = server.Host,
            // Приведение к int обязательно: у ushort нет неявного преобразования
            // в JsonNode, и значение ушло бы через резолвер типов, которого нет.
            ["server_port"] = (int)server.Port,
        };

        switch (server.Protocol)
        {
            case ProxyProtocol.Vless:
                outbound["type"] = "vless";
                outbound["uuid"] = server.Credential;
                if (!string.IsNullOrEmpty(server.Flow))
                    outbound["flow"] = server.Flow;
                break;

            case ProxyProtocol.Vmess:
                outbound["type"] = "vmess";
                outbound["uuid"] = server.Credential;
                outbound["alter_id"] = server.AlterId;
                outbound["security"] = "auto";
                break;

            case ProxyProtocol.Trojan:
                outbound["type"] = "trojan";
                outbound["password"] = server.Credential;
                break;

            case ProxyProtocol.Shadowsocks:
                outbound["type"] = "shadowsocks";
                outbound["method"] = server.Method ?? "aes-256-gcm";
                outbound["password"] = server.Credential;
                break;

            case ProxyProtocol.Hysteria2:
                outbound["type"] = "hysteria2";
                outbound["password"] = server.Credential;
                break;

            default:
                throw new NotSupportedException($"Протокол {server.Protocol} не поддерживается генератором.");
        }

        var tls = BuildTls(server);
        if (tls is not null)
            outbound["tls"] = tls;

        var transport = BuildTransport(server);
        if (transport is not null)
            outbound["transport"] = transport;

        return outbound;
    }

    private static JsonObject? BuildTls(ProxyServer server)
    {
        bool reality = server.Security.Equals("reality", StringComparison.OrdinalIgnoreCase);
        bool tls = reality
            || server.Security.Equals("tls", StringComparison.OrdinalIgnoreCase)
            || server.Protocol == ProxyProtocol.Hysteria2
            || server.Protocol == ProxyProtocol.Trojan;

        if (!tls)
            return null;

        var block = new JsonObject { ["enabled"] = true };

        if (!string.IsNullOrEmpty(server.Sni))
            block["server_name"] = server.Sni;

        if (server.AllowInsecure)
            block["insecure"] = true;

        if (server.Alpn.Count > 0)
        {
            var alpn = new JsonArray();
            foreach (var value in server.Alpn)
                alpn.Add(value);

            block["alpn"] = alpn;
        }

        // uTLS подделывает отпечаток клиента. Для REALITY он обязателен:
        // без него сервер не отвечает.
        if (!string.IsNullOrEmpty(server.Fingerprint) || reality)
        {
            block["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = string.IsNullOrEmpty(server.Fingerprint) ? "chrome" : server.Fingerprint,
            };
        }

        if (reality)
        {
            var realityBlock = new JsonObject { ["enabled"] = true };

            if (!string.IsNullOrEmpty(server.RealityPublicKey))
                realityBlock["public_key"] = server.RealityPublicKey;

            if (!string.IsNullOrEmpty(server.RealityShortId))
                realityBlock["short_id"] = server.RealityShortId;

            block["reality"] = realityBlock;
        }

        return block;
    }

    private static JsonObject? BuildTransport(ProxyServer server)
    {
        switch (server.Transport.ToLowerInvariant())
        {
            case "ws":
            {
                var transport = new JsonObject { ["type"] = "ws" };

                if (!string.IsNullOrEmpty(server.Path))
                    transport["path"] = server.Path;

                if (!string.IsNullOrEmpty(server.HostHeader))
                    transport["headers"] = new JsonObject { ["Host"] = server.HostHeader };

                return transport;
            }

            case "grpc":
                return new JsonObject
                {
                    ["type"] = "grpc",
                    ["service_name"] = server.ServiceName ?? string.Empty,
                };

            case "httpupgrade":
            {
                var transport = new JsonObject { ["type"] = "httpupgrade" };

                if (!string.IsNullOrEmpty(server.Path))
                    transport["path"] = server.Path;

                if (!string.IsNullOrEmpty(server.HostHeader))
                    transport["host"] = server.HostHeader;

                return transport;
            }

            case "http":
            {
                var transport = new JsonObject { ["type"] = "http" };

                if (!string.IsNullOrEmpty(server.Path))
                    transport["path"] = server.Path;

                return transport;
            }

            // tcp, udp и прочее транспортной секции не требуют.
            default:
                return null;
        }
    }

    private static JsonObject BuildRoute(
        RuleSet ruleSet,
        IReadOnlyDictionary<ProxyServer, string> tags,
        SingBoxOptions options)
    {
        bool haveServers = tags.Count > 0;
        var rules = new JsonArray
        {
            // Определение протокола по содержимому — нужно, чтобы доменные
            // правила срабатывали и на соединениях без обращения к DNS.
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" },
            new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" },
        };

        if (ruleSet.Operating == OperatingMode.Selective)
        {
            foreach (var rule in ruleSet.Rules)
            {
                var node = BuildRule(rule, tags, options, haveServers);
                if (node is not null)
                    rules.Add(node);
            }
        }

        return new JsonObject
        {
            ["rules"] = rules,
            ["final"] = ResolveFinal(ruleSet, options, haveServers),
            ["auto_detect_interface"] = true,
            // Обязателен начиная с sing-box 1.12: без него запуск падает.
            ["default_domain_resolver"] = new JsonObject { ["server"] = "remote" },
        };
    }

    private static JsonObject? BuildRule(
        RoutingRule rule,
        IReadOnlyDictionary<ProxyServer, string> tags,
        SingBoxOptions options,
        bool haveServers)
    {
        var outbound = ResolveOutbound(rule.Mode, rule.Server, tags, options, haveServers);
        var node = new JsonObject();

        switch (rule.Match)
        {
            case MatchKind.Process:
            {
                // sing-box различает имя файла и полный путь разными полями.
                var value = rule.Value;
                bool isPath = value.Contains('\\') || value.Contains('/');
                var array = new JsonArray { value };

                node[isPath ? "process_path" : "process_name"] = array;
                break;
            }

            case MatchKind.Domain:
            {
                var value = rule.Value;

                if (value.StartsWith("*.", StringComparison.Ordinal))
                {
                    // domain_suffix в sing-box покрывает и сам домен, что совпадает
                    // с семантикой маски "*.example.com" в правилах NetZapret.
                    node["domain_suffix"] = new JsonArray { value[2..] };
                }
                else if (value.Contains('*'))
                {
                    node["domain_regex"] = new JsonArray { GlobToRegex(value) };
                }
                else
                {
                    node["domain"] = new JsonArray { value };
                }

                break;
            }

            case MatchKind.Ip:
                node["ip_cidr"] = new JsonArray { rule.Value };
                break;

            default:
                return null;
        }

        node["outbound"] = outbound;
        return node;
    }

    private static string ResolveFinal(RuleSet ruleSet, SingBoxOptions options, bool haveServers) =>
        ruleSet.Operating switch
        {
            OperatingMode.Off => "direct",
            OperatingMode.ProxyAll => haveServers ? options.SelectorTag : "direct",
            _ => ResolveOutbound(ruleSet.DefaultMode, ruleSet.DefaultServer, null, options, haveServers),
        };

    private static string ResolveOutbound(
        RoutingMode mode,
        string? server,
        IReadOnlyDictionary<ProxyServer, string>? tags,
        SingBoxOptions options,
        bool haveServers)
    {
        if (mode != RoutingMode.Proxy)
            return "direct";

        // Если серверов нет вовсе, прокси-правило превращается в direct:
        // ссылка на несуществующий outbound не даст конфигу даже запуститься.
        if (!haveServers)
            return "direct";

        if (string.IsNullOrWhiteSpace(server) || server.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return options.SelectorTag;

        if (tags is not null)
        {
            foreach (var (candidate, tag) in tags)
            {
                if (tag.Equals(server, StringComparison.OrdinalIgnoreCase)
                    || candidate.Tag.Equals(server, StringComparison.OrdinalIgnoreCase))
                {
                    return tag;
                }
            }
        }

        // Имя сервера из конфига не нашлось среди подписки — безопаснее отдать
        // селектор, чем сослаться на отсутствующий тег.
        return options.SelectorTag;
    }

    private static string GlobToRegex(string glob)
    {
        var builder = new StringBuilder("^");

        foreach (char c in glob)
        {
            if (c == '*')
                builder.Append(".*");
            else if (c == '?')
                builder.Append('.');
            else
                builder.Append(System.Text.RegularExpressions.Regex.Escape(c.ToString()));
        }

        return builder.Append('$').ToString();
    }
}

public sealed record CompilationResult
{
    public required string Json { get; init; }

    public required IReadOnlyList<ProxyServer> UsedServers { get; init; }

    /// <summary>Серверы, пропущенные из-за неподдерживаемого транспорта.</summary>
    public required IReadOnlyList<ProxyServer> SkippedServers { get; init; }

    public required IReadOnlyDictionary<ProxyServer, string> ServerTags { get; init; }
}
