using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Имена своих рецептов — только у правила, которое выигрывает их первым (27.10, seriesgraph.com).
/// </summary>
public sealed class OwnRecipeNamesTests
{
    private static RoutingRule List(string value, RoutingMode mode, string? recipe, params string[] names)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = value, Mode = mode, Recipe = recipe, Source = RuleSource.User };
        rule.LoadHostList(names);
        return rule;
    }

    private static RoutingRule Domain(string value, RoutingMode mode, string? recipe = null) =>
        new() { Match = MatchKind.Domain, Value = value, Mode = mode, Recipe = recipe, Source = RuleSource.User };

    [Fact]
    public void A_name_goes_only_to_the_recipe_of_the_rule_that_wins_it()
    {
        // Как у владельца: список с «pass» выше в файле, своё имя с подобранным
        // рецептом ниже, и «pass» раньше встретился у другого имени. Движок
        // ставит свои имена раньше списков сервисов — seriesgraph.com достаётся 4pda.
        var engine = RuleEngine.Build(
            [
                List("config/lists/seriesgraph.txt", RoutingMode.Desync, "pass", "seriesgraph.com"),
                Domain("*.outlook.live.com", RoutingMode.Desync, "pass"),
                Domain("*.seriesgraph.com", RoutingMode.Desync, "seqovl-4pda"),
                Domain("*.itch.io", RoutingMode.Desync, "seqovl-4pda"),
            ],
            RoutingMode.Direct);

        var winning = OwnRecipeNames.Winning(engine.RuleSet)
            .GroupBy(w => w.Rule.Recipe!)
            .ToDictionary(g => g.Key, g => g.SelectMany(w => w.Names).ToList());

        Assert.Equal(["outlook.live.com"], winning["pass"]);
        Assert.Equal(["seriesgraph.com", "itch.io"], winning["seqovl-4pda"]);
    }

    [Fact]
    public void A_name_taken_by_an_earlier_direct_or_vpn_rule_gets_no_recipe()
    {
        var engine = RuleEngine.Build(
            [
                Domain("*.api.example.com", RoutingMode.Proxy),
                List("config/lists/example.txt", RoutingMode.Desync, "multidisorder", "example.com", "api.example.com", "cdn.example.net"),
            ],
            RoutingMode.Direct);

        var (_, names) = Assert.Single(OwnRecipeNames.Winning(engine.RuleSet));

        Assert.Equal(["example.com", "cdn.example.net"], names);
    }
}
