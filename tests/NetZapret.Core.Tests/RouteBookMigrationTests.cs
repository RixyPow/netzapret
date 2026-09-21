using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Перенос прежних правил и пинов в книгу маршрутов.
/// </summary>
/// <remarks>
/// <para>
/// Самая рискованная часть первой цели 0.7.0: у владельца восемьдесят
/// восемь правил и сто двенадцать пинов — работа месяцев, — и потерять
/// нельзя ни одного.
/// </para>
/// <para>
/// Поэтому проверки построены на обороте: собрать книгу, собрать из неё
/// прежние правила, сравнить. Не «перенеслось похоже», а «собралось
/// обратно то же самое».
/// </para>
/// </remarks>
public sealed class RouteBookMigrationTests
{
    private static RoutingRule List(string name, RoutingMode mode, string? recipe = null) => new()
    {
        Match = MatchKind.HostList,
        Value = $"config/lists/{name}.txt",
        Mode = mode,
        Recipe = recipe,
    };

    private static RuleSet Set(params RoutingRule[] rules) =>
        new() { Rules = rules, DefaultMode = RoutingMode.Desync };

    [Fact]
    public void A_catalogue_list_becomes_a_group()
    {
        // Ради этого группы и заводились: восемьдесят один список иначе
        // развернулся бы в несколько сотен строк отдельных имён.
        var made = RouteBookMigration.From(Set(List("discord", RoutingMode.Desync)));

        var entry = Assert.Single(made.Book.Entries);

        Assert.Equal("discord", entry.Name);
        Assert.True(entry.IsGroup);
    }

    [Fact]
    public void A_domain_rule_keeps_its_name_without_the_star()
    {
        var made = RouteBookMigration.From(Set(new RoutingRule
        {
            Match = MatchKind.Domain,
            Value = "*.svtv.org",
            Mode = RoutingMode.Proxy,
        }));

        Assert.Equal("svtv.org", made.Book.Entries[0].Name);
        Assert.Equal(RouteChoice.Vpn, made.Book.Entries[0].Choice);
    }

    [Fact]
    public void An_address_rule_cannot_be_expressed_and_is_kept()
    {
        // Книга говорит именами, а подсеть именем не назовёшь. У владельца
        // таких четыре — ipset телеграма, дискорда, ютуба и твиттера, —
        // и они ловят соединения, идущие по адресу без всякого имени.
        var made = RouteBookMigration.From(Set(new RoutingRule
        {
            Match = MatchKind.IpSet,
            Value = "config/lists/ipset-telegram.txt",
            Mode = RoutingMode.Proxy,
        }));

        Assert.Empty(made.Book.Entries);
        Assert.Single(made.Kept);
        Assert.Contains(made.Notes, n => n.Contains("по адресам"));
    }

    [Fact]
    public void A_recipe_survives_the_move()
    {
        var made = RouteBookMigration.From(
            Set(List("instagram", RoutingMode.Desync, "hostfakesplit-stealth")));

        Assert.Equal("hostfakesplit-stealth", made.Book.Entries[0].Recipe);
    }

    [Fact]
    public void A_recipe_with_spaces_in_its_name_survives_too()
    {
        // «Cloudflare WARP API» — имя секции пресета, и пробелы в нём есть.
        // Строка книги кончается рецептом, поэтому он берётся целиком.
        var made = RouteBookMigration.From(
            Set(List("facebook", RoutingMode.Desync, "Cloudflare WARP API")));

        var text = RouteBookFile.Write(made.Book);
        var back = RouteBookFile.Parse(text);

        Assert.Equal("Cloudflare WARP API", back.Entries[0].Recipe);
    }

    [Fact]
    public void Everything_comes_back_the_same()
    {
        // Главная проверка. Перенос, который нельзя обратить, проверяется
        // только на глаз — а на глаз восемьдесят восемь правил не проверишь.
        var was = Set(
            List("discord", RoutingMode.Desync),
            List("github", RoutingMode.Direct),
            List("telegram", RoutingMode.Proxy),
            List("instagram", RoutingMode.Desync, "hostfakesplit-stealth"),
            new RoutingRule { Match = MatchKind.Domain, Value = "*.svtv.org", Mode = RoutingMode.Proxy });

        var back = RouteBookMigration.Back(RouteBookMigration.From(was).Book);

        static string Key(RoutingRule r) => $"{RouteBookMigration.NameOf(r)}|{r.Mode}|{r.Recipe}";

        Assert.Equal(
            was.Rules.Select(Key).OrderBy(k => k, StringComparer.Ordinal),
            back.Select(Key).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void A_pin_becomes_a_route_of_its_own()
    {
        var made = RouteBookMigration.From(Set(), ["instagram.com"]);

        Assert.Equal(RouteChoice.Pin, Assert.Single(made.Book.Entries).Choice);
    }

    [Fact]
    public void A_pin_does_not_come_back_as_a_routing_rule()
    {
        // Пин живёт в hosts и правилом маршрутизации никогда не был.
        // Собрав его обратно, мы выдумали бы правило, которого не было.
        var book = RouteBookMigration.From(Set(), ["instagram.com"]).Book;

        Assert.Empty(RouteBookMigration.Back(book));
    }

    [Fact]
    public void A_pinned_name_that_is_also_desynced_is_named_a_clash()
    {
        // Ровно случай 21.09: Instagram был прибит и потому выведен
        // из-под десинка целиком, рецепт не применялся ни разу, а разбор
        // занял час. Перенос находит это сразу.
        var made = RouteBookMigration.From(
            Set(List("instagram.com", RoutingMode.Desync)),
            ["instagram.com"]);

        Assert.NotEmpty(made.Book.Clashes);
        Assert.Contains(made.Notes, n => n.Contains("противоречий"));
    }

    [Fact]
    public void A_pinned_name_that_is_direct_is_not_a_clash()
    {
        // tmdb у владельца и выведен напрямую, и прибит. Оба означают
        // «мимо всего», и жаловаться тут не на что.
        var made = RouteBookMigration.From(
            Set(List("tmdb.org", RoutingMode.Direct)),
            ["tmdb.org"]);

        Assert.Empty(made.Book.Clashes);
    }

    [Fact]
    public void An_empty_set_gives_an_empty_book()
    {
        var made = RouteBookMigration.From(Set());

        Assert.Empty(made.Book.Entries);
        Assert.Empty(made.Kept);
        Assert.Empty(made.Notes);
    }
}
