using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// «Прятать VPN от российских приложений»: двенадцать имён — напрямую.
/// </summary>
public sealed class VpnHidingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"netzapret-hide-{Guid.NewGuid():N}");
    private readonly string _basePath;
    private readonly string _userPath;

    public VpnHidingTests()
    {
        Directory.CreateDirectory(_directory);
        _basePath = Path.Combine(_directory, "rules.yaml");
        _userPath = Path.Combine(_directory, "rules.user.yaml");

        File.WriteAllText(_basePath, """
            mode: selective
            rules:
              - match: domain
                value: "*.rutracker.org"
                mode: proxy
            default:
              mode: desync
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static ConnectionEvent Connection(string hostname) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch,
        Protocol = ProtocolKind.Tcp,
        RemoteAddress = System.Net.IPAddress.Parse("203.0.113.7"),
        RemotePort = 443,
        Hostname = hostname,
    };

    private AppSettings Settings(bool hide, OperatingMode mode) =>
        new() { RulesPath = _basePath, HideVpnFromRussianApps = hide, Mode = mode };

    /// <summary>
    /// Ради этого режима и заведено: всё, что не исключено, идёт в туннель,
    /// и приложение, спросив свой адрес, увидело бы зарубежный.
    /// </summary>
    [Theory]
    [InlineData("ifconfig.me")]
    [InlineData("api.ipify.org")]
    [InlineData("sdk-api.apptracer.ru")]   // поддомен покрыт, как domain_suffix у KVN
    public void Address_checks_go_direct_even_when_everything_goes_through_vpn(string host)
    {
        var engine = RuleSetLoader.LoadFor(Settings(hide: true, OperatingMode.ProxyAll), userPath: _userPath);

        var decision = engine.Evaluate(Connection(host));

        Assert.Equal(RoutingMode.Direct, decision.Mode);
        Assert.Equal(RuleSource.Setting, decision.Rule?.Source);
    }

    [Fact]
    public void Switched_off_nothing_changes()
    {
        var engine = RuleSetLoader.LoadFor(Settings(hide: false, OperatingMode.ProxyAll), userPath: _userPath);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("ifconfig.me")).Mode);
        Assert.DoesNotContain(engine.RuleSet.Rules, r => r.Source == RuleSource.Setting);
    }

    /// <summary>Своё правило человека главнее выключателя.</summary>
    [Fact]
    public void An_own_rule_wins_over_the_switch()
    {
        File.WriteAllText(_userPath, """
            rules:
              - match: domain
                value: "*.ifconfig.me"
                mode: proxy
            """);

        var engine = RuleSetLoader.LoadFor(Settings(hide: true, OperatingMode.Selective), userPath: _userPath);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("ifconfig.me")).Mode);
        Assert.Equal(RoutingMode.Direct, engine.Evaluate(Connection("api.ipify.org")).Mode);
    }

    [Fact]
    public void Other_rules_are_untouched()
    {
        var engine = RuleSetLoader.LoadFor(Settings(hide: true, OperatingMode.Selective), userPath: _userPath);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection("rutracker.org")).Mode);
        Assert.Equal(RoutingMode.Desync, engine.Evaluate(Connection("example.com")).Mode);
    }
}

/// <summary>Правило доходит до движка туннеля, а не остаётся в окне.</summary>
public sealed class VpnHidingConfigTests
{
    [Fact]
    public void Proxy_all_config_sends_the_names_direct_and_passes_sing_box_check()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netzapret-hidecfg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var basePath = Path.Combine(directory, "rules.yaml");
            File.WriteAllText(basePath, "mode: selective\nrules: []\ndefault:\n  mode: desync\n");

            var settings = new AppSettings
            {
                RulesPath = basePath,
                HideVpnFromRussianApps = true,
                Mode = OperatingMode.ProxyAll,
            };

            var engine = RuleSetLoader.LoadFor(settings, userPath: Path.Combine(directory, "none.yaml"));

            Assert.True(NetZapret.Subscriptions.ProxyUriParser.TryParse(
                "trojan://secret@t.example.com:443?sni=t.example.com#T", out var server, out _));

            var result = new NetZapret.Proxy.SingBoxConfigCompiler()
                .Compile(engine.RuleSet, [server!], new NetZapret.Proxy.SingBoxOptions());

            var json = result.Json;
            Assert.Contains("ifconfig.me", json);
            Assert.Contains("api.oneme.ru", json);

            var check = SingBoxCheck.Run(json);
            Assert.True(check.Ok, check.Said);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
