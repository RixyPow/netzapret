using System.Text.Json.Nodes;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Сервер, на котором движок отказался подняться, уходит из конфига
/// (обсуждение №38, 28.10: один ключ из подписки на 8 028 ронял туннель).
/// </summary>
public sealed class BrokenOutboundTests
{
    private static ProxyServer Parse(string uri)
    {
        Assert.True(ProxyUriParser.TryParse(uri, out var server, out var error), error);
        return server!;
    }

    private static string Ws(string tag) =>
        "vless://11111111-2222-3333-4444-555555555555@w.example.com:443?security=tls&type=ws&sni=w.example.com&path=%2Fok#" + tag;

    /// <summary>Конфиг кодом движка, у одного сервера — путь, который sing-box не примет.</summary>
    private static string Spoiled(params string[] tags)
    {
        var engine = RuleSetLoader.Load("mode: selective\nrules: []\n");
        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, tags.Select(t => Parse(Ws(t))).ToList(), new SingBoxOptions()).Json;

        // Наш разбор такой путь отсеивает; здесь он — образец кривизны, которую
        // разбор ещё не знает.
        var root = JsonNode.Parse(json)!;
        var bad = root["outbounds"]!.AsArray().OfType<JsonObject>().First(o => (string?)o["tag"] == tags[0]);
        bad["transport"]!["path"] = "/a%zz";

        return root.ToJsonString();
    }

    [Fact]
    public void The_refused_server_leaves_and_the_engine_comes_up()
    {
        var config = Spoiled("Битый", "Годный");

        var refused = SingBoxCheck.Run(config);
        Assert.False(refused.Ok);
        Assert.Matches(@"initialize outbound\[\d+\]", refused.Said);

        var repaired = BrokenOutbound.Remove(config, refused.Said);

        Assert.NotNull(repaired);
        Assert.Equal("Битый", repaired.Value.Tag);
        Assert.DoesNotContain("\"Битый\"", repaired.Value.Config);
        Assert.Contains("\"Годный\"", repaired.Value.Config);

        var check = SingBoxCheck.Run(repaired.Value.Config);
        Assert.True(check.Ok, check.Said);
    }

    /// <summary>Последний сервер в группе не убирается: группа без него уронила бы движок сама.</summary>
    [Fact]
    public void The_only_server_is_not_removed()
    {
        var config = Spoiled("Единственный");

        var refused = SingBoxCheck.Run(config);
        Assert.False(refused.Ok);

        Assert.Null(BrokenOutbound.Remove(config, refused.Said));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("FATAL[0000] start service: listen tcp 127.0.0.1:9090: bind: address already in use")]
    [InlineData("FATAL[0000] create service: initialize outbound[999]: что-то")]
    public void Other_failures_are_not_repaired(string? fatal)
    {
        Assert.Null(BrokenOutbound.Remove(Spoiled("Первый", "Второй"), fatal));
    }

    /// <summary>Группы и direct — не серверы: их отказ — поломка конфига, а не ключа.</summary>
    [Fact]
    public void A_group_is_never_removed()
    {
        var config = Spoiled("Первый", "Второй");
        var outbounds = JsonNode.Parse(config)!["outbounds"]!.AsArray();
        int direct = outbounds.Select((o, i) => (o, i)).First(p => (string?)p.o!["type"] == "direct").i;

        Assert.Null(BrokenOutbound.Remove(config, $"FATAL[0000] create service: initialize outbound[{direct}]: что-то"));
    }
}
