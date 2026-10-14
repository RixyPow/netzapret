using System.Diagnostics;
using System.Net;
using System.Text.Json;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Российские зоны напрямую нижним слоем и реестр блокировок над ними (владелец 15.10).
/// </summary>
public sealed class RuZonesTests
{
    private static RoutingRule List(string value, RoutingMode mode, RuleSource source, params string[] names)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = value, Mode = mode, Source = source };
        rule.LoadHostList(names);
        return rule;
    }

    /// <summary>Реестр (заводской, «десинк»), своё правило по адресу — и зоны снизу.</summary>
    private static RuleEngine Engine(OperatingMode operating, RoutingMode defaultMode, params RoutingRule[] extra) =>
        RuleEngine.Build(
            [
                List(ServiceCatalog.RegistryList, RoutingMode.Desync, RuleSource.Base, "amdm.ru", "blocked.example"),
                new RoutingRule { Match = MatchKind.Ip, Value = "203.0.113.0/24", Mode = RoutingMode.Proxy, Source = RuleSource.User },
                .. extra,
                .. RuZones.Rules(),
            ],
            defaultMode,
            null,
            operating);

    private static ConnectionEvent To(string host, string address = "198.51.100.7") => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Protocol = ProtocolKind.Tcp,
        RemoteAddress = IPAddress.Parse(address),
        RemotePort = 443,
        Hostname = host,
    };

    [Fact]
    public void ZonesDecideOnlyWhenNothingElseDoes()
    {
        var engine = Engine(OperatingMode.ProxyAll, RoutingMode.Proxy);

        var gosuslugi = engine.Evaluate(To("www.gosuslugi.ru"));
        Assert.Equal(RoutingMode.Direct, gosuslugi.Mode);
        Assert.Equal(RuleSource.Fallback, gosuslugi.Rule!.Source);

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(To("xn--e1afmkfd.xn--p1ai")).Mode);
        Assert.Equal(RoutingMode.Direct, engine.Evaluate(To("forum.su")).Mode);

        // Реестр выше зоны, своё правило по адресу — тоже: «если нет других
        // правил». «Всё через VPN» сводит «десинк» реестра к туннелю, «Гибрид» —
        // оставляет десинком.
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(To("amdm.ru")).Mode);
        Assert.Equal(RoutingMode.Desync, Engine(OperatingMode.Selective, RoutingMode.Desync).Evaluate(To("amdm.ru")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(To("game.ru", "203.0.113.9")).Mode);

        // Не российская зона — как прежде.
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(To("example.com")).Mode);
        Assert.False(RuZones.Covers("guru"));
        Assert.True(RuZones.Covers("*.mos.ru"));
    }

    /// <summary>
    /// «Гибрид»: зона в щите winws2, имена реестра в ней — дырами, то есть с десинком, как прежде.
    /// </summary>
    [Fact]
    public void RegistryNamesAreHolesInTheZoneShield()
    {
        var engine = Engine(OperatingMode.Selective, RoutingMode.Desync);
        var hosts = Path.Combine(Path.GetTempPath(), $"no-hosts-{Guid.NewGuid():N}");

        var shield = HostsFile.DescribeDesyncExclusions(engine.RuleSet, hosts);
        var holes = HostsFile.CollectShieldHoles(engine.RuleSet, shield);

        Assert.Contains(("ru", DesyncBypass.Direct), shield);
        Assert.Contains("amdm.ru", holes);
        Assert.DoesNotContain("blocked.example", holes);

        Assert.Equal(DesyncBypass.Direct, HostsFile.BypassFor(shield, "www.gosuslugi.ru", holes));
        Assert.Equal(DesyncBypass.None, HostsFile.BypassFor(shield, "amdm.ru", holes));
    }

    private static JsonElement Compile(RuleEngine engine, TunnelScope scope)
    {
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [Server()], new SingBoxOptions { Scope = scope });
        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    private static ProxyServer Server() => new()
    {
        Protocol = ProxyProtocol.Trojan,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 443,
        Credential = "PLACEHOLDER",
        Transport = "tcp",
        Security = "tls",
        Sni = "nl.example.com",
    };

    private static List<JsonElement> Rules(JsonElement root) =>
        [.. root.GetProperty("route").GetProperty("rules").EnumerateArray()];

    private static string[] Suffixes(JsonElement rule) =>
        rule.TryGetProperty("domain_suffix", out var list) ? [.. list.EnumerateArray().Select(e => e.GetString()!)] : [];

    /// <summary>
    /// «Туннель»: реестр приходит в конфиг только российскими именами, через VPN,
    /// раньше правила зоны; остальное его — по final, как прежде.
    /// </summary>
    [Fact]
    public void InTheTunnelRegistryRussianNamesStayInTheTunnel()
    {
        var root = Compile(Engine(OperatingMode.ProxyAll, RoutingMode.Proxy), TunnelScope.Everything);
        var rules = Rules(root);

        int registry = rules.FindIndex(r => Suffixes(r).SequenceEqual(["amdm.ru"]));
        int zone = rules.FindIndex(r => Suffixes(r).SequenceEqual(["ru"]));

        Assert.True(registry >= 0, "российских имён реестра нет в конфиге");
        Assert.True(zone > registry, "правило зоны должно стоять ниже реестра");
        Assert.Equal("direct", rules[zone].GetProperty("outbound").GetString());
        Assert.NotEqual("direct", rules[registry].GetProperty("outbound").GetString());
        Assert.DoesNotContain("blocked.example", root.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Своё «через VPN» над заводским «напрямую» в «Туннеле» больше не проигрывает.</summary>
    [Fact]
    public void InTheTunnelAUserVpnRuleBeatsABaseDirectBelowIt()
    {
        var engine = RuleEngine.Build(
            [
                new RoutingRule { Match = MatchKind.Domain, Value = "*.avito.ru", Mode = RoutingMode.Proxy, Source = RuleSource.User },
                new RoutingRule { Match = MatchKind.Domain, Value = "*.avito.ru", Mode = RoutingMode.Direct, Source = RuleSource.Base },
            ],
            RoutingMode.Proxy,
            null,
            OperatingMode.ProxyAll);

        var rules = Rules(Compile(engine, TunnelScope.Everything));
        int vpn = rules.FindIndex(r => Suffixes(r).SequenceEqual(["avito.ru"]) && r.GetProperty("outbound").GetString() != "direct");
        int direct = rules.FindIndex(r => Suffixes(r).SequenceEqual(["avito.ru"]) && r.GetProperty("outbound").GetString() == "direct");

        Assert.True(vpn >= 0 && vpn < direct);
    }

    /// <summary>«Гибрид»: реестр мимо туннеля в конфиг не идёт; отправленный через VPN — идёт.</summary>
    [Fact]
    public void InTheHybridTheRegistryReachesTheConfigOnlyThroughTheVpn()
    {
        var desync = Compile(Engine(OperatingMode.Selective, RoutingMode.Desync), TunnelScope.ProxyOnly);
        Assert.DoesNotContain("amdm.ru", desync.GetRawText(), StringComparison.Ordinal);

        var vpn = RuleEngine.Build(
            [List(ServiceCatalog.RegistryList, RoutingMode.Proxy, RuleSource.User, "amdm.ru"), .. RuZones.Rules()],
            RoutingMode.Desync,
            null,
            OperatingMode.Selective);

        Assert.Contains("amdm.ru", Compile(vpn, TunnelScope.ProxyOnly).GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Конфиг «Туннеля» с зонами и урезанным реестром — настоящим sing-box.</summary>
    [Fact]
    public void TheTunnelConfigPassesSingBoxCheck()
    {
        var singBox = SingBoxConfigCompilerTests.FindSingBox();
        if (singBox is null)
            return;

        var result = new SingBoxConfigCompiler().Compile(
            Engine(OperatingMode.ProxyAll, RoutingMode.Proxy).RuleSet,
            [Server()],
            new SingBoxOptions { Scope = TunnelScope.Everything, HealthInbound = true, Keys = EngineKeys.Generate() });

        var path = Path.Combine(Path.GetTempPath(), $"netzapret-ruzones-{Guid.NewGuid():N}.json");

        try
        {
            SingBoxConfigCompiler.WriteToFile(path, result.Json);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = singBox,
                ArgumentList = { "check", "-c", path },
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;

            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            Assert.True(process.ExitCode == 0, $"sing-box check не пройден: {stderr}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Маршрут в каталоге, правило в поставке, список на месте.</summary>
    [Fact]
    public void TheRegistryRouteShips()
    {
        var part = Assert.Single(ServiceCatalog.All.SelectMany(s => s.Parts), p => p.List == ServiceCatalog.RegistryList);
        Assert.True(part.Bulk);
        Assert.True(ServiceCatalog.IsBulk(ServiceCatalog.RegistryList));
        Assert.False(ServiceCatalog.IsBulk("config/lists/discord.txt"));

        var root = Repository();
        if (root is null)
            return;

        var names = File.ReadLines(Path.Combine(root, ServiceCatalog.RegistryList))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(names.Count > 100_000, $"в реестре {names.Count} имён");
        Assert.Contains("amdm.ru", names);
        Assert.Contains("dtf.ru", names);

        var shipped = RuleSetLoader.LoadFromFile(Path.Combine(root, "config", "rules.yaml"));
        var rule = Assert.Single(shipped.RuleSet.Rules, r => r.Value == ServiceCatalog.RegistryList);
        Assert.Equal(RoutingMode.Desync, rule.Mode);
    }

    private static string? Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "config", "rules.yaml")))
                return directory.FullName;
        }

        return null;
    }
}
