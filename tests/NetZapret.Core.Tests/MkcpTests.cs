using System.Text;
using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// mKCP доходит до движка, а транспорт, которого движок не повезёт, — нет.
/// </summary>
/// <remarks>
/// До 30.10 незнакомый транспорт собирался обычным TCP. Какие маски сборка
/// extended повторяет — замер на петле против Xray 26 (см. <see cref="MkcpSettings"/>).
/// </remarks>
public sealed class MkcpTests
{
    private const string Id = "b7f3c1d2-4a5e-4c11-9f2b-8e7d6a1c0f33";

    private static ProxyServer Link(string query, string name = "k")
    {
        Assert.True(ProxyUriParser.TryParse($"vless://{Id}@kcp.example.com:4443?{query}#{name}", out var server, out var error), error);
        return server!;
    }

    private static JsonObject Options(ProxyServer server) =>
        JsonNode.Parse(server.KcpOptions!)!.AsObject();

    private static string Fm(string json) => Uri.EscapeDataString(json);

    [Fact]
    public void An_old_style_link_carries_header_seed_and_numbers()
    {
        var server = Link("type=kcp&headerType=srtp&seed=s3cr3t&mtu=1350&tti=20&congestion=1");

        Assert.Equal("mkcp", server.Transport);
        Assert.Null(server.Unusable);

        var options = Options(server);
        Assert.Equal("srtp", options["header_type"]!.GetValue<string>());
        Assert.Equal("s3cr3t", options["seed"]!.GetValue<string>());
        Assert.Equal(1350, options["mtu"]!.GetValue<long>());
        Assert.Equal(20, options["tti"]!.GetValue<long>());
        Assert.True(options["congestion"]!.GetValue<bool>());
    }

    [Fact]
    public void Legacy_masks_of_xray_26_become_header_and_seed()
    {
        var plain = Link("type=kcp&fm=" + Fm("""{"udp":[{"type":"mkcp-legacy"},{"type":"mkcp-legacy","settings":{"header":"wechat"}}]}"""));
        var seeded = Link("type=kcp&fm=" + Fm("""[{"type":"mkcp-legacy","settings":{"value":"s3cr3t"}}]"""));

        Assert.Null(plain.Unusable);
        Assert.Equal("wechat-video", Options(plain)["header_type"]!.GetValue<string>());
        Assert.Null(Options(plain)["seed"]);

        Assert.Null(seeded.Unusable);
        Assert.Equal("s3cr3t", Options(seeded)["seed"]!.GetValue<string>());
        Assert.Null(Options(seeded)["header_type"]);
    }

    /// <summary>Так у владельца лежит «Польша — XDNS»: одна маска, не список.</summary>
    [Fact]
    public void A_dns_tunnel_is_refused_by_name()
    {
        var server = Link("type=kcp&mtu=130&fm=" + Fm(
            """{"type":"xdns","settings":{"domains":[{"name":"a.example.com","edns0":1232,"types":[1]}],"resolvers":[{"type":"udp","settings":{"addr":"77.88.8.8:53"}}]}}"""));

        Assert.False(server.IsSupportedBySingBox);
        Assert.Equal("маскировку «xdns» движок не знает", server.Unusable);
    }

    [Theory]
    [InlineData("""{"udp":[]}""", "mKCP без защиты (Xray 26) движок не повторит")]
    [InlineData("""[{"type":"mkcp-legacy","settings":{"header":"srtp"}},{"type":"mkcp-legacy"}]""", "маски mKCP в порядке, которого движок не повторит")]
    [InlineData("""[{"type":"mkcp-legacy"},{"type":"mkcp-legacy","settings":{"value":"x"}}]""", "маски mKCP в порядке, которого движок не повторит")]
    [InlineData("""[{"type":"mkcp-legacy"},{"type":"mkcp-legacy","settings":{"header":"dns","value":"www.baidu.com"}}]""", "заголовок mKCP «dns» движок не знает")]
    [InlineData("{oops", "маскировку mKCP (fm) не разобрать")]
    public void Masks_the_engine_cannot_repeat_are_refused(string fm, string why)
    {
        var server = Link("type=kcp&fm=" + Fm(fm));

        Assert.False(server.IsSupportedBySingBox);
        Assert.Equal(why, server.Unusable);
    }

    [Fact]
    public void An_unknown_header_is_refused_not_silently_dropped()
    {
        // Сборка заменила бы его на «none» без единого слова.
        Assert.Equal("заголовок mKCP «dns» движок не знает", Link("type=kcp&headerType=dns").Unusable);
        Assert.Equal("заголовок mKCP «foo» движок не знает", Link("type=kcp&headerType=foo").Unusable);
    }

    [Fact]
    public void Numbers_out_of_range_fall_back_to_defaults()
    {
        // tti больше 1000 сборка делит на ноль; mtu 130 — DNS-туннель, не mKCP.
        var options = Options(Link("type=kcp&tti=5000&mtu=130&uplinkCapacity=-1"));

        Assert.Null(options["tti"]);
        Assert.Null(options["mtu"]);
        Assert.Null(options["uplink_capacity"]);
    }

    [Theory]
    [InlineData("type=quic", "транспорт «quic» движок не знает")]
    [InlineData("type=meek", "транспорт «meek» движок не знает")]
    [InlineData("type=tcp&headerType=http", "маскировку TCP «http» движок не знает")]
    public void A_transport_the_engine_cannot_carry_is_skipped_not_dialed_as_tcp(string query, string why)
    {
        var server = Link(query);

        Assert.False(server.IsSupportedBySingBox);
        Assert.Equal(why, server.Unusable);
    }

    [Theory]
    [InlineData("type=raw", "tcp")]
    [InlineData("type=tcp&headerType=none", "tcp")]
    [InlineData("type=splithttp&path=%2Fx", "xhttp")]
    [InlineData("type=h2&path=%2Fx", "http")]
    [InlineData("type=KCP", "mkcp")]
    public void Xray_names_become_engine_names(string query, string transport)
    {
        var server = Link(query);

        Assert.Equal(transport, server.Transport);
        Assert.Null(server.Unusable);
    }

    [Fact]
    public void Vmess_keeps_header_in_type_and_seed_in_path()
    {
        var json = $$"""{"v":"2","ps":"vm","add":"vm.example.com","port":"4443","id":"{{Id}}","net":"kcp","type":"utp","path":"s3cr3t"}""";
        Assert.True(ProxyUriParser.TryParse("vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json)), out var server, out var error), error);

        Assert.Equal("mkcp", server!.Transport);
        Assert.Null(server.Path);
        Assert.Equal("utp", Options(server)["header_type"]!.GetValue<string>());
        Assert.Equal("s3cr3t", Options(server)["seed"]!.GetValue<string>());
    }

    [Fact]
    public void Xray_and_sing_box_configs_are_read_the_same_way()
    {
        var (fromXray, xrayErrors) = XrayConfigParser.Parse($$"""
            { "outbounds": [
              { "protocol": "vless", "tag": "XK",
                "settings": { "vnext": [ { "address": "x.example.com", "port": 4443, "users": [ { "id": "{{Id}}", "encryption": "none" } ] } ] },
                "streamSettings": { "network": "kcp", "kcpSettings": { "mtu": 1350, "tti": 50 },
                  "finalmask": { "udp": [ { "type": "mkcp-legacy", "settings": { "value": "s3cr3t" } },
                                          { "type": "mkcp-legacy", "settings": { "header": "dtls" } } ] } } },
              { "protocol": "vless", "tag": "XO",
                "settings": { "vnext": [ { "address": "o.example.com", "port": 4443, "users": [ { "id": "{{Id}}", "encryption": "none" } ] } ] },
                "streamSettings": { "network": "mkcp", "kcpSettings": { "header": { "type": "wireguard" }, "seed": "old" } } }
            ] }
            """);

        Assert.Empty(xrayErrors);
        Assert.Equal("dtls", Options(fromXray[0])["header_type"]!.GetValue<string>());
        Assert.Equal("s3cr3t", Options(fromXray[0])["seed"]!.GetValue<string>());
        Assert.Equal("wireguard", Options(fromXray[1])["header_type"]!.GetValue<string>());
        Assert.Equal("old", Options(fromXray[1])["seed"]!.GetValue<string>());

        var (fromSingBox, singBoxErrors) = SubscriptionParser.ParseBody($$"""
            { "outbounds": [
              { "type": "vless", "tag": "SK", "server": "s.example.com", "server_port": 4443, "uuid": "{{Id}}",
                "transport": { "type": "mkcp", "mtu": 1200, "header_type": "srtp", "seed": "s3cr3t" } },
              { "type": "vless", "tag": "SQ", "server": "q.example.com", "server_port": 443, "uuid": "{{Id}}",
                "transport": { "type": "quic" } }
            ] }
            """);

        Assert.Empty(singBoxErrors);
        Assert.Null(fromSingBox[0].Unusable);
        Assert.Equal(1200, Options(fromSingBox[0])["mtu"]!.GetValue<long>());
        Assert.Equal("транспорт «quic» движок не знает", fromSingBox[1].Unusable);
    }

    [Fact]
    public void The_engine_takes_what_we_build()
    {
        var servers = new[]
        {
            Link("type=kcp&headerType=wechat-video&seed=s3cr3t&mtu=1350&tti=20&uplinkCapacity=5&downlinkCapacity=20", "k1"),
            Link("type=kcp&fm=" + Fm("""[{"type":"mkcp-legacy"},{"type":"mkcp-legacy","settings":{"header":"srtp"}}]"""), "k2"),
            Link("type=kcp", "k3"),
        };

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, servers, new SingBoxOptions());

        var transports = JsonNode.Parse(result.Json)!["outbounds"]!.AsArray().OfType<JsonObject>()
            .Where(o => (string?)o["type"] == "vless")
            .Select(o => o["transport"]!.AsObject())
            .ToList();

        Assert.Equal(3, transports.Count);
        Assert.All(transports, t => Assert.Equal("mkcp", t["type"]!.GetValue<string>()));
        Assert.Equal("wechat-video", transports[0]["header_type"]!.GetValue<string>());
        Assert.Equal(20, transports[0]["tti"]!.GetValue<long>());

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }
}
