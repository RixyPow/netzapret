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

    /// <summary>
    /// Адрес для замера задержки. Запрос уходит через сам прокси, поэтому
    /// проверяется не доступность сайта, а работоспособность сервера.
    /// </summary>
    public string LatencyTestUrl { get; init; } = "https://www.gstatic.com/generate_204";

    /// <summary>Как часто перезамерять задержку.</summary>
    public string LatencyTestInterval { get; init; } = "3m";

    /// <summary>
    /// На сколько миллисекунд новый сервер должен опережать текущий,
    /// чтобы произошло переключение.
    /// </summary>
    /// <remarks>
    /// Без допуска соединения перескакивали бы между серверами при каждом
    /// колебании сети, обрывая уже установленные сессии.
    /// </remarks>
    public int LatencyTolerance { get; init; } = 50;

    /// <summary>
    /// Закреплённый сервер: селектор будет указывать на него, а не на автоподбор.
    /// <c>null</c> — выбирать по задержке.
    /// </summary>
    public string? PreferredServerTag { get; init; }

    public string LogLevel { get; init; } = "warn";

    /// <summary>
    /// Поднимать TUN. Если выключено, вместо туннеля создаётся локальный
    /// инбаунд <c>mixed</c> на <see cref="LocalListenPort"/>.
    /// </summary>
    /// <remarks>
    /// Режим без TUN нужен для проверки и отладки: он не требует прав
    /// администратора и не трогает маршруты. Правила маршрутизации при этом
    /// работают ровно те же, так что поведение движка можно проверить
    /// до того, как поднимать туннель.
    /// </remarks>
    public bool UseTun { get; init; } = true;

    public int LocalListenPort { get; init; } = 21080;

    /// <summary>Что именно заводить в туннель.</summary>
    public TunnelScope Scope { get; init; } = TunnelScope.Everything;

    /// <summary>
    /// Адреса резолверов, которые нужно завести в туннель при
    /// <see cref="TunnelScope.ProxyOnly"/>.
    /// </summary>
    /// <remarks>
    /// Без них fakeip не работает: чтобы подменить ответ, sing-box должен
    /// увидеть запрос, а при выборочном перехвате запросы к резолверу
    /// в туннель не попадают. Заводим только сами адреса резолверов —
    /// перехват остаётся выборочным.
    /// </remarks>
    public IReadOnlyList<string> DnsServerAddresses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Подсети, покрытые десинком: они выводятся из туннеля через
    /// <c>route_exclude_address</c>. Источник — файлы <c>--ipset</c> пресета.
    /// </summary>
    public IReadOnlyList<string> DesyncAddresses { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Дополнительные префиксы для перехвата: развёрнутая секция <c>capture</c>
    /// из конфига правил.
    /// </summary>
    public IReadOnlyList<string> CaptureAddresses { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Какой трафик перехватывает TUN.
/// </summary>
public enum TunnelScope
{
    /// <summary>
    /// Весь трафик, дальше решают правила маршрутизации.
    /// </summary>
    /// <remarks>
    /// Простой вариант, но именно он ломает часть рецептов Zapret: трафик
    /// десинка проходит через туннель и выходит переоткрытым сокетом sing-box,
    /// а WinDivert рассчитывает увидеть исходный поток приложения.
    /// </remarks>
    Everything,

    /// <summary>
    /// Только то, что идёт в прокси.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Реализовано инверсией: вместо «исключить весь десинк» — «завести только
    /// прокси». Опирается на fakeip: адрес из диапазона fakeip выдаётся лишь
    /// доменам, у которых правило <c>mode: proxy</c>, а <c>route_address</c>
    /// у TUN ограничен этим диапазоном. Всё прочее туннеля не касается вовсе,
    /// и WinDivert видит исходные потоки.
    /// </para>
    /// <para>
    /// Почему не через <c>route_exclude_address</c> по спискам пресета: из
    /// 20 секций Universal V6 с рецептом <c>fake</c> четырнадцать сопоставляются
    /// только по доменам, а поле принимает лишь адреса. Разрешать 117 тысяч
    /// доменов заранее неосуществимо. Подробнее — docs/coexistence.md.
    /// </para>
    /// </remarks>
    ProxyOnly,
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
            ["dns"] = BuildDns(options, CollectProxyDomains(ruleSet)),
            ["inbounds"] = BuildInbounds(options, CollectProxyAddresses(ruleSet)),
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
    /// Собирает минимальный конфиг для проверки одного сервера.
    /// </summary>
    /// <remarks>
    /// Ни TUN, ни fakeip, ни маршрутов: только локальный инбаунд <c>mixed</c>
    /// (он же SOCKS, он же HTTP-прокси) и один исходящий. Такой конфиг
    /// запускается без прав администратора и не трогает систему — именно то,
    /// что нужно, чтобы отделить неисправность транспорта от проблем
    /// маршрутизации.
    /// </remarks>
    private const string ProbeDnsServer = "8.8.8.8";

    /// <summary>Тег автоподбора по задержке; спрятан за селектором.</summary>
    private const string LatencyTag = "auto-latency";

    public string CompileProbeConfig(ProxyServer server, int listenPort, string? logPath, string logLevel = "debug")
    {
        var log = new JsonObject { ["level"] = logLevel, ["timestamp"] = true };

        if (!string.IsNullOrEmpty(logPath))
            log["output"] = Path.GetFullPath(logPath);

        var root = new JsonObject
        {
            ["log"] = log,
            ["inbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "probe-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = listenPort,
                },
            },
            ["outbounds"] = new JsonArray
            {
                BuildOutbound(server, "probe-out"),
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" },
            },
            ["route"] = new JsonObject
            {
                ["rules"] = new JsonArray
                {
                    // Запросы к резолверу обязаны идти мимо прокси. Иначе выходит
                    // замкнутый круг: чтобы разрешить имя сервера, нужен прокси,
                    // а чтобы поднять прокси — разрешить имя.
                    //
                    // Через detour у DNS-сервера это не выражается: sing-box 1.13
                    // отвергает detour в пустой direct-исходящий с формулировкой
                    // «makes no sense». Поэтому развязка сделана правилом маршрута.
                    new JsonObject
                    {
                        ["ip_cidr"] = new JsonArray { ProbeDnsServer + "/32" },
                        ["outbound"] = "direct",
                    },
                },
                ["final"] = "probe-out",
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = new JsonObject { ["server"] = "probe-dns" },
            },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["tag"] = "probe-dns",
                        ["type"] = "udp",
                        ["server"] = ProbeDnsServer,
                    },
                },
            },
        };

        return root.ToJsonString(SerializerOptions);
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

    /// <summary>
    /// Домены, которые по правилам уходят в прокси. При выборочном перехвате
    /// только им выдаётся адрес fakeip — и только они попадают в туннель.
    /// </summary>
    private static IReadOnlyList<string> CollectProxyDomains(RuleSet ruleSet)
    {
        if (ruleSet.Operating != OperatingMode.Selective)
            return Array.Empty<string>();

        return ruleSet.Rules
            .Where(r => r.Mode == RoutingMode.Proxy && r.Match == MatchKind.Domain)
            .Select(r => r.Value.StartsWith("*.", StringComparison.Ordinal) ? r.Value[2..] : r.Value)
            .Where(v => !v.Contains('*'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> CollectProxyAddresses(RuleSet ruleSet)
    {
        if (ruleSet.Operating != OperatingMode.Selective)
            return Array.Empty<string>();

        return ruleSet.Rules
            .Where(r => r.Mode == RoutingMode.Proxy && r.Match == MatchKind.Ip)
            .Select(r => r.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static JsonObject BuildDns(SingBoxOptions options, IReadOnlyList<string> proxyDomains)
    {
        var dns = new JsonObject
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
            ["independent_cache"] = true,
        };

        // Fakeip выдаёт каждому домену собственный адрес, поэтому соответствие
        // «адрес → домен» точное 1:1, и доменные правила работают без догадок
        // по реальным адресам CDN.
        var rules = new JsonArray();

        if (options.Scope == TunnelScope.ProxyOnly && proxyDomains.Count > 0)
        {
            // Выборочный fakeip: подменяем ответ только тем доменам, которые
            // идут в прокси. Всем остальным отдаётся настоящий адрес, и их
            // трафик в туннель не попадает — этим и достигается развод
            // с десинком.
            var suffixes = new JsonArray();
            foreach (var domain in proxyDomains)
                suffixes.Add(domain);

            rules.Add(new JsonObject
            {
                ["domain_suffix"] = suffixes,
                ["query_type"] = new JsonArray { "A", "AAAA" },
                ["server"] = "fake",
            });
        }
        else if (options.Scope != TunnelScope.ProxyOnly)
        {
            rules.Add(new JsonObject
            {
                ["query_type"] = new JsonArray { "A", "AAAA" },
                ["server"] = "fake",
            });
        }

        dns["rules"] = rules;
        dns["final"] = "remote";

        return dns;
    }

    private static JsonArray BuildInbounds(SingBoxOptions options, IReadOnlyList<string> proxyAddresses)
    {
        if (!options.UseTun)
        {
            return new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "mixed",
                    ["tag"] = "local-in",
                    ["listen"] = "127.0.0.1",
                    ["listen_port"] = options.LocalListenPort,
                },
            };
        }

        return BuildTunInbound(options, proxyAddresses);
    }

    private static JsonArray BuildTunInbound(SingBoxOptions options, IReadOnlyList<string> proxyAddresses)
    {
        var tun = new JsonObject
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
        };

        if (options.Scope == TunnelScope.ProxyOnly)
        {
            var captured = new JsonArray
            {
                options.FakeIpV4Range,
                options.FakeIpV6Range,
            };

            // Адреса резолверов заводим в туннель намеренно: без перехвата
            // запроса fakeip нечего подменять. Правило hijack-dns в маршрутах
            // стоит раньше исключения приватных сетей, поэтому запрос к роутеру
            // тоже будет перехвачен.
            foreach (var address in options.DnsServerAddresses)
                captured.Add(address);

            // Правила mode: proxy по адресу тоже должны попадать в туннель:
            // им fakeip не выдаётся, домена у них нет.
            foreach (var cidr in proxyAddresses)
                captured.Add(cidr);

            // Секция capture из конфига: диапазоны приложений, которые ходят
            // по голым адресам без DNS. Без них правило по процессу для такого
            // приложения не сработает — его трафик просто не дойдёт до туннеля.
            foreach (var cidr in options.CaptureAddresses)
                captured.Add(cidr);

            tun["route_address"] = captured;
        }

        if (options.DesyncAddresses.Count > 0)
        {
            // Подсети из ipset-списков пресета. При выборочном перехвате они уже
            // и так вне туннеля, но исключение остаётся страховкой: правило
            // mode: proxy может случайно накрыть адрес, который чинит десинк.
            var excluded = new JsonArray();
            foreach (var cidr in options.DesyncAddresses)
                excluded.Add(cidr);

            tun["route_exclude_address"] = excluded;
        }

        return new JsonArray { tun };
    }

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

        if (servers.Count == 0)
            return outbounds;

        var members = new JsonArray();
        foreach (var server in servers)
            members.Add(tags[server]);

        // urltest сам опрашивает серверы и выбирает быстрейший из живых.
        // Статический список этого не умеет: раньше по умолчанию брался
        // первый сервер подписки, и если он мёртв — а у провайдера
        // это обычное дело, — прокси не работал вовсе.
        outbounds.Add(new JsonObject
        {
            ["type"] = "urltest",
            ["tag"] = LatencyTag,
            ["outbounds"] = members,
            ["url"] = options.LatencyTestUrl,
            ["interval"] = options.LatencyTestInterval,
            ["tolerance"] = options.LatencyTolerance,
        });

        // Поверх — селектор: только он позволяет закрепить конкретный сервер
        // вручную через Clash API. По умолчанию указывает на автоподбор.
        var selectorMembers = new JsonArray { LatencyTag };
        foreach (var server in servers)
            selectorMembers.Add(tags[server]);

        // Закреплённый сервер должен существовать: тег из настроек мог остаться
        // от прежней подписки, а ссылка на отсутствующий outbound не даст
        // конфигу даже запуститься.
        var pinned = options.PreferredServerTag is { } wanted
            ? servers.Select(s => tags[s]).FirstOrDefault(t => t.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            : null;

        outbounds.Add(new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = options.SelectorTag,
            ["outbounds"] = selectorMembers,
            ["default"] = pinned ?? LatencyTag,
        });

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

            case MatchKind.IpSet:
            {
                // Непрогруженный список означает, что файл не нашёлся.
                // Правило без адресов совпало бы с чем попало, поэтому
                // безопаснее его не выпускать вовсе.
                if (rule.IpSetCidrs.Count == 0)
                    return null;

                var cidrs = new JsonArray();
                foreach (var cidr in rule.IpSetCidrs)
                    cidrs.Add(cidr);

                node["ip_cidr"] = cidrs;
                break;
            }

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
