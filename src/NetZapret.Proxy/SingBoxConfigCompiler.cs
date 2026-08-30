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

    /// <summary>
    /// MTU туннеля.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Задаётся явно, потому что без этого адаптер поднимался с MTU 65535 при
    /// физическом интерфейсе в 1500. Приложение отдавало в туннель огромные
    /// сегменты, наружу они уходили по пути с обычным MTU и рассыпались:
    /// мелкие запросы проходили, крупные загрузки — нет. В Telegram это
    /// выглядело так, что переписка работает, а фотографии и видео не грузятся
    /// вовсе, и на маршруты это не походило совершенно.
    /// </para>
    /// <para>
    /// 1400 с запасом: из 1500 вычитаются заголовки IP и UDP плюс обвязка
    /// QUIC у Hysteria2 — около восьмидесяти байт. У TCP-транспортов
    /// накладные расходы меньше, так что значение годится и им.
    /// </para>
    /// </remarks>
    public int TunMtu { get; init; } = 1400;

    /// <summary>Адрес TUN-интерфейса для IPv6.</summary>
    /// <remarks>
    /// Без него <c>auto_route</c> не ставит ни одного маршрута IPv6, и всё,
    /// что перечислено в <c>route_address</c> из адресов IPv6, туда не
    /// попадает. На практике это означало, что резолвер провайдера
    /// (адрес вида <c>fd7d:…::1</c>) оставался вне туннеля и отвечал
    /// настоящими адресами в обход fakeip — то есть выборочный перехват
    /// обходился через IPv6, даже когда с IPv4 всё было верно.
    /// Диапазон взят из документации sing-box.
    /// </remarks>
    public string TunAddressV6 { get; init; } = "fdfe:dcba:9876::1/126";

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

    /// <summary>
    /// Разрешать имена через туннель, а не напрямую.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ответ на блокировку DoH и DoT у российских операторов: запрос,
    /// уходящий внутри туннеля, для провайдера неотличим от прочего трафика,
    /// и перекрыть его отдельно нельзя.
    /// </para>
    /// <para>
    /// По умолчанию выключено, и намеренно. Включённое, оно ставит разрешение
    /// имён в зависимость от туннеля: пока тот не поднялся или лёг, не
    /// открывается вообще ничего, включая то, что прекрасно работало
    /// на десинке. Это хуже исходной беды, поэтому включать стоит тогда,
    /// когда прямой DoH уже перестал отвечать, а не заранее.
    /// </para>
    /// <para>
    /// При включении добавляется отдельный резолвер <c>bootstrap</c> для имён
    /// самих серверов подписки — иначе получается замкнутый круг: чтобы
    /// разрешить имя сервера, нужен туннель, а чтобы поднять туннель, нужно
    /// разрешить имя.
    /// </para>
    /// </remarks>
    public bool DnsThroughTunnel { get; init; }

    /// <summary>Тег селектора, к которому обращаются правила с <c>server: auto</c>.</summary>
    public string SelectorTag { get; init; } = "auto";

    /// <summary>
    /// Не пускать отечественные серверы в автоподбор.
    /// </summary>
    /// <remarks>
    /// Автоподбор идёт по задержке, а меньше всего она у ближайшего сервера —
    /// своего же. Для обхода блокировок оператора он годится, а для сервисов,
    /// закрывающихся от страны, бесполезен: адрес остаётся отечественным.
    /// Закрепить конкретный сервер вручную это не мешает — селектор
    /// по-прежнему знает их все.
    /// </remarks>
    public bool ForeignExitsOnly { get; init; }

    /// <summary>
    /// Адреса для имён, у которых своего адреса нет.
    /// </summary>
    /// <remarks>
    /// Отвечает наш собственный резолвер, системный <c>hosts</c> не трогается.
    /// Разница существенная: hosts требует прав администратора, переживает
    /// удаление программы и виден всем приложениям, а это — одна строка
    /// в нашем конфиге, снимаемая вместе с ним.
    /// </remarks>
    public IReadOnlyDictionary<string, string> AddressOverrides { get; init; } =
        new Dictionary<string, string>();

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
    /// Адреса, прибитые в hosts к доменам с правилом <c>mode: proxy</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Заводятся в туннель <b>и</b> получают правило на прокси —둘 вместе,
    /// одним полем, потому что порознь получается хуже, чем ничего.
    /// </para>
    /// <para>
    /// Так и вышло в первой редакции: адрес заводился в перехват, правила
    /// ему не доставалось, и он уходил в <c>direct</c> по умолчанию. То есть
    /// трафик входил в туннель и выходил из него переоткрытым сокетом —
    /// до VPN не доезжал, а десинк ломался, потому что WinDivert видел уже
    /// не исходный поток приложения. Canva в логе давала на этом сотни
    /// оборванных соединений подряд.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> PinnedProxyAddresses { get; init; } = Array.Empty<string>();

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
            ["dns"] = BuildDns(options, CollectProxyDomains(ruleSet), tags.Count > 0),
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

    /// <summary>Резолвер для имён самих серверов подписки, всегда в обход туннеля.</summary>
    private const string BootstrapTag = "bootstrap";

    private const string OverrideTag = "pinned";

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

        WriteStamp(path);
    }

    /// <summary>Путь к отметке о том, кто собрал конфиг.</summary>
    public static string StampPathFor(string configPath) => configPath + ".built-by";

    /// <summary>
    /// Оставляет рядом с конфигом отметку о собравшей его версии.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отдельным файлом, а не полем внутри: sing-box разбирает свой конфиг
    /// строго и на незнакомый ключ ругается.
    /// </para>
    /// <para>
    /// Заведено потому, что развёрнутых копий бывает несколько, и старая молча
    /// пересобирает конфиг по-своему. Трижды за один день выходило так, что
    /// исправление внесено, конфиг свежий по времени, а собрала его прошлая
    /// версия — и поле, которого она не знает, в файл не попадало. По времени
    /// это не видно никак: конфиг пересобирается при каждом запуске.
    /// </para>
    /// </remarks>
    private static void WriteStamp(string configPath)
    {
        try
        {
            var version = typeof(SingBoxConfigCompiler).Assembly.GetName().Version?.ToString() ?? "неизвестна";

            File.WriteAllText(
                StampPathFor(configPath),
                $"{version}\n{DateTimeOffset.Now:O}\n",
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Отметка — удобство, а не условие работы.
        }
    }

    /// <summary>
    /// Возвращает версию, собравшую конфиг; <c>null</c> — отметки нет.
    /// </summary>
    public static string? ReadStamp(string configPath)
    {
        try
        {
            var stamp = StampPathFor(configPath);

            return File.Exists(stamp)
                ? File.ReadAllLines(stamp).FirstOrDefault()
                : null;
        }
        catch (IOException)
        {
            return null;
        }
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

        var proxied = ruleSet.Rules.Where(r => r.Mode == RoutingMode.Proxy);

        // Домены из правил по одному имени и из списков — вперемешку: для
        // fakeip разницы нет, а для человека список это тот же набор доменов,
        // только записанный одной строкой.
        var domains = proxied
            .Where(r => r.Match == MatchKind.Domain)
            .Select(r => r.Value)
            .Concat(proxied
                .Where(r => r.Match == MatchKind.HostList)
                .SelectMany(r => r.HostListDomains));

        return domains
            .Select(v => v.StartsWith("*.", StringComparison.Ordinal) ? v[2..] : v)
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

    private static JsonObject BuildDns(
        SingBoxOptions options,
        IReadOnlyList<string> proxyDomains,
        bool haveServers)
    {
        var remote = new JsonObject
        {
            ["tag"] = "remote",
            ["type"] = options.DnsServerType,
            ["server"] = options.DnsServer,
        };

        // Через туннель — только если ему есть куда вести. Иначе detour
        // указывал бы на селектор без единого сервера, и разрешение имён
        // умерло бы целиком.
        bool throughTunnel = options.DnsThroughTunnel && haveServers;

        if (throughTunnel)
            remote["detour"] = options.SelectorTag;

        var servers = new JsonArray
        {
            remote,
            new JsonObject
            {
                ["tag"] = "fake",
                ["type"] = "fakeip",
                ["inet4_range"] = options.FakeIpV4Range,
                ["inet6_range"] = options.FakeIpV6Range,
            },
        };

        if (throughTunnel)
        {
            // Имена самих серверов подписки разрешаются в обход туннеля,
            // иначе замкнутый круг: чтобы поднять туннель, нужно разрешить
            // имя сервера, а чтобы разрешить — уже иметь туннель.
            servers.Add(new JsonObject
            {
                ["tag"] = BootstrapTag,
                ["type"] = "local",
            });
        }

        // Подставленные адреса идут отдельным резолвером типа hosts. Первым
        // правилом, раньше fakeip: у такого имени настоящего адреса нет,
        // и отдать вместо него fakeip значило бы завести соединение в туннель,
        // где его точно так же не с кем установить.
        if (options.AddressOverrides.Count > 0)
        {
            var predefined = new JsonObject();

            foreach (var (host, address) in options.AddressOverrides)
                predefined[host] = new JsonArray { address };

            servers.Add(new JsonObject
            {
                ["tag"] = OverrideTag,
                ["type"] = "hosts",
                ["predefined"] = predefined,
            });
        }

        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["independent_cache"] = true,
        };

        // Fakeip выдаёт каждому домену собственный адрес, поэтому соответствие
        // «адрес → домен» точное 1:1, и доменные правила работают без догадок
        // по реальным адресам CDN.
        var rules = new JsonArray();

        if (options.AddressOverrides.Count > 0)
        {
            var names = new JsonArray();

            foreach (var host in options.AddressOverrides.Keys)
                names.Add(host);

            rules.Add(new JsonObject
            {
                ["domain"] = names,
                ["server"] = OverrideTag,
            });
        }

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
            ["address"] = new JsonArray { options.TunAddress, options.TunAddressV6 },
            ["mtu"] = options.TunMtu,
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

            // Прибитые в hosts адреса проксируемых доменов. Правило на прокси
            // для них добавляется в BuildRoute — заводить сюда без правила
            // означает отправить их в direct переоткрытым сокетом.
            foreach (var cidr in options.PinnedProxyAddresses)
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

        // Домашние выходы можно вывести из автоподбора. Иначе он неизбежно
        // на них и оседает: выбор идёт по задержке, а ближайший сервер —
        // свой же. Для обхода блокировки оператора это годится, для сервисов,
        // закрывающихся от страны, — нет: адрес остаётся отечественным,
        // и отказ приходит тот же, что и без туннеля.
        var eligible = options.ForeignExitsOnly
            ? servers.Where(s => !LooksDomestic(tags[s])).ToList()
            : servers;

        // Но не до пустоты: подписка может состоять из домашних серверов
        // целиком, и группа без единого участника не даст конфигу
        // запуститься вовсе.
        if (eligible.Count == 0)
            eligible = servers;

        var members = new JsonArray();
        foreach (var server in eligible)
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

    /// <summary>
    /// Похож ли сервер на отечественный по его названию.
    /// </summary>
    /// <remarks>
    /// <para>
    /// По названию, потому что другого источника нет: страну выхода знает
    /// только сам сервер, а спрашивать её у каждого — это подключение к каждому
    /// перед сборкой конфига. Названия ставит поставщик подписки, и флаг
    /// с «Россия» в них встречается у всех, кого доводилось видеть.
    /// </para>
    /// <para>
    /// Способ приблизительный, и это признаётся вслух: «США (вход РФ)» назван
    /// американским, а выходит, по замерам, в России. Поэтому исключение —
    /// не гарантия, а отсев очевидного; страну выхода проверка блокировок
    /// всё равно спрашивает отдельно и говорит, если та не та.
    /// </para>
    /// </remarks>
    internal static bool LooksDomestic(string tag) =>
        tag.Contains("🇷🇺", StringComparison.Ordinal)
        || tag.Contains("Россия", StringComparison.OrdinalIgnoreCase)
        || tag.Contains("Russia", StringComparison.OrdinalIgnoreCase);

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
        };

        foreach (var node in BuildDohRejects(options))
            rules.Add(node);

        rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });

        // В режиме «всё через VPN» правила не отбрасываются целиком, как было
        // раньше: исключения на прямой проход обязаны пережить его. Российские
        // сервисы через зарубежный адрес не работают вовсе — банки и госуслуги
        // потребуют подтверждений, часть откажет наотрез, — и «всё через VPN»
        // без этой оговорки означает «интернет наполовину сломан».
        // Остальные правила при этом действительно лишние: всё, что не выведено
        // явно, и так уходит в туннель по final.
        IReadOnlyList<RoutingRule> applicable = ruleSet.Operating switch
        {
            OperatingMode.Selective => ruleSet.Rules,
            OperatingMode.ProxyAll => ruleSet.Rules.Where(r => r.Mode == RoutingMode.Direct).ToList(),

            // ProxyStrict — «без исключений» буквально: ни одного правила,
            // включая прямые. Российские сервисы при этом сломаются, и это
            // осознанный выбор режима, а не недосмотр.
            _ => Array.Empty<RoutingRule>(),
        };

        // Раньше пользовательских правил: адрес прибит в hosts именно потому,
        // что домен иначе не разрешается, и общее правило по подсети не должно
        // перехватить его первым.
        if (options.PinnedProxyAddresses.Count > 0 && ruleSet.Operating != OperatingMode.Off)
        {
            var pinned = new JsonArray();
            foreach (var cidr in options.PinnedProxyAddresses)
                pinned.Add(cidr);

            rules.Add(new JsonObject
            {
                ["ip_cidr"] = pinned,
                ["outbound"] = haveServers ? options.SelectorTag : "direct",
            });
        }

        foreach (var rule in applicable)
        {
            var node = BuildRule(rule, tags, options, haveServers);
            if (node is not null)
                rules.Add(node);
        }

        return new JsonObject
        {
            ["rules"] = rules,
            ["final"] = ResolveFinal(ruleSet, options, haveServers),
            ["auto_detect_interface"] = true,

            // Обязателен начиная с sing-box 1.12: без него запуск падает.
            //
            // Когда remote ходит через туннель, разрешать по нему имена самих
            // серверов подписки нельзя — они нужны, чтобы туннель поднялся.
            // Для этого и заведён bootstrap.
            ["default_domain_resolver"] = new JsonObject
            {
                ["server"] = options.DnsThroughTunnel && haveServers ? BootstrapTag : "remote",
            },
        };
    }

    /// <summary>
    /// Закрывает DNS over HTTPS к системным резолверам, чтобы Windows
    /// вернулась на обычный UDP, который мы умеем перехватывать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Без этого вся схема выборочного перехвата рушится в первом звене.
    /// Windows 11 включает DoH сама: запрос уходит по TCP/443 на dns.google,
    /// правило <c>hijack-dns</c> его не видит (оно ловит DNS на UDP/53),
    /// домен резолвится в настоящий адрес, fakeip не выдаётся, и трафик,
    /// который должен был уйти в туннель, идёт напрямую под блокировку.
    /// </para>
    /// <para>
    /// Отказ безопасен, потому что в системе стоит <c>DohFlags = 6</c> —
    /// DoH с разрешённым откатом на открытый UDP. Получив RST, резолвер
    /// переспрашивает по 53 порту, и запрос попадает к нам. Приватность
    /// при этом не страдает: наружу запрос всё равно уходит по DoH, только
    /// его делает sing-box, а не сама Windows.
    /// </para>
    /// <para>
    /// Правило ограничено входящим <c>tun-in</c> намеренно. Наш собственный
    /// DNS-сервер — тоже DoH и тоже на 8.8.8.8; без этой оговорки отказ
    /// накрыл бы его самого, и резолвить стало бы нечем.
    /// </para>
    /// </remarks>
    private static IEnumerable<JsonObject> BuildDohRejects(SingBoxOptions options)
    {
        // Вне выборочного режима резолверы в туннель не заводятся,
        // и перехватывать нечего.
        if (options.Scope != TunnelScope.ProxyOnly || options.DnsServerAddresses.Count == 0)
            yield break;

        var addresses = new JsonArray();
        foreach (var address in options.DnsServerAddresses)
            addresses.Add(address);

        // Адрес DoH-эндпоинта совпадает с адресом обычного резолвера:
        // Windows поднимает 8.8.8.8 до https://dns.google/dns-query,
        // а тот живёт там же. Поэтому отдельного списка не требуется.
        yield return new JsonObject
        {
            ["inbound"] = new JsonArray { "tun-in" },
            ["ip_cidr"] = addresses,
            ["port"] = new JsonArray { 443 },
            ["action"] = "reject",
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

            case MatchKind.HostList:
            {
                // Непрогруженный список означает, что файл не нашёлся.
                // Правило без доменов совпало бы со всем подряд, поэтому
                // выбрасываем его целиком — о пропаже уже сказал разворот.
                if (rule.HostListDomains.Count == 0)
                    return null;

                var suffixes = new JsonArray();

                foreach (var domain in rule.HostListDomains)
                {
                    // Записи в списках Zapret — это зоны: там пишут
                    // discord.media, подразумевая и russia1234.discord.media.
                    suffixes.Add(domain.StartsWith("*.", StringComparison.Ordinal) ? domain[2..] : domain);
                }

                node["domain_suffix"] = suffixes;
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
            // Десинк туннеля не касается вовсе, так что для sing-box этот
            // режим неотличим от выключенного.
            OperatingMode.Off or OperatingMode.DesyncOnly => "direct",
            OperatingMode.ProxyAll or OperatingMode.ProxyStrict =>
                haveServers ? options.SelectorTag : "direct",
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
