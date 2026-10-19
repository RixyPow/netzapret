using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Список сайтов, вставленный в «Маршруты» разом (идея №14, 19.10).
/// </summary>
public sealed class RouteListInputTests
{
    [Fact]
    public void Lines_commas_spaces_and_comments_split_the_list()
    {
        var parts = RouteListInput.Split("7tv.app\r\n7tv.io, 7tv.gg;  example.com\texample.org\n# комментарий\nrutracker.org # трекер\n\n");

        Assert.Equal(["7tv.app", "7tv.io", "7tv.gg", "example.com", "example.org", "rutracker.org"], parts);
    }

    [Theory]
    [InlineData("example.com", false)]
    [InlineData("  example.com  ", false)]
    [InlineData("example.com\nexample.org", true)]
    [InlineData("example.com, example.org", true)]
    public void A_list_is_two_parts_or_more(string text, bool expected) =>
        Assert.Equal(expected, RouteListInput.IsList(text));

    [Theory]
    [InlineData("kws2.pclead.co.uk", true)]
    [InlineData("pclead.co.uk", true)]
    [InlineData("kws5.pyatdesyatdva.co.uk", true)]
    [InlineData("kws7.somethingnew.co.uk", true)]
    [InlineData("www.msftconnecttest.com", true)]
    [InlineData("msftncsi.com", true)]
    [InlineData("bbc.co.uk", false)]
    [InlineData("example.com", false)]
    public void Names_of_the_program_itself_are_recognised(string name, bool expected) =>
        Assert.Equal(expected, RouteListInput.IsProgramOwn(name));

    [Fact]
    public void Without_an_engine_names_are_cleaned_reduced_and_sorted_out()
    {
        UserRuleEntry[] own = [new() { Match = MatchKind.Domain, Value = "*.mine.com", Mode = RoutingMode.Proxy }];

        var plan = RouteListInput.Plan(
            "https://www.7tv.app/emotes\ncdn.7tv.app\n7tv.io\nmine.com\nkws2.pclead.co.uk\nпривет\n203.0.113.0/24\n7TV.APP",
            engine: null, own, RoutingMode.Proxy);

        Assert.Equal(["7tv.app", "7tv.io"], plan.Sites);
        Assert.Equal(["203.0.113.0/24"], plan.Subnets);
        Assert.Equal(["mine.com"], plan.AlreadyOwn);
        Assert.Equal(["pclead.co.uk"], plan.ProgramOwn);
        Assert.Equal(["привет"], plan.NotNames);
        Assert.Equal(3, plan.Count);
    }

    [Fact]
    public void Exact_names_stay_when_asked_not_to_reduce()
    {
        var plan = RouteListInput.Plan("cdn.7tv.app\nevents.7tv.io", engine: null, [], RoutingMode.Desync, toSite: false);

        Assert.Equal(["cdn.7tv.app", "events.7tv.io"], plan.Sites);
    }

    [Fact]
    public void The_plan_is_written_as_own_rules_with_one_route()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-routelist-{Guid.NewGuid():N}.yaml");

        try
        {
            var file = UserRulesFile.Load(path);
            var plan = RouteListInput.Plan("7tv.app\n7tv.io\n203.0.113.0/24", engine: null, file.Entries, RoutingMode.Proxy);

            var written = RouteListInput.Write(plan, RoutingMode.Proxy, file);
            file.Save();

            Assert.Equal([(MatchKind.Domain, "*.7tv.app"), (MatchKind.Domain, "*.7tv.io"), (MatchKind.Ip, "203.0.113.0/24")], written);

            var again = UserRulesFile.Load(path);
            Assert.Equal(3, again.Entries.Count(e => e.Mode == RoutingMode.Proxy && e.Recipe is null));

            // Второй раз то же — уже своё, записывать нечего.
            Assert.Equal(0, RouteListInput.Plan("7tv.app\n7tv.io\n203.0.113.0/24", engine: null, again.Entries, RoutingMode.Proxy).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// С движком: своё правило — «уже есть», сервис тем же путём — двойник,
    /// сервис другим путём — своё перекроет, нижний слой зон не в счёт.
    /// </summary>
    [Fact]
    public void The_engine_tells_own_rules_duplicates_and_overrides_apart()
    {
        var engine = RuleEngine.Build(
            [
                new RoutingRule { Match = MatchKind.Domain, Value = "*.mine.com", Mode = RoutingMode.Direct, Source = RuleSource.User },
                new RoutingRule { Match = MatchKind.Domain, Value = "*.chatgpt.com", Mode = RoutingMode.Proxy, Source = RuleSource.Base },
                new RoutingRule { Match = MatchKind.Domain, Value = "*.youtube.com", Mode = RoutingMode.Desync, Source = RuleSource.Base },
                new RoutingRule { Match = MatchKind.Domain, Value = "*.ru", Mode = RoutingMode.Direct, Source = RuleSource.Fallback },
            ],
            RoutingMode.Desync);

        var plan = RouteListInput.Plan("sub.mine.com\nchatgpt.com\nyoutube.com\nya.ru\n7tv.app", engine, [], RoutingMode.Proxy, toSite: false);

        Assert.Equal(["sub.mine.com"], plan.AlreadyOwn);
        Assert.Equal(["chatgpt.com"], plan.SameRoute);
        Assert.Equal(["youtube.com", "ya.ru", "7tv.app"], plan.Sites);
        Assert.Equal(["youtube.com"], plan.Overrides);
    }
}
