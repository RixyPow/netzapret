using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Раздел «Шифрованные» вкладки DNS (04.11): dnscrypt-proxy на петле — резолвер движка.
/// </summary>
public sealed class DnsCryptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-dnscrypt-{Guid.NewGuid():N}");

    public DnsCryptTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string? RepositoryRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "NetZapret.sln")))
                return d.FullName;
        }

        return null;
    }

    [Theory]
    [InlineData(DnsCryptMode.DnsCrypt)]
    [InlineData(DnsCryptMode.Anonymized)]
    [InlineData(DnsCryptMode.Odoh)]
    public void KeysRoundTrip(DnsCryptMode mode) => Assert.Equal(mode, DnsCrypt.Parse(DnsCrypt.Key(mode)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("doh")]
    public void UnknownKeyIsNoMode(string? key) => Assert.Null(DnsCrypt.Parse(key));

    [Fact]
    public void PlainDnsCryptAsksDnsCryptServersDirectly()
    {
        var config = DnsCrypt.Config(DnsCryptMode.DnsCrypt, _root);

        Assert.Contains($"listen_addresses = ['127.0.0.1:{DnsCrypt.Port}']", config);
        Assert.Contains("dnscrypt_servers = true", config);
        Assert.Contains("odoh_servers = false", config);
        Assert.Contains("require_nolog = true", config);
        Assert.Contains("require_nofilter = true", config);
        Assert.Contains("[sources.'public-resolvers']", config);
        Assert.DoesNotContain("[anonymized_dns]", config);
    }

    /// <summary>Замер шифрованных поднимает свой dnscrypt-proxy на свободном порту и без кэша.</summary>
    [Fact]
    public void TheProbeListensElsewhereWithoutCache()
    {
        var config = DnsCrypt.Config(DnsCryptMode.Odoh, _root, port: 61234, cache: false);

        Assert.Contains("listen_addresses = ['127.0.0.1:61234']", config);
        Assert.Contains("cache = false", config);
        Assert.DoesNotContain($":{DnsCrypt.Port}'", config);
        Assert.Contains("cache = true", DnsCrypt.Config(DnsCryptMode.Odoh, _root));
    }

    [Fact]
    public void AnonymizedGoesThroughRelays()
    {
        var config = DnsCrypt.Config(DnsCryptMode.Anonymized, _root);

        Assert.Contains("[sources.'relays']", config);
        Assert.Contains("routes = [{ server_name = '*', via = ['*'] }]", config);

        // Только в своей таблице: на верхнем уровне dnscrypt-proxy его не ждёт,
        // а внутри [sources] падает с «type mismatch» (опыт 04.11).
        Assert.True(config.IndexOf("skip_incompatible", StringComparison.Ordinal) > config.IndexOf("[anonymized_dns]", StringComparison.Ordinal));
    }

    [Fact]
    public void OdohAsksOdohServersThroughOdohRelays()
    {
        var config = DnsCrypt.Config(DnsCryptMode.Odoh, _root);

        Assert.Contains("dnscrypt_servers = false", config);
        Assert.Contains("odoh_servers = true", config);
        Assert.Contains("[sources.'odoh-servers']", config);
        Assert.Contains("[sources.'odoh-relays']", config);
        Assert.Contains("[anonymized_dns]", config);
    }

    [Fact]
    public void ListPathsAreFullAndForwardSlashed()
    {
        var config = DnsCrypt.Config(DnsCryptMode.DnsCrypt, _root);
        var expected = Path.GetFullPath(Path.Combine(_root, "public-resolvers.md")).Replace('\\', '/');

        Assert.Contains($"cache_file = '{expected}'", config);
    }

    [Theory]
    [InlineData(DnsCryptMode.DnsCrypt)]
    [InlineData(DnsCryptMode.Anonymized)]
    [InlineData(DnsCryptMode.Odoh)]
    public void EveryListAModeNeedsShipsWithItsSignature(DnsCryptMode mode)
    {
        if (RepositoryRoot() is not { } root)
            return;

        foreach (var list in DnsCrypt.Lists(mode))
        {
            var file = Path.Combine(root, "config", "dnscrypt", list);

            Assert.True(File.Exists(file), list);
            Assert.True(File.Exists(file + ".minisig"), list + ".minisig");

            // Подпись проверяется побайтно: перевод строк в CRLF её сломал бы.
            Assert.DoesNotContain((byte)'\r', File.ReadAllBytes(file));
        }
    }

    [Fact]
    public void SeedingKeepsWhatTheProxyAlreadyRefreshed()
    {
        var shipped = Path.Combine(_root, "shipped");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(shipped);
        Directory.CreateDirectory(work);

        File.WriteAllText(Path.Combine(shipped, "public-resolvers.md"), "old");
        File.WriteAllText(Path.Combine(shipped, "public-resolvers.md.minisig"), "old-sig");
        File.WriteAllText(Path.Combine(work, "public-resolvers.md"), "fresh");

        DnsCrypt.SeedLists(DnsCryptMode.DnsCrypt, shipped, work);

        Assert.Equal("fresh", File.ReadAllText(Path.Combine(work, "public-resolvers.md")));
        Assert.Equal("old-sig", File.ReadAllText(Path.Combine(work, "public-resolvers.md.minisig")));
    }

    [Fact]
    public void RouterDnsOverridesEncrypted()
    {
        Assert.Equal(DnsCryptMode.Odoh, new AppSettings { DnsCrypt = "odoh" }.DnsCryptActive);
        Assert.Null(new AppSettings { DnsCrypt = "odoh", UseRouterDns = true }.DnsCryptActive);
    }

    private static ProxyServer Server() => new()
    { Protocol = ProxyProtocol.Hysteria2, Tag = "Test", Host = "proxy.example", Port = 443, Credential = "TEST", Transport = "udp", Security = "tls" };

    private static SingBoxOptions Options(TunnelScope scope = TunnelScope.ProxyOnly) => new()
    {
        Scope = scope,
        DnsServer = DnsCrypt.Address,
        DnsServerType = "udp",
        DnsServerPort = DnsCrypt.Port,
        DnsCrypt = true,
        DnsViaAuto = true,
        DnsThroughTunnel = false,
    };

    private const string Rules = """
        mode: selective
        rules:
          - match: domain
            value: "*.discord.gg"
            mode: proxy
        """;

    private static (JsonNode Json, string Text) Compile(SingBoxOptions options, string rules = Rules)
    {
        var loaded = RuleSetLoader.Load(rules);
        var text = new SingBoxConfigCompiler().Compile(loaded.RuleSet, [Server()], options).Json;
        return (JsonNode.Parse(WithoutCanary.Strip(text))!, text);
    }

    [Fact]
    public void EngineAsksTheProxyOnLoopbackNeverThroughTheTunnel()
    {
        var (json, _) = Compile(Options());
        var remote = json["dns"]!["servers"]!.AsArray().Single(n => (string?)n!["tag"] == "remote")!;

        Assert.Equal("udp", (string?)remote["type"]);
        Assert.Equal(DnsCrypt.Address, (string?)remote["server"]);
        Assert.Equal(DnsCrypt.Port, (int?)remote["server_port"]);

        // «Авто» в настройках есть, но до 127.0.0.1 через туннель не добраться.
        Assert.Null(remote["detour"]);
        Assert.DoesNotContain(json["dns"]!["servers"]!.AsArray(), n => (string?)n!["tag"] == SingBoxOptions.DnsTcpTag);
    }

    [Fact]
    public void ProxyTrafficLeavesBeforeDnsIsHijacked()
    {
        var (json, _) = Compile(Options(TunnelScope.Everything), Rules.Replace("mode: selective", "mode: proxy"));
        var rules = json["route"]!["rules"]!.AsArray();

        int own = rules.ToList().FindIndex(r => r!["process_name"] is JsonArray names
            && names.Any(n => (string?)n == "dnscrypt-proxy.exe"));
        int hijack = rules.ToList().FindIndex(r => (string?)r!["action"] == "hijack-dns");

        Assert.True(own >= 0);
        Assert.Equal("direct", (string?)rules[own]!["outbound"]);
        Assert.True(own < hijack);
    }

    [Fact]
    public void WithoutEncryptedNoProcessRule()
    {
        var (json, _) = Compile(new SingBoxOptions { Scope = TunnelScope.ProxyOnly, DnsServer = "8.8.4.4" });

        Assert.DoesNotContain(json["route"]!["rules"]!.AsArray(), r => r!["process_name"] is not null);
    }

    [Fact]
    public void SingBoxAcceptsTheConfig()
    {
        var (_, text) = Compile(Options(TunnelScope.Everything), Rules.Replace("mode: selective", "mode: proxy"));
        var check = SingBoxCheck.Run(text);

        Assert.True(check.Ok, check.Said);
    }

    [Fact]
    public void IntermediariesAreNeverPickedAutomatically()
    {
        var zapret = DnsSurvey.Providers.Single(p => p.Name == "Zapret DNS");
        var google = DnsSurvey.Providers.Single(p => p.Name == "Google");

        var fastest = DnsSurvey.Fastest(
        [
            new DnsSurveyRow { Provider = zapret, DohMs = 5 },
            new DnsSurveyRow { Provider = google, DohMs = 40 },
        ]);

        Assert.Same(google, fastest);
        Assert.True(zapret.Choosable);
        Assert.Equal(DnsGroup.Intermediary, zapret.Group);
    }
}
