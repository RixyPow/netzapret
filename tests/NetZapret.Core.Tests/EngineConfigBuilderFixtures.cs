using System.IO;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;

namespace NetZapret.Core.Tests;

/// <summary>
/// Общее для проверок сборки конфига: правила всех видов, серверы, замеры.
/// </summary>
/// <remarks>
/// Правила — каждого вида, что понимает компилятор: имя, маска, маска
/// посередине, список имён (и перечень-реестр), список адресов, адрес,
/// программа файлом и пакетом, пул. Иначе сверка «байт в байт» прошла бы
/// мимо ветки, которую никто не задел.
/// </remarks>
internal static class EngineConfigBuilderFixtures
{
    public const string PoolId = "p1";

    public static RuleSet RuleFixture(OperatingMode mode = OperatingMode.Selective)
    {
        var engine = RuleSetLoader.Load($$"""
            mode: selective
            rules:
              - match: domain
                value: "chatgpt.com"
                mode: proxy
              - match: domain
                value: "*.claude.ai"
                mode: proxy
                server: "pool:{{PoolId}}"
              - match: domain
                value: "rutracker.org"
                mode: desync
              - match: domain
                value: "*.gosuslugi.ru"
                mode: direct
              - match: hostlist
                value: "config/lists/discord.txt"
                mode: proxy
              - match: hostlist
                value: "{{Core.Services.ServiceCatalog.RegistryList}}"
                mode: desync
              - match: ipset
                value: "config/lists/ipset-games.txt"
                mode: desync
              - match: ip
                value: "149.154.160.0/20"
                mode: proxy
              - match: process
                value: "telegram.exe"
                mode: proxy
              - match: process
                value: "org.telegram.messenger"
                mode: desync
              - match: domain
                value: "cdn*.example.org"
                mode: proxy
              - match: domain
                value: "*.ru"
                mode: direct
            default:
              mode: desync
            """);

        foreach (var rule in engine.RuleSet.Rules)
        {
            if (rule.Value == "config/lists/discord.txt")
            {
                rule.LoadHostList(["discord.com", "*.discord.gg", "discordapp.net"]);
                rule.LoadHostListAddresses(["162.159.128.0/24", "35.207.81.249"]);
            }
            else if (rule.Value == Core.Services.ServiceCatalog.RegistryList)
            {
                // tvrain.ru — имя реестра в российской зоне: ниже его забрало бы
                // «*.ru — напрямую» (у реестра таких 37 085 из 117 157, 27.10).
                rule.LoadHostList(["rutracker.org", "kinozal.tv", "linkedin.com", "*.nnmclub.to", "tvrain.ru"]);
            }
            else if (rule.Value == "config/lists/ipset-games.txt")
            {
                rule.LoadIpSet(["185.40.64.0/22", "2a03:2880::/32"]);
            }
        }

        return engine.RuleSet with { Operating = mode };
    }

    public static IReadOnlyList<ProxyServer> Servers() =>
    [
        new ProxyServer
        {
            Protocol = ProxyProtocol.Vless,
            Tag = "🇩🇪 Германия",
            Host = "de.example.net",
            Port = 443,
            Credential = "00000000-0000-0000-0000-000000000001",
            Security = "tls",
            Sni = "de.example.net",
            Fingerprint = "chrome",
        },
        new ProxyServer
        {
            Protocol = ProxyProtocol.Trojan,
            Tag = "🇫🇮 Финляндия",
            Host = "fi.example.net",
            Port = 443,
            Credential = "PLACEHOLDER",
            Security = "tls",
            Sni = "fi.example.net",
        },
        new ProxyServer
        {
            Protocol = ProxyProtocol.Hysteria2,
            Tag = "🇳🇱 Нидерланды",
            Host = "nl.example.net",
            Port = 8443,
            Credential = "PLACEHOLDER",
            Security = "tls",
            HopPorts = "20000:30000",
        },
        new ProxyServer
        {
            Protocol = ProxyProtocol.Shadowsocks,
            Tag = "🇷🇺 Москва",
            Host = "203.0.113.10",
            Port = 8388,
            Credential = "PLACEHOLDER",
            Method = "chacha20-ietf-poly1305",
        },
    ];

    public static IReadOnlyList<ServerPool> Pools() =>
        [new ServerPool(PoolId, "Пул", ["🇩🇪 Германия", "🇳🇱 Нидерланды"])];

    /// <summary>Замеры: один мёртвый, один «через раз», у двух — задержка.</summary>
    public static ServerHealthCache Health(string directory)
    {
        var path = Path.Combine(directory, "server-health.json");

        File.WriteAllText(path, """
            [
              { "Tag": "🇫🇮 Финляндия", "Success": false, "CheckedAt": "2026-10-27T00:00:00+03:00",
                "Failures": 3, "Recent": [false, false, false] },
              { "Tag": "🇩🇪 Германия", "Success": true, "LatencyMs": 120, "CheckedAt": "2026-10-27T00:00:00+03:00",
                "Failures": 0, "Recent": [true, false, true, false, false, true] },
              { "Tag": "🇳🇱 Нидерланды", "Success": true, "LatencyMs": 80, "CheckedAt": "2026-10-27T00:00:00+03:00",
                "Failures": 0, "Recent": [true, true, true] }
            ]
            """);

        return ServerHealthCache.Load(path);
    }

    public static AppSettings Settings() =>
        new AppSettings
        {
            SubscriptionUrl = "https://panel.example.invalid/sub/token",
            PresetName = "Universal V12 Lite",
        }.With(new EngineChoice { Desync = true, Tunnel = true });

    public static RouterDnsConnection.Selection RouterSelection(AppSettings settings) =>
        new(settings, settings.UseRouterDns
            ? new RouterDnsConnection.Lan("Ethernet", 7, System.Net.IPAddress.Parse("192.168.1.10"), 24)
            : null, null);

    public static PinnedProxy Pinned() =>
        new(["87.228.47.200/32"], ["chatgpt.com", "ab.chatgpt.com"]);

    public static IReadOnlyDictionary<string, string> Overrides() =>
        new Dictionary<string, string> { ["tr.rbxcdn.com"] = "104.18.40.10" };

    /// <summary>Хозяин Windows без обращений к системе: всё, что он знает, задано.</summary>
    public static EngineHost WindowsHost(bool ipv6, IReadOnlyList<string> resolvers) => new()
    {
        Platform = SingBoxPlatform.Windows,
        Ipv6 = () => ipv6,
        SystemResolvers = () => resolvers,
        LoadRules = _ => (RuleFixture(), null),
        ExpandAddressLists = (entries, _) => entries,
        Subscriptions = () => SubscriptionSources.Empty,
    };

    /// <summary>Хозяин телефона; файл пинов — в переданной папке.</summary>
    public static EngineHost AndroidHost(string directory, AndroidEngine? android = null, RuleSet? rules = null) => new()
    {
        Platform = SingBoxPlatform.Android,
        Ipv6 = () => true,
        SystemResolvers = () => [],
        LoadRules = _ => (rules ?? RuleFixture(), null),
        ExpandAddressLists = (entries, _) => entries,
        Subscriptions = () => SubscriptionSources.Empty,
        Android = android ?? new AndroidEngine
        {
            HostsFilePath = Path.Combine(directory, "config", "hosts"),
            ExcludePackages = ["org.netzapret.android", "ru.sberbankmobile"],
            Socks = new DesyncSocks(41234, "nz", "secret-for-test"),
            DesyncPassHosts = ["github.com", "riotgames.com"],
        },
    };
}
