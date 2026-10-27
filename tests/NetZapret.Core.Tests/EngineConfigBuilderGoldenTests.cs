using System.IO;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;
using static NetZapret.Core.Tests.EngineConfigBuilderFixtures;

namespace NetZapret.Core.Tests;

/// <summary>
/// Сборка конфига переехала из окна в библиотеку (27.10) — Windows-конфиг тот же, байт в байт.
/// </summary>
/// <remarks>
/// <para>
/// Слева — инициализатор параметров из прежнего <c>TunnelConfig.BuildAsync</c>
/// окна, перенесённый сюда как был (чтения файлов заменены теми же данными),
/// справа — <see cref="EngineConfigBuilder.TunnelOptions"/>. Оба собираются
/// одним компилятором, и JSON должен совпасть до символа на всех сочетаниях:
/// охват, путь DNS, DNS роутера, WARP, пулы, IPv6, память fakeip, нарезка.
/// </para>
/// <para>
/// Что новый компилятор с теми же параметрами пишет то же, что прежний,
/// сверено отдельно (27.10, прежний компилятор рядом во временной копии) —
/// здесь сверяется переезд параметров.
/// </para>
/// </remarks>
public sealed class EngineConfigBuilderGoldenTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"nz-golden-{Guid.NewGuid():N}");

    public EngineConfigBuilderGoldenTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static readonly IReadOnlyList<string> Resolvers = ["192.168.1.1/32", "fd7d:1::1/128"];
    private static readonly IReadOnlyList<string> BookUrls = ["https://panel.example.invalid/sub/token", "https://other.example.invalid/s/2"];

    [Fact]
    public void Windows_config_is_byte_identical_to_the_one_the_window_built()
    {
        var health = Health(_root);
        var dead = health.Dead(new SingBoxOptions().DeadAfterFailures);
        var keys = new EngineKeys("s", "u", "p");
        int checkedCount = 0;

        foreach (var via in new[] { DnsRoute.Direct, DnsRoute.Auto, DnsRoute.Tunnel })
        foreach (var router in new[] { false, true })
        foreach (var warp in new[] { false, true })
        foreach (var pools in new[] { false, true })
        foreach (var narrow in new[] { true, false })
        foreach (var stableFirst in new[] { true, false })
        foreach (var ipv6 in new[] { true, false })
        foreach (var fakeIpMemory in new[] { true, false })
        foreach (var fragment in new[] { TlsFragment.Off, TlsFragment.Records })
        {
            var settings = Settings() with
            {
                DnsVia = via,
                UseRouterDns = router,
                RouterDnsAddress = "192.168.1.1",
                WarpEnabled = warp,
                SelectiveRouting = pools,
                Pools = pools ? Pools() : [],
                StableFirst = stableFirst,
                FakeIpMemory = fakeIpMemory,
                TlsFragment = fragment,
                AutoPickExcluded = ["🇷🇺 Москва"],
            };

            var routerDns = RouterSelection(settings);
            var servers = Warp.TunnelExits(settings, Servers());
            var ruleSet = RuleFixture();
            var capture = (IReadOnlyList<string>)["91.108.4.0/22"];
            var keptOut = narrow ? Array.Empty<string>() : (IReadOnlyList<string>)["185.40.64.0/22"];

            var legacy = Legacy(settings, routerDns, narrow, ipv6, Resolvers, BookUrls, keys,
                capture, keptOut, Pinned(), Overrides(), dead, health);

            var moved = EngineConfigBuilder.TunnelOptions(settings, WindowsHost(ipv6, Resolvers), new TunnelInputs(
                routerDns, narrow, !narrow, capture, keptOut, Pinned(), Overrides(), dead, health, keys, BookUrls));

            var expected = new SingBoxConfigCompiler().Compile(ruleSet, servers, legacy).Json;
            var actual = new SingBoxConfigCompiler().Compile(ruleSet, servers, moved).Json;

            Assert.True(string.Equals(expected, actual, StringComparison.Ordinal),
                $"расхождение: DNS {via}, роутер {router}, WARP {warp}, пулы {pools}, узкий {narrow}, "
                + $"стабильные {stableFirst}, IPv6 {ipv6}, память {fakeIpMemory}, нарезка {fragment}");

            checkedCount++;
        }

        Assert.Equal(768, checkedCount);
    }

    /// <summary>
    /// Поля телефона на Windows не значат ничего: заданные по ошибке, они не
    /// должны сдвинуть Windows-конфиг ни на байт.
    /// </summary>
    [Fact]
    public void Phone_fields_do_not_touch_a_Windows_config()
    {
        var plain = new SingBoxOptions
        {
            Scope = TunnelScope.Everything,
            HealthInbound = true,
            Keys = new EngineKeys("s", "u", "p"),
            StoreFakeIp = true,
        };

        var withPhoneFields = new SingBoxOptions
        {
            Scope = TunnelScope.Everything,
            HealthInbound = true,
            Keys = new EngineKeys("s", "u", "p"),
            StoreFakeIp = true,
            HostsFilePath = "/data/user/0/org.netzapret.android/files/config/hosts",
            ExcludePackages = ["ru.sberbankmobile"],
            DesyncSocks = new DesyncSocks(41234, "nz", "secret-for-test"),
            DesyncPassHosts = ["github.com"],
            DesyncShieldNames = ["notion.so"],
            DesyncFragment = TlsFragment.Records,
            DesyncRejectQuic = true,
            DesyncDefault = true,
        };

        var ruleSet = RuleFixture();

        Assert.Equal(
            new SingBoxConfigCompiler().Compile(ruleSet, Servers(), plain).Json,
            new SingBoxConfigCompiler().Compile(ruleSet, Servers(), withPhoneFields).Json);
    }

    /// <summary>
    /// Инициализатор из <c>gui/NetZapret.Gui/TunnelConfig.cs</c> до 27.10 (master 7c28aa4),
    /// как был. Подставлено только то, что он читал сам: реестр (IPv6),
    /// резолверы Windows, книга подписок, замеры и новые пароли.
    /// </summary>
    private static SingBoxOptions Legacy(
        AppSettings settings,
        RouterDnsConnection.Selection routerDns,
        bool narrow,
        bool ipv6,
        IReadOnlyList<string> systemResolvers,
        IReadOnlyList<string> bookUrls,
        EngineKeys keys,
        IReadOnlyList<string> capture,
        IReadOnlyList<string> keptOut,
        PinnedProxy pinned,
        IReadOnlyDictionary<string, string> addresses,
        IReadOnlyCollection<string> dead,
        ServerHealthCache health) => new()
    {
        Scope = narrow ? TunnelScope.ProxyOnly : TunnelScope.Everything,
        Ipv6 = ipv6,
        DnsServerAddresses = narrow ? systemResolvers : Array.Empty<string>(),
        DnsServer = settings.UseRouterDns ? settings.RouterDnsAddress : settings.DnsServer,
        DnsServerType = settings.UseRouterDns ? "udp" : "https",
        RouterDns = settings.UseRouterDns,
        RouterDnsInterface = routerDns.Interface?.Name,
        DnsServerName = DnsSurvey.ByAddress(settings.DnsServer)?.TlsName,
        DnsServerPath = DnsSurvey.ByAddress(settings.DnsServer)?.DohPath,
        DnsThroughTunnel = !settings.UseRouterDns && settings.DnsVia == DnsRoute.Tunnel,
        DnsViaAuto = !settings.UseRouterDns && settings.DnsVia == DnsRoute.Auto,
        DnsSpare = DnsSpare.For(settings.DnsServer),
        PanelHosts = SubscriptionHosts.From([settings.SubscriptionUrl, .. bookUrls]),
        PreferredServerTag = Warp.PreferredExit(settings),
        Pools = settings.EnginePools,
        ForeignExitsOnly = settings.ForeignExitsOnly,
        HealthInbound = true,
        Keys = keys,
        CaptureAddresses = capture,
        DesyncAddresses = keptOut,
        PinnedProxyAddresses = pinned.Addresses,
        PinnedProxyNames = pinned.Names,
        AddressOverrides = addresses,
        DeadServerTags = dead
            .Concat(settings.AutoPickExcluded)
            .ToHashSet(StringComparer.Ordinal),
        FlakyServerTags = settings.StableFirst
            ? health.Flaky().ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal),
        LatencyTestInterval = $"{Math.Max(1, settings.FullCheckMinutes)}m",
        LatencyTestUrl = Ping.UrlOf(settings),
        TlsFragment = settings.TlsFragment,
        StoreFakeIp = settings.FakeIpMemory,
        AutoPickPerEntry = Math.Max(0, settings.AutoPickPerEntry),
        KnownLatency = health.Latencies(),
    };
}
