using System.Net;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Маршрут программы не перебивает решений человека (07.11, вопрос в чате:
/// Chrome «через VPN» выучил vk.com, и «vk.com → напрямую» не помогало).
/// </summary>
public sealed class ProgramRouteDecisionTests : IDisposable
{
    private const string ChromeDomains = "config/lists/own/programs/chrome.exe.domains.txt";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-prog-{Guid.NewGuid():N}");

    public ProgramRouteDecisionTests() => Directory.CreateDirectory(Path.Combine(_root, "config", "lists"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string Settings => Path.Combine(_root, "config", "netzapret.json");

    private string Rules => Path.Combine(_root, "config", "rules.user.yaml");

    private static RoutingRule ProgramList(params string[] names)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = ChromeDomains, Mode = RoutingMode.Proxy, Source = RuleSource.User };
        rule.LoadHostList(names);
        return rule;
    }

    private static RoutingRule OwnName(string name, RoutingMode mode) =>
        new() { Match = MatchKind.Domain, Value = name, Mode = mode, Source = RuleSource.User };

    private static RoutingRule CatalogList(string list, RoutingMode mode, RuleSource source, params string[] names)
    {
        var rule = new RoutingRule { Match = MatchKind.HostList, Value = list, Mode = mode, Source = source };
        rule.LoadHostList(names);
        return rule;
    }

    [Fact]
    public void Program_list_comes_after_every_choice_of_the_person_and_before_factory_rules()
    {
        int ownName = RuleEngine.Tier(MatchKind.Domain, RuleSource.User);
        int ownList = RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/own/site.txt");
        int setting = RuleEngine.Tier(MatchKind.Domain, RuleSource.Setting);
        int catalogChoice = RuleEngine.Tier(MatchKind.HostList, RuleSource.User, "config/lists/vk.txt");
        int program = RuleEngine.Tier(MatchKind.HostList, RuleSource.User, ChromeDomains);
        int factory = RuleEngine.Tier(MatchKind.HostList, RuleSource.Base, "config/lists/vk.txt");

        Assert.Equal(ownName, ownList);
        Assert.True(setting < program);
        Assert.True(catalogChoice < program);
        Assert.True(program < factory);
    }

    [Fact]
    public void Own_rule_wins_over_a_learned_name_whatever_the_file_order()
    {
        // Список программы в файле первым — так его ставит ProgramRoutes.Add.
        var engine = RuleEngine.Build([ProgramList("vk.com"), OwnName("vk.com", RoutingMode.Direct)], RoutingMode.Desync);

        var decision = engine.Evaluate(ConnectionEvent.Describe("vk.com"));

        Assert.Equal(RoutingMode.Direct, decision.Mode);
        Assert.Equal(MatchKind.Domain, decision.Rule!.Match);
    }

    [Fact]
    public void Catalog_choice_of_the_person_wins_over_a_learned_name()
    {
        var engine = RuleEngine.Build(
            [ProgramList("vk.com"), CatalogList("config/lists/vk.txt", RoutingMode.Direct, RuleSource.User, "vk.com")],
            RoutingMode.Desync);

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(ConnectionEvent.Describe("vk.com")).Mode);
    }

    [Fact]
    public void Learned_name_still_wins_over_a_factory_rule()
    {
        var engine = RuleEngine.Build(
            [CatalogList("config/lists/vk.txt", RoutingMode.Desync, RuleSource.Base, "vk.com"), ProgramList("vk.com")],
            RoutingMode.Desync);

        Assert.Equal(RoutingMode.Proxy, engine.Evaluate(ConnectionEvent.Describe("vk.com")).Mode);
    }

    private ProgramRouteLearner Learner()
    {
        Assert.True(ProgramRoutes.Add("chrome.exe", Settings, Rules, _root));
        return new ProgramRouteLearner(Settings, _root);
    }

    private static ConnectionEvent Chrome(string host, string address = "87.240.132.72") => new()
    {
        Timestamp = DateTimeOffset.Now,
        Protocol = ProtocolKind.Tcp,
        RemoteAddress = IPAddress.Parse(address),
        RemotePort = 443,
        ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        Hostname = host,
    };

    private static RuleDecision By(RoutingRule rule) => new() { Mode = rule.Mode, Rule = rule, Reason = "тест" };

    [Fact]
    public void Name_the_person_decided_about_is_not_learned()
    {
        var learner = Learner();

        Assert.Null(learner.Observe(Chrome("vk.com"), By(OwnName("vk.com", RoutingMode.Direct))));
        Assert.Null(learner.Observe(Chrome("ok.ru"), By(new RoutingRule
        {
            Match = MatchKind.Domain, Value = "*.ru", Mode = RoutingMode.Direct, Source = RuleSource.Fallback,
        })));

        Assert.Empty(ProgramRoutes.Entries(ProgramRoutes.DomainsPath("chrome.exe"), _root));
    }

    [Fact]
    public void Name_under_a_factory_rule_or_no_rule_is_learned()
    {
        var learner = Learner();

        Assert.NotNull(learner.Observe(Chrome("rutracker.org"),
            By(CatalogList("config/lists/rutracker.txt", RoutingMode.Desync, RuleSource.Base, "rutracker.org"))));
        Assert.NotNull(learner.Observe(Chrome("example.org")));

        Assert.Equal(["rutracker.org", "example.org"], ProgramRoutes.Entries(ProgramRoutes.DomainsPath("chrome.exe"), _root));
    }

    [Fact]
    public void Learned_names_with_a_decision_of_the_person_are_pruned_and_the_rest_stays()
    {
        Assert.True(ProgramRoutes.Add("chrome.exe", Settings, Rules, _root));

        var list = Path.Combine(_root, ProgramRoutes.DomainsPath("chrome.exe"));
        File.AppendAllText(list, "vk.com\r\nexample.org\r\n");

        var engine = RuleEngine.Build([ProgramList("vk.com", "example.org"), OwnName("vk.com", RoutingMode.Direct)], RoutingMode.Desync);

        var removed = ProgramRoutes.Prune(engine, Settings, _root);

        Assert.Equal([("chrome.exe", "vk.com")], removed);
        Assert.Equal(["example.org"], ProgramRoutes.Entries(ProgramRoutes.DomainsPath("chrome.exe"), _root));
        Assert.StartsWith("# Маршрут программы chrome.exe", File.ReadAllText(list));
    }
}
