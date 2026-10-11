using System.Net;
using System.Text.Json.Nodes;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подсети в «Маршрутах» и адреса в своих списках (владелец 12.10).
/// </summary>
/// <remarks>
/// До этого поле «Маршрутов» принимало «45.82.160.0/24» за имя без маски,
/// а свой список R.E.P.O. из пяти адресов при правиле по именам не совпадал
/// ни с чем — молча: <c>nz where 45.82.160.159</c> отвечал «по умолчанию».
/// </remarks>
public sealed class SubnetRoutesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-subnet-{Guid.NewGuid():N}");

    public SubnetRoutesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("45.82.160.159", "45.82.160.159")]
    [InlineData(" 45.82.160.0/24 ", "45.82.160.0/24")]
    [InlineData("45.82.160.159/24", "45.82.160.0/24")]
    [InlineData("10.1.2.3/32", "10.1.2.3")]
    [InlineData("161.156.0.0/18", "161.156.0.0/18")]
    [InlineData("2001:db8::1/48", "2001:db8::/48")]
    [InlineData("2001:db8::1", "2001:db8::1")]
    public void AnAddressOrSubnetIsRecognised(string typed, string expected)
    {
        Assert.Equal(expected, AddressInput.Normalize(typed));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("45")]
    [InlineData("1.2")]
    [InlineData("1.2.3")]
    [InlineData("0.0.0.0/0")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4/x")]
    [InlineData("1.2.3.4 # сервер")]
    [InlineData("")]
    [InlineData(null)]
    public void NotAnAddressIsRefused(string? typed)
    {
        Assert.Null(AddressInput.Normalize(typed));
    }

    /// <summary>Адрес — не имя сайта: прежде маску срезало, и адрес уходил в правило по имени.</summary>
    [Theory]
    [InlineData("45.82.160.0/24")]
    [InlineData("45.82.160.159")]
    public void AnAddressIsNotADomain(string typed)
    {
        Assert.Null(DomainInput.Normalize(typed));
        Assert.Equal("example.com", DomainInput.Normalize("https://example.com/page"));
    }

    private string OwnList(params string[] lines)
    {
        var value = OwnLists.Create("repo", lines, _root);
        return value;
    }

    private (RuleEngine Engine, RuleSet RuleSet) Load(string value, string mode)
    {
        var loaded = RuleSetLoader.Load($"mode: selective\nrules:\n  - match: hostlist\n    value: \"{value}\"\n    mode: {mode}\n");
        RuleSetExpander.Expand(loaded.RuleSet, _root);
        return (loaded, loaded.RuleSet);
    }

    private static ConnectionEvent To(string address, string? host = null) => new()
    {
        Timestamp = DateTimeOffset.Now,
        Protocol = ProtocolKind.Udp,
        RemoteAddress = IPAddress.Parse(address),
        RemotePort = 27001,
        Hostname = host,
    };

    /// <summary>Свой список с адресами совпадает и по адресу, и по имени — одним маршрутом.</summary>
    [Fact]
    public void OwnListAddressesRouteLikeItsNames()
    {
        var value = OwnList("exitgames.com", "45.82.160.0/24", "216.120.180.19");
        var (engine, _) = Load(value, "proxy");

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(To("45.82.160.159")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(To("216.120.180.19")).Mode);
        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(To("8.8.8.8", "gclux1113.exitgames.com")).Mode);
        Assert.NotEqual(RoutingMode.Proxy, engine.Evaluate(To("45.82.161.1")).Mode);
    }

    /// <summary>
    /// В конфиг туннеля адреса своего списка идут ip_cidr того же правила и в маршруты
    /// TUN — иначе при выборочном перехвате UDP к ним в туннель не попал бы.
    /// Имена — без адресов: адрес именем дал бы fakeip несуществующему имени.
    /// </summary>
    [Fact]
    public void OwnListAddressesReachTheTunnelConfig()
    {
        var value = OwnList("exitgames.com", "45.82.160.0/24");
        var (_, ruleSet) = Load(value, "proxy");

        var json = new SingBoxConfigCompiler().Compile(ruleSet, [], new SingBoxOptions()).Json;
        var root = JsonNode.Parse(json)!;

        var rule = root["route"]!["rules"]!.AsArray().OfType<JsonObject>()
            .Single(r => r["ip_cidr"] is JsonArray cidrs && cidrs.Any(c => c!.GetValue<string>() == "45.82.160.0/24"));

        Assert.Contains("exitgames.com", rule["domain_suffix"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.DoesNotContain("45.82.160.0/24", rule["domain_suffix"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Contains("45.82.160.0/24", json);
    }

    /// <summary>Список из одних адресов не выпадает из конфига как «пустой».</summary>
    [Fact]
    public void AnAddressOnlyListIsNotDropped()
    {
        var value = OwnList("45.82.160.159", "216.120.180.19");
        var (engine, ruleSet) = Load(value, "proxy");

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(To("216.120.180.19")).Mode);

        var json = new SingBoxConfigCompiler().Compile(ruleSet, [], new SingBoxOptions()).Json;
        Assert.Contains("216.120.180.19", json);
    }
}
