using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

public class RuleEngineTests
{
    private static ConnectionEvent Connection(
        string? process = null,
        string? hostname = null,
        string? remoteIp = null,
        bool loopback = false) => new()
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            Protocol = ProtocolKind.Tcp,
            RemoteAddress = remoteIp is null ? null : IPAddress.Parse(remoteIp),
            RemotePort = 443,
            ExecutablePath = process,
            Hostname = hostname,
            IsLoopback = loopback,
        };

    private const string ExampleConfig = """
        rules:
          - match: process
            value: "somegame.exe"
            mode: direct
          - match: domain
            value: "*.rutracker.org"
            mode: proxy
            server: "auto"
        default:
          mode: direct
        """;

    [Fact]
    public void ProcessRuleMatchesByFileName()
    {
        var engine = RuleSetLoader.Load(ExampleConfig);

        var decision = engine.Evaluate(Connection(process: @"D:\Games\SomeGame\somegame.exe"));

        Assert.Equal(RoutingMode.Direct, decision.Mode);
        Assert.NotNull(decision.Rule);
        Assert.Equal(MatchKind.Process, decision.Rule!.Match);
    }

    [Fact]
    public void DomainWildcardAlsoMatchesTheBareDomain()
    {
        // "*.rutracker.org" должен покрывать и сам rutracker.org — иначе каждое
        // правило пришлось бы дублировать.
        var engine = RuleSetLoader.Load(ExampleConfig);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection(hostname: "rutracker.org")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection(hostname: "www.rutracker.org")).Mode);
        Assert.Equal(RoutingMode.Direct, engine.Evaluate(Connection(hostname: "notrutracker.org")).Mode);
    }

    [Fact]
    public void ProcessRulesWinOverDomainRulesRegardlessOfFileOrder()
    {
        // Доменное правило записано первым, но приоритет класса ставит process выше.
        var engine = RuleSetLoader.Load("""
            rules:
              - match: domain
                value: "example.com"
                mode: proxy
              - match: process
                value: "somegame.exe"
                mode: direct
            default:
              mode: proxy
            """);

        var decision = engine.Evaluate(Connection(process: "somegame.exe", hostname: "example.com"));

        Assert.Equal(RoutingMode.Direct, decision.Mode);
        Assert.Equal(MatchKind.Process, decision.Rule!.Match);
    }

    [Fact]
    public void OrderIsPreservedWithinTheSameMatchKind()
    {
        var engine = RuleSetLoader.Load("""
            rules:
              - match: domain
                value: "*.example.com"
                mode: proxy
              - match: domain
                value: "cdn.example.com"
                mode: direct
            default:
              mode: direct
            """);

        // Первое правило шире и записано раньше, поэтому оно и срабатывает.
        var decision = engine.Evaluate(Connection(hostname: "cdn.example.com"));

        Assert.Equal(RoutingMode.Proxy, decision.Mode);
        Assert.Equal(0, decision.Rule!.Ordinal);
    }

    [Fact]
    public void UnknownHostnameMarksDomainRulesAsUnevaluable()
    {
        // Главное свойство PoC: «сработал default» и «не смогли проверить»
        // должны различаться в выводе.
        var engine = RuleSetLoader.Load(ExampleConfig);

        var decision = engine.Evaluate(Connection(remoteIp: "104.21.32.1"));

        Assert.Equal(RoutingMode.Direct, decision.Mode);
        Assert.Null(decision.Rule);
        Assert.True(decision.HadUnevaluableDomainRules);
    }

    [Fact]
    public void LoopbackIsAlwaysDirect()
    {
        var engine = RuleSetLoader.Load("""
            rules: []
            default:
              mode: proxy
            """);

        var decision = engine.Evaluate(Connection(remoteIp: "127.0.0.1", loopback: true));

        Assert.Equal(RoutingMode.Direct, decision.Mode);
    }

    [Fact]
    public void CidrRuleMatchesAddressesInsideTheSubnet()
    {
        // Диапазон намеренно публичный: приватные сети отсекаются раньше правил,
        // и на них этот тест ничего бы не проверял.
        var engine = RuleSetLoader.Load("""
            rules:
              - match: ip
                value: "203.0.113.0/24"
                mode: direct
            default:
              mode: proxy
            """);

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(Connection(remoteIp: "203.0.113.7")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection(remoteIp: "203.0.114.7")).Mode);
    }

    [Fact]
    public void DesyncModeIsParsedAndKeptDistinctFromDirect()
    {
        var engine = RuleSetLoader.Load("""
            rules:
              - match: domain
                value: "youtube.com"
                mode: desync
            default:
              mode: direct
            """);

        Assert.Equal(RoutingMode.Desync, engine.Evaluate(Connection(hostname: "youtube.com")).Mode);
    }

    [Fact]
    public void ProxyRuleFallsBackToTheDefaultServer()
    {
        var engine = RuleSetLoader.Load("""
            rules:
              - match: domain
                value: "example.com"
                mode: proxy
            default:
              mode: direct
              server: "fallback-node"
            """);

        Assert.Equal("fallback-node", engine.Evaluate(Connection(hostname: "example.com")).Server);
    }

    [Fact]
    public void OffModeSendsEverythingDirectAndIgnoresRules()
    {
        var engine = RuleSetLoader.Load("""
            mode: off
            rules:
              - match: domain
                value: "rutracker.org"
                mode: proxy
            """);

        var decision = engine.Evaluate(Connection(hostname: "rutracker.org", remoteIp: "203.0.113.7"));

        Assert.Equal(RoutingMode.Direct, decision.Mode);
        Assert.Null(decision.Rule);
    }

    /// <summary>«Кроме РФ» чтит исключения — в них весь его смысл.</summary>
    /// <remarks>
    /// Прежде этот режим правила не вычислял вовсе и отправлял в туннель
    /// всё подряд, включая российские сервисы, ради которых назван «кроме
    /// РФ». Так вёл себя он, а «без исключений» — наоборот, правила читал.
    /// Названия и поведение стояли наоборот.
    /// </remarks>
    [Fact]
    public void ProxyAllKeepsTheExceptionsItIsNamedFor()
    {
        var engine = RuleSetLoader.Load("""
            mode: proxy
            rules:
              - match: process
                value: "somegame.exe"
                mode: direct
            """);

        Assert.Equal(
            RoutingMode.Direct,
            engine.Evaluate(Connection(process: "somegame.exe", remoteIp: "203.0.113.7")).Mode);
    }

    /// <summary>Десинк в этом режиме уступает туннелю.</summary>
    /// <remarks>
    /// Правило, написанное ради обхода, здесь означает лишь «сервис закрыт»,
    /// а чем открывать — решает режим. Пока десинк переживал переключение,
    /// «всё через VPN» вело себя как «выборочно»: в туннель уходило два
    /// имени из семидесяти одного.
    /// </remarks>
    [Fact]
    public void ProxyAllTurnsDesyncRulesIntoTunnel()
    {
        var engine = RuleSetLoader.Load("""
            mode: proxy
            rules:
              - match: domain
                value: "youtube.com"
                mode: desync
            """);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection(hostname: "youtube.com")).Mode);
    }

    /// <summary>А «без исключений» не чтит ничего.</summary>
    [Fact]
    public void ProxyStrictIgnoresEveryRule()
    {
        var engine = RuleSetLoader.Load("""
            mode: proxy_strict
            rules:
              - match: process
                value: "somegame.exe"
                mode: direct
            """);

        Assert.Equal(
            RoutingMode.Proxy,
            engine.Evaluate(Connection(process: "somegame.exe", remoteIp: "203.0.113.7")).Mode);
    }

    [Fact]
    public void ProxyAllModeStillKeepsLocalNetworksDirect()
    {
        var engine = RuleSetLoader.Load("""
            mode: proxy
            rules: []
            """);

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(Connection(remoteIp: "192.168.1.1")).Mode);
        Assert.Equal(RoutingMode.Direct, engine.Evaluate(Connection(remoteIp: "10.0.0.5")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection(remoteIp: "8.8.8.8")).Mode);
    }

    [Fact]
    public void SelectiveModeDefaultsToDesyncWhenDefaultIsOmitted()
    {
        // «По умолчанию ко всему применяется десинк, прокси — выборочно.»
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "rutracker.org"
                mode: proxy
            """);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(Connection(hostname: "rutracker.org", remoteIp: "203.0.113.7")).Mode);
        Assert.Equal(RoutingMode.Desync, engine.Evaluate(Connection(hostname: "example.com", remoteIp: "203.0.113.7")).Mode);
    }

    [Fact]
    public void ExplicitDefaultOverridesTheModeDerivedOne()
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules: []
            default:
              mode: direct
            """);

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(Connection(remoteIp: "203.0.113.7")).Mode);
    }

    [Fact]
    public void UnknownOperatingModeFailsWithAnActionableMessage()
    {
        var ex = Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load("""
            mode: nonsense
            rules: []
            """));

        Assert.Contains("режим работы", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownModeFailsWithAnActionableMessage()
    {
        var ex = Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load("""
            rules:
              - match: domain
                value: "example.com"
                mode: nonsense
            default:
              mode: direct
            """));

        Assert.Contains("неизвестный режим", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownMatchKindFailsWithAnActionableMessage()
    {
        var ex = Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load("""
            rules:
              - match: nonsense
                value: "example.com"
                mode: proxy
            default:
              mode: direct
            """));

        Assert.Contains("неизвестный тип", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingValueFailsWithTheRuleIndex()
    {
        var ex = Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load("""
            rules:
              - match: domain
                mode: proxy
            default:
              mode: direct
            """));

        Assert.Contains("#0", ex.Message);
    }

    [Fact]
    public void InvalidCidrIsRejectedAtLoadTimeNotAtMatchTime()
    {
        var yaml = """
            rules:
              - match: ip
                value: "не-адрес"
                mode: proxy
            default:
              mode: direct
            """;

        Assert.Throws<RuleConfigurationException>(() => RuleSetLoader.Load(yaml));
    }
}
