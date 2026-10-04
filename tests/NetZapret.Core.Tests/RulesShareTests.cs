using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Маршруты файлом: чтение чужого файла, что изменится, чего нет на машине.
/// </summary>
/// <remarks>
/// Заведено 30.09 вместо книги маршрутов: делимся самим rules.user.yaml.
/// </remarks>
public sealed class RulesShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nz-share-{Guid.NewGuid():N}");

    public RulesShareTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static UserRuleEntry Rule(MatchKind match, string value, RoutingMode mode, string? recipe = null, bool enabled = true) =>
        new() { Match = match, Value = value, Mode = mode, Recipe = recipe, Enabled = enabled };

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>
    /// Правила для программ с файлом не едут, пока обход по программе на
    /// переработке (04.10, Rules.ProgramRulesOff); прочее едет как было.
    /// </summary>
    /// <remarks>
    /// До 04.10 этот тест требовал обратного: книга теряла правила программ,
    /// и их стали возить. Теперь их сняли у всех, и чужой файл не должен
    /// возвращать их в обход.
    /// </remarks>
    [Fact]
    public void A_shared_file_leaves_program_routes_behind()
    {
        var path = Write("routes.yaml", """
            rules:
              - match: process
                value: "Fallout76.exe"
                mode: proxy
              - match: hostlist
                value: "config/lists/discord.txt"
                mode: desync
                recipe: "multisplit"
            """);

        var rules = RulesShare.Read(path);

        Assert.Single(rules);
        Assert.Equal(MatchKind.HostList, rules[0].Match);
        Assert.Equal("multisplit", rules[0].Recipe);
    }

    /// <summary>
    /// Испорченный чужой файл — ошибка, но сам файл не трогается.
    /// </summary>
    /// <remarks>
    /// UserRulesFile.Load отодвигает испорченный файл в .broken — для своего
    /// это верно, а файл, выбранный для загрузки, чужой.
    /// </remarks>
    [Fact]
    public void A_broken_file_is_refused_and_left_where_it_was()
    {
        var path = Write("broken.yaml", "rules:\n  - match: nonsense\n    value: x\n    mode: sideways\n");

        Assert.ThrowsAny<Exception>(() => RulesShare.Read(path));
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + UserRulesFile.BrokenSuffix));
    }

    [Fact]
    public void The_difference_names_what_is_added_removed_and_changed()
    {
        var current = new[]
        {
            Rule(MatchKind.HostList, "config/lists/discord.txt", RoutingMode.Desync),
            Rule(MatchKind.Domain, "*.github.com", RoutingMode.Direct),
            Rule(MatchKind.Domain, "*.tmdb.org", RoutingMode.Proxy),
        };

        var incoming = new[]
        {
            // Путь другой чертой — то же правило.
            Rule(MatchKind.HostList, @"config\lists\discord.txt", RoutingMode.Proxy),
            Rule(MatchKind.Domain, "*.tmdb.org", RoutingMode.Proxy),
            Rule(MatchKind.Process, "Fallout76.exe", RoutingMode.Proxy),
        };

        var diff = RulesShare.Compare(current, incoming);

        Assert.Equal("Fallout76.exe", Assert.Single(diff.Added).Value);
        Assert.Equal("*.github.com", Assert.Single(diff.Removed).Value);

        var change = Assert.Single(diff.Changed);
        Assert.Equal(RoutingMode.Desync, change.Before.Mode);
        Assert.Equal(RoutingMode.Proxy, change.After.Mode);
        Assert.Equal("discord: десинк → VPN", RulesShare.Describe(change.Before, change.After));
    }

    [Fact]
    public void A_changed_recipe_is_a_change_and_is_named()
    {
        var diff = RulesShare.Compare(
            [Rule(MatchKind.Domain, "*.github.com", RoutingMode.Desync, "pass")],
            [Rule(MatchKind.Domain, "*.github.com", RoutingMode.Desync, "multisplit")]);

        var change = Assert.Single(diff.Changed);
        Assert.Contains("pass", RulesShare.Describe(change.Before, change.After));
        Assert.Contains("multisplit", RulesShare.Describe(change.Before, change.After));
    }

    [Fact]
    public void The_same_routes_are_no_difference()
    {
        var rules = new[] { Rule(MatchKind.Domain, "*.github.com", RoutingMode.Direct) };

        Assert.True(RulesShare.Compare(rules, rules).Same);
    }

    /// <summary>
    /// Правило на список, которого здесь нет, названо до замены.
    /// </summary>
    /// <remarks>
    /// Так выходит со своими списками чужой машины: их файлы в маршруты
    /// не входят, и правило не совпадает ни с чем — молча.
    /// </remarks>
    [Fact]
    public void Rules_on_lists_missing_here_are_named()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "config", "lists"));
        File.WriteAllText(Path.Combine(_dir, "config", "lists", "discord.txt"), "discord.com\n");

        var missing = RulesShare.MissingLists(
            [
                Rule(MatchKind.HostList, "config/lists/discord.txt", RoutingMode.Desync),
                Rule(MatchKind.HostList, OwnLists.PathFor("мои-сайты"), RoutingMode.Proxy),
                Rule(MatchKind.Domain, "*.github.com", RoutingMode.Direct),
            ],
            _dir);

        Assert.Equal("мои-сайты", RulesShare.NameOf(Assert.Single(missing)));
        Assert.Equal(1, RulesShare.OnOwnLists([Rule(MatchKind.HostList, OwnLists.PathFor("x"), RoutingMode.Proxy)]));
    }

    /// <summary>Копия прежних маршрутов — в runtime, не в config: тот не весь в .gitignore.</summary>
    [Fact]
    public void The_backup_goes_to_runtime_not_config()
    {
        var rules = Write("rules.user.yaml", "rules: []\n");
        var runtime = Path.Combine(_dir, "runtime");

        var copy = RulesShare.Backup(rules, runtime);

        Assert.NotNull(copy);
        Assert.StartsWith(runtime, copy);
        Assert.True(File.Exists(copy));
        Assert.Null(RulesShare.Backup(Path.Combine(_dir, "нет-такого.yaml"), runtime));
    }

    /// <summary>
    /// Противоречие собирается прямо из правил и пинов — книга для этого была лишней.
    /// </summary>
    [Fact]
    public void Clashes_are_found_from_rules_and_pins()
    {
        var clashes = RouteClashes.FromRules(
            [
                Rule(MatchKind.Domain, "*.instagram.com", RoutingMode.Desync),
                Rule(MatchKind.Domain, "*.tmdb.org", RoutingMode.Direct),

                // Выключенное не решает ничего и не спорит.
                Rule(MatchKind.Domain, "*.claude.ai", RoutingMode.Proxy, enabled: false),

                // Программа — не имя.
                Rule(MatchKind.Process, "Fallout76.exe", RoutingMode.Proxy),
            ],
            ["instagram.com", "tmdb.org", "claude.ai"]);

        var clash = Assert.Single(clashes);
        Assert.Equal("instagram.com", clash.Name);
        Assert.Contains("десинк", clash.Outcome);
    }
}
