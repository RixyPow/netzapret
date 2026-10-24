using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Смена портов у Hysteria2 (port hopping) — с 24.10.
/// </summary>
/// <remarks>
/// Владелец: «Добавь в Hysteria2 смену портов: диапазон в ссылке и mport —
/// в server_ports движка». До того официальная ссылка со списком портов
/// в адресе не читалась вовсе («порт … не разбирается»), а mport молча
/// пропускался. Главная проверка — последняя: собранный конфиг принимает
/// сам движок, а одиночный порт без двоеточия в server_ports он не принимает
/// и роняет конфиг целиком.
/// </remarks>
public sealed class PortHoppingTests
{
    [Fact]
    public void Official_link_with_a_port_list_in_the_address_is_read()
    {
        var server = Parse(
            "hysteria2://s3cret@hop.example.com:443,20000-50000/"
            + "?sni=hop.example.com&obfs=salamander&obfs-password=mask#HOP");

        Assert.Equal(ProxyProtocol.Hysteria2, server.Protocol);
        Assert.Equal("hop.example.com", server.Host);
        Assert.Equal(443, server.Port);
        Assert.Equal("443:443,20000:50000", server.HopPorts);
        Assert.Equal("s3cret", server.Credential);
        Assert.Equal("hop.example.com", server.Sni);
        Assert.Equal("salamander", server.ObfsType);
        Assert.Equal("mask", server.ObfsPassword);
        Assert.Equal("HOP", server.Tag);
    }

    [Fact]
    public void A_range_alone_in_the_address_puts_the_server_at_its_first_port()
    {
        var server = Parse("hy2://s3cret@hop.example.com:20000-50000?sni=hop.example.com");

        Assert.Equal(20000, server.Port);
        Assert.Equal("20000:50000", server.HopPorts);
    }

    /// <summary>
    /// mport — запись v2rayN: порт адреса в смену не входит, как и у него.
    /// </summary>
    [Fact]
    public void Mport_gives_the_ports_to_hop_over()
    {
        var server = Parse("hysteria2://s3cret@hop.example.com:443?sni=hop.example.com&mport=20000-30000,40000:45000,50000#V2");

        Assert.Equal(443, server.Port);
        Assert.Equal("20000:30000,40000:45000,50000:50000", server.HopPorts);
    }

    [Theory]
    [InlineData("8443")] // одиночный — не смена: увёл бы с порта, названного в адресе
    [InlineData("abc")] // неразборчивый — сервер живёт без смены, как прежде
    [InlineData("")]
    public void Mport_without_a_list_leaves_the_server_on_its_port(string mport)
    {
        var server = Parse($"hysteria2://s3cret@hop.example.com:443?sni=hop.example.com&mport={mport}");

        Assert.Equal(443, server.Port);
        Assert.Null(server.HopPorts);
    }

    [Fact]
    public void Plain_link_has_no_hopping() =>
        Assert.Null(Parse("hysteria2://s3cret@nl.example.com:4443/?sni=nl.example.com#NL").HopPorts);

    /// <summary>
    /// Движок развернул бы «50000:20000» в пустой список и молча ушёл
    /// без смены; официальный клиент такой диапазон переворачивает.
    /// </summary>
    [Fact]
    public void Reversed_range_is_turned_around() =>
        Assert.Equal("20000:50000", Parse("hysteria2://s3cret@hop.example.com:50000-20000/").HopPorts);

    [Theory]
    [InlineData("443,0-100")]
    [InlineData("443,65000-70000")]
    [InlineData("443,x-100")]
    [InlineData("443,-")]
    public void Broken_list_in_the_address_is_an_error(string ports)
    {
        Assert.False(ProxyUriParser.TryParse($"hysteria2://s3cret@hop.example.com:{ports}/?sni=x", out _, out var error));
        Assert.Contains("порты", error);
    }

    [Fact]
    public void Ipv6_address_keeps_its_colons_apart_from_the_list()
    {
        var server = Parse("hysteria2://s3cret@[2001:db8::1]:443,20000-20010/?insecure=1#V6");

        Assert.Equal("2001:db8::1", server.Host);
        Assert.Equal(443, server.Port);
        Assert.Equal("443:443,20000:20010", server.HopPorts);
    }

    [Fact]
    public void Address_list_and_mport_go_together_without_repeats() =>
        Assert.Equal(
            "443:443,20000:30000,40000:40000",
            Parse("hysteria2://s3cret@hop.example.com:443,20000-30000/?mport=20000-30000,40000").HopPorts);

    /// <summary>
    /// Конфиг sing-box: v2rayN при смене не пишет server_port вовсе,
    /// а одиночный порт в server_ports сам движок не принял бы.
    /// </summary>
    [Fact]
    public void Sing_box_config_brings_its_server_ports()
    {
        var (servers, errors) = SubscriptionParser.ParseBody("""
            { "outbounds": [
              { "type": "hysteria2", "tag": "HOP", "server": "hop.example.com",
                "server_ports": ["20000:50000", "443"], "password": "s3cret" }
            ] }
            """);

        Assert.Empty(errors);
        var server = Assert.Single(servers);
        Assert.Equal(20000, server.Port);
        Assert.Equal("20000:50000,443:443", server.HopPorts);
    }

    [Fact]
    public void Engine_gets_server_ports_in_its_own_notation()
    {
        var outbound = ProbeOutbound(Parse("hysteria2://s3cret@hop.example.com:443,20000-50000/?sni=hop.example.com#HOP"));

        Assert.Equal(["443:443", "20000:50000"], outbound["server_ports"]!.AsArray().Select(n => (string)n!));
    }

    [Fact]
    public void Without_hopping_the_outbound_has_no_server_ports() =>
        Assert.Null(ProbeOutbound(Parse("hysteria2://s3cret@nl.example.com:4443/?sni=nl.example.com#NL"))["server_ports"]);

    [Fact]
    public void Config_with_hopping_passes_sing_box_check()
    {
        var servers = new[]
        {
            Parse("hysteria2://s3cret@a.example.com:443,20000-50000/?sni=a.example.com&obfs=salamander&obfs-password=mask#A"),
            Parse("hysteria2://s3cret@b.example.com:443?sni=b.example.com&mport=20000-30000,40000#B"),
        };
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        Assert.Equal(2, result.UsedServers.Count);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    private static ProxyServer Parse(string link)
    {
        Assert.True(ProxyUriParser.TryParse(link, out var server, out var error), error);
        return server!;
    }

    private static JsonObject ProbeOutbound(ProxyServer server)
    {
        var json = new SingBoxConfigCompiler().CompileProbeConfig(server, 21099, logPath: null);

        return JsonNode.Parse(json)!["outbounds"]!.AsArray()
            .Single(o => (string?)o!["tag"] == "probe-out")!.AsObject();
    }
}
