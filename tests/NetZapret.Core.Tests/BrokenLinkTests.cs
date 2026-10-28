using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ключи из публичных подписок с кривизной, которую движок не прощает
/// или которую разбор не понимал (обсуждения №38 и №41, 28.10).
/// </summary>
public sealed class BrokenLinkTests
{
    private static ProxyServer Parse(string uri)
    {
        Assert.True(ProxyUriParser.TryParse(uri, out var server, out var error), error);
        return server!;
    }

    private static string Ws(string path, string tag) =>
        "vless://11111111-2222-3333-4444-555555555555@w.example.com:443?security=tls&type=ws&sni=w.example.com&path="
        + path + "#" + tag;

    [Theory]
    [InlineData("/a%2", true)]
    [InlineData("/a%zz", true)]
    [InlineData("/a%", true)]
    [InlineData("/a%2Fb", false)]
    [InlineData("/plain", false)]
    [InlineData(null, false)]
    public void A_broken_escape_is_told_from_a_good_one(string? path, bool broken)
    {
        Assert.Equal(broken, ProxyServer.HasBrokenEscape(path));
    }

    /// <summary>
    /// Один сервер с битым путём не роняет туннель: его нет в конфиге,
    /// а соседний проходит <c>sing-box check</c> (№38: «xhttp: invalid URL
    /// escape "%2" parse path» у подписки на 8 028 серверов).
    /// </summary>
    [Fact]
    public void A_server_with_a_broken_path_is_left_out()
    {
        var bad = Parse(Ws("%2Fapi%25zz", "Битый"));
        var good = Parse(Ws("%2Fapi%252Fb", "Годный"));

        Assert.Equal("/api%zz", bad.Path);
        Assert.False(bad.IsUsableOutbound);
        Assert.True(good.IsUsableOutbound);

        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var result = new SingBoxConfigCompiler().Compile(engine.RuleSet, [bad, good], new SingBoxOptions());

        Assert.Single(result.UsedServers);

        var check = SingBoxCheck.Run(result.Json);
        Assert.True(check.Ok, check.Said);
    }
}
