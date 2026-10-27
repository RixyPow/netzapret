using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Flow VLESS — значением, которое знает sing-box (обсуждение №38, 27.10:
/// «unsupported flow: xtls-rprx-vision-udp443» ронял туннель целиком).
/// </summary>
public sealed class VlessFlowsTests
{
    [Theory]
    [InlineData("xtls-rprx-vision", VlessFlows.Vision)]
    [InlineData("xtls-rprx-vision-udp443", VlessFlows.Vision)]
    [InlineData("XTLS-RPRX-VISION", VlessFlows.Vision)]
    [InlineData(" xtls-rprx-vision ", VlessFlows.Vision)]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Vision_and_its_xray_client_variant_become_vision(string? flow, string expected) =>
        Assert.Equal(expected, VlessFlows.ForSingBox(flow));

    /// <summary>Незнакомый flow — не годится: сервер пропускается, а не роняет туннель.</summary>
    [Theory]
    [InlineData("xtls-rprx-direct")]
    [InlineData("xtls-rprx-origin")]
    [InlineData("xtls-rprx-splice")]
    [InlineData("none")]
    public void An_unknown_flow_makes_the_server_unusable(string flow)
    {
        Assert.Null(VlessFlows.ForSingBox(flow));

        var server = Vless("x", flow);

        Assert.False(server.IsUsableOutbound);
        Assert.True((server with { Flow = "xtls-rprx-vision-udp443" }).IsUsableOutbound);
    }

    [Fact]
    public void The_client_variant_starts_as_vision_and_an_unknown_flow_is_left_out()
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet,
            [Vless("udp443", "xtls-rprx-vision-udp443"), Vless("direct", "xtls-rprx-direct"), Vless("plain", null)],
            new SingBoxOptions());

        Assert.Equal(["udp443", "plain"], result.UsedServers.Select(s => s.Tag));
        Assert.Contains("\"flow\":\"xtls-rprx-vision\"", result.Json.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("xtls-rprx-vision-udp443", result.Json, StringComparison.Ordinal);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }

    private static ProxyServer Vless(string tag, string? flow) => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = tag,
        Host = "1.2.3.4",
        Port = 443,
        Credential = "11111111-2222-3333-4444-555555555555",
        Security = "tls",
        Sni = "t.example.com",
        Flow = flow,
    };
}
