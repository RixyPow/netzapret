using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Таблица владельца от 23.09: куда идёт маршрут книги при каждом сочетании
/// выключателей.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>десинк: напрямую — напрямую, десинк — десинк, VPN — напрямую;</item>
/// <item>десинк и туннель: всё как в книге;</item>
/// <item>только туннель: напрямую — напрямую, десинк — VPN, VPN — VPN;</item>
/// <item>игнорировать исключения: всё — VPN.</item>
/// </list>
/// <para>
/// Проверяется то, что уходит движкам: конфиг sing-box и список исключений
/// winws2. Промежуточные значения вроде режима проверены отдельно
/// и сами по себе ничего не доказывают — правило могло потеряться дальше.
/// </para>
/// </remarks>
public sealed class ModeTableTests : IDisposable
{
    private const string Direct = "twitch.tv";
    private const string Desync = "youtube.com";
    private const string Vpn = "instagram.com";

    private readonly string _hosts = Path.Combine(
        Path.GetTempPath(), $"netzapret-hosts-{Guid.NewGuid():N}.txt");

    public ModeTableTests() => File.WriteAllText(_hosts, string.Empty);

    public void Dispose()
    {
        if (File.Exists(_hosts))
            File.Delete(_hosts);
    }

    private static RuleSet Book(OperatingMode mode)
    {
        var engine = RuleSetLoader.Load($"""
            mode: selective
            rules:
              - match: domain
                value: "*.{Direct}"
                mode: direct
              - match: domain
                value: "*.{Desync}"
                mode: desync
              - match: domain
                value: "*.{Vpn}"
                mode: proxy
            """);

        return engine.RuleSet with { Operating = mode };
    }

    private static ProxyServer Server() => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = "NL",
        Host = "nl.example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Transport = "udp",
        Security = "tls",
        Sni = "nl.example.com",
    };

    /// <summary>Куда sing-box отправит имя: первое совпавшее правило либо final.</summary>
    private static string Outbound(EngineChoice choice, string name)
    {
        var json = JsonNode.Parse(new SingBoxConfigCompiler()
            .Compile(Book(choice.Mode), [Server()], new SingBoxOptions()).Json)!;

        var route = json["route"]!;

        foreach (var rule in route["rules"]!.AsArray())
        {
            var suffixes = rule!["domain_suffix"]?.AsArray().Select(x => x!.GetValue<string>());

            if (suffixes is not null && suffixes.Contains(name))
                return rule["outbound"]!.GetValue<string>();
        }

        return route["final"]!.GetValue<string>();
    }

    private static string Tunnel => new SingBoxOptions().SelectorTag;

    [Fact]
    public void Both_engines_follow_the_book()
    {
        var both = new EngineChoice { Desync = true, Tunnel = true };

        Assert.Equal("direct", Outbound(both, Direct));
        Assert.Equal("direct", Outbound(both, Desync));
        Assert.Equal(Tunnel, Outbound(both, Vpn));
    }

    [Fact]
    public void The_tunnel_alone_keeps_direct_and_takes_desync()
    {
        var alone = new EngineChoice { Desync = false, Tunnel = true };

        Assert.Equal("direct", Outbound(alone, Direct));
        Assert.Equal(Tunnel, Outbound(alone, Desync));
        Assert.Equal(Tunnel, Outbound(alone, Vpn));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ignoring_the_exclusions_sends_everything_to_the_tunnel(bool desync)
    {
        var ignore = new EngineChoice { Desync = desync, Tunnel = true, IgnoreExclusions = true };

        Assert.Equal(Tunnel, Outbound(ignore, Direct));
        Assert.Equal(Tunnel, Outbound(ignore, Desync));
        Assert.Equal(Tunnel, Outbound(ignore, Vpn));
    }

    [Fact]
    public void Desync_alone_lets_vpn_go_direct()
    {
        // Туннеля нет, везти «через VPN» некуда — и десинк его не трогает,
        // как и «напрямую». «Десинк» остаётся десинку.
        var found = HostsFile.DescribeDesyncExclusions(
            Book(OperatingMode.DesyncOnly), _hosts, tunnelUp: false);

        Assert.Equal(DesyncBypass.Direct, HostsFile.BypassFor(found, Direct));
        Assert.Equal(DesyncBypass.VpnWithoutTunnel, HostsFile.BypassFor(found, Vpn));
        Assert.Equal(DesyncBypass.None, HostsFile.BypassFor(found, Desync));
    }

    [Fact]
    public void With_the_tunnel_vpn_names_are_not_excluded()
    {
        // Их трафик уходит в туннель, и до WinDivert не доходит вовсе.
        var found = HostsFile.DescribeDesyncExclusions(
            Book(OperatingMode.Selective), _hosts, tunnelUp: true);

        Assert.Equal(DesyncBypass.Direct, HostsFile.BypassFor(found, Direct));
        Assert.Equal(DesyncBypass.None, HostsFile.BypassFor(found, Vpn));
        Assert.Equal(DesyncBypass.None, HostsFile.BypassFor(found, Desync));
    }

    /// <summary>
    /// Таблица словами совпадает с таблицей в деле — при каждом сочетании.
    /// </summary>
    /// <remarks>
    /// EngineChoice.Effective отвечает «куда пойдёт имя», а исполняют
    /// таблицу конфиг sing-box и список исключений winws2. Три места,
    /// и разойтись им легко; здесь их сверяют между собой.
    /// </remarks>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void The_named_table_matches_what_the_engines_get(bool desync, bool tunnel, bool ignore)
    {
        var choice = new EngineChoice { Desync = desync, Tunnel = tunnel, IgnoreExclusions = ignore };

        var excluded = HostsFile.DescribeDesyncExclusions(Book(choice.Mode), _hosts, tunnelUp: tunnel);

        foreach (var (name, book) in new[]
        {
            (Direct, RoutingMode.Direct),
            (Desync, RoutingMode.Desync),
            (Vpn, RoutingMode.Proxy),
        })
        {
            var said = choice.Effective(book, tunnelUp: tunnel);

            bool intoTunnel = tunnel && Outbound(choice, name) == Tunnel;
            bool desynced = choice.DesyncRuns && !intoTunnel
                && HostsFile.BypassFor(excluded, name) == DesyncBypass.None;

            var done = intoTunnel ? RoutingMode.Proxy
                : desynced ? RoutingMode.Desync
                : RoutingMode.Direct;

            Assert.True(said == done, $"{choice.Describe()}, {name} ({book}): сказано {said}, сделано {done}");
        }
    }

    [Fact]
    public void Without_the_tunnel_a_pin_under_vpn_is_excluded_too()
    {
        // С туннелем адрес такого пина заводится в туннель; без него
        // соединение идёт на прибитый адрес напрямую, и рецепт, выверенный
        // на настоящей сети доставки, его только порвёт.
        File.WriteAllText(_hosts, "157.240.0.35 " + Vpn);

        var down = HostsFile.DescribeDesyncExclusions(Book(OperatingMode.DesyncOnly), _hosts, tunnelUp: false);
        var up = HostsFile.DescribeDesyncExclusions(Book(OperatingMode.Selective), _hosts, tunnelUp: true);

        Assert.Equal(DesyncBypass.Pin, HostsFile.BypassFor(down, Vpn));
        Assert.Equal(DesyncBypass.None, HostsFile.BypassFor(up, Vpn));
    }
}
