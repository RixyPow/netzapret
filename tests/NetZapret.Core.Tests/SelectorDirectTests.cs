using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Прямой выход — участник группы выбора.
/// </summary>
/// <remarks>
/// Без этого обход туннеля неосуществим: Clash API переключает группу
/// только на того, кто в ней состоит, и попытка увести трафик мимо мёртвых
/// выходов кончалась бы отказом движка — тихо, потому что переключение
/// возвращает лишь «получилось или нет».
/// </remarks>
public sealed class SelectorDirectTests
{
    private static ProxyServer Server(string tag) => new()
    {
        Protocol = ProxyProtocol.Hysteria2,
        Tag = tag,
        Host = tag.ToLowerInvariant() + ".example.com",
        Port = 4443,
        Credential = "PLACEHOLDER",
        Transport = "udp",
        Security = "tls",
        Sni = tag.ToLowerInvariant() + ".example.com",
    };

    private static JsonElement Compile(SingBoxOptions? options = null)
    {
        var engine = RuleSetLoader.Load("rules: []");

        var result = new SingBoxConfigCompiler().Compile(
            engine.RuleSet,
            [Server("NL"), Server("FI")],
            options ?? new SingBoxOptions());

        return JsonDocument.Parse(result.Json).RootElement.Clone();
    }

    private static JsonElement Group(JsonElement root, string type) =>
        root.GetProperty("outbounds").EnumerateArray()
            .Single(o => o.TryGetProperty("type", out var t) && t.GetString() == type);

    private static List<string> Members(JsonElement group) =>
        group.GetProperty("outbounds").EnumerateArray()
            .Select(m => m.GetString()!)
            .ToList();

    [Fact]
    public void The_selector_can_be_switched_to_direct()
    {
        Assert.Contains("direct", Members(Group(Compile(), "selector")));
    }

    [Fact]
    public void Auto_selection_does_not_consider_direct()
    {
        // urltest берёт быстрейшего, а прямой выход быстрее любого туннеля
        // по построению. Попади он сюда — автоподбор выбирал бы его всегда,
        // и туннеля не было бы вовсе.
        Assert.DoesNotContain("direct", Members(Group(Compile(), "urltest")));
    }

    [Fact]
    public void Direct_is_not_what_the_group_points_at_by_default()
    {
        // Ошибка здесь означала бы туннель, который никогда не включается.
        Assert.Equal(
            "auto-latency",
            Group(Compile(), "selector").GetProperty("default").GetString());
    }

    [Fact]
    public void A_pinned_server_still_wins_the_default()
    {
        var selector = Group(
            Compile(new SingBoxOptions { PreferredServerTag = "FI" }),
            "selector");

        Assert.Equal("FI", selector.GetProperty("default").GetString());
        Assert.Contains("direct", Members(selector));
    }

    [Fact]
    public void Direct_comes_after_the_servers()
    {
        // В чужих панелях управления группа показывается списком,
        // и прямой выход уместнее после серверов, а не перед ними.
        Assert.Equal("direct", Members(Group(Compile(), "selector"))[^1]);
    }

    [Fact]
    public void The_outbound_it_points_at_exists()
    {
        // Ссылка на несуществующий outbound не даёт конфигу запуститься —
        // то есть ошибка здесь роняет не обход, а весь туннель.
        var tags = Compile().GetProperty("outbounds").EnumerateArray()
            .Select(o => o.TryGetProperty("tag", out var t) ? t.GetString() : null)
            .ToList();

        Assert.Contains("direct", tags);
    }
}
