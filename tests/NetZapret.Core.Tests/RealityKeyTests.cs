using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// REALITY без годного ключа не попадает в конфиг (обсуждение №38, 25.10:
/// «initialize outbound[198]: invalid public_key» ронял туннель целиком).
/// </summary>
public sealed class RealityKeyTests
{
    /// <summary>Выдуманный ключ: 32 байта в base64url, как пишет xray x25519.</summary>
    private const string Key = "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0";

    private static ProxyServer Parse(string uri)
    {
        Assert.True(ProxyUriParser.TryParse(uri, out var server, out var error), error);
        return server!;
    }

    private static string Link(string? pbk, string tag) =>
        "vless://11111111-2222-3333-4444-555555555555@r.example.com:443?security=reality&type=tcp&sni=www.example.com&fp=chrome"
        + (pbk is null ? string.Empty : "&pbk=" + pbk) + "&sid=ab#" + tag;

    [Theory]
    [InlineData(Key, true)]
    [InlineData("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI+T4E7RoLJS0=", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("abc", false)]
    [InlineData("jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI", false)]
    [InlineData("не ключ вовсе", false)]
    public void Only_a_32_byte_key_is_a_reality_key(string? key, bool valid)
    {
        Assert.Equal(valid, ProxyServer.IsRealityKey(key));
    }

    [Fact]
    public void A_reality_server_without_a_key_is_left_out_and_the_rest_start()
    {
        var good = Parse(Link(Key, "good"));
        var missing = Parse(Link(null, "missing"));
        var broken = Parse(Link("abc", "broken"));
        var plainTls = Parse("vless://11111111-2222-3333-4444-555555555555@t.example.com:443?security=tls&type=tcp&sni=t.example.com#tls");

        Assert.True(good.IsUsableOutbound);
        Assert.False(missing.IsUsableOutbound);
        Assert.False(broken.IsUsableOutbound);
        Assert.True(plainTls.IsUsableOutbound);

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [good, missing, broken, plainTls], new SingBoxOptions());

        Assert.Equal(2, result.UsedServers.Count);
        Assert.DoesNotContain(result.UsedServers, s => s.Tag is "missing" or "broken");

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }
}
