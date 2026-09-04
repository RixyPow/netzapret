using NetZapret.Core.Rules;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Правила, спорящие об одном имени.
/// </summary>
/// <remarks>
/// Поломка молчаливая: перекрытое правило есть в файле, значится в меню
/// и не работает. Найдена на живом случае — четыре заводских правила
/// на ChatGPT не действовали, перекрытые одной строкой своего слоя.
/// </remarks>
public sealed class RouteConflictTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-conflict-{Guid.NewGuid():N}");

    public RouteConflictTests() =>
        Directory.CreateDirectory(Path.Combine(_root, "config", "lists"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_root, "config", "lists", name), lines);

    private RuleEngine Load(string yaml)
    {
        var engine = RuleSetLoader.Load(yaml);
        RuleSetExpander.Expand(engine.RuleSet, _root);

        return engine;
    }

    [Fact]
    public void AListRuleShadowsTheDomainRulesItCovers()
    {
        // Тот самый случай. Список ведёт напрямую, доменные правила — в туннель,
        // и до них дело не доходит.
        Write("chatgpt.txt", "chatgpt.com", "openai.com");

        var engine = Load("""
            rules:
              - match: hostlist
                value: "config/lists/chatgpt.txt"
                mode: direct
              - match: domain
                value: "*.chatgpt.com"
                mode: proxy
              - match: domain
                value: "*.openai.com"
                mode: proxy
            default:
              mode: desync
            """);

        var conflicts = RouteConflicts.Find(engine.RuleSet);

        Assert.Equal(2, conflicts.Count);
        Assert.All(conflicts, c => Assert.Equal("config/lists/chatgpt.txt", c.Winner.Value));
        Assert.All(conflicts, c => Assert.True(c.Entirely));
        Assert.Contains(conflicts, c => c.Loser.Value == "*.chatgpt.com");
        Assert.Contains(conflicts, c => c.Loser.Value == "*.openai.com");
    }

    [Fact]
    public void TheSameModeIsNotAConflict()
    {
        // Перекрытие с тем же маршрутом — лишняя строка, а не поломка.
        // Называть её поломкой значит приучить пропускать весь раздел.
        var engine = Load("""
            rules:
              - match: domain
                value: "*.example.com"
                mode: proxy
              - match: domain
                value: "cdn.example.com"
                mode: proxy
            default:
              mode: desync
            """);

        Assert.Empty(RouteConflicts.Find(engine.RuleSet));
    }

    [Fact]
    public void ASubdomainRuleAfterAZoneRuleIsReported()
    {
        // Порядок решает всё: широкое правило, записанное раньше, забирает
        // и поддомен, ради которого написано второе.
        var engine = Load("""
            rules:
              - match: domain
                value: "*.example.com"
                mode: proxy
              - match: domain
                value: "cdn.example.com"
                mode: direct
            default:
              mode: desync
            """);

        var conflict = Assert.Single(RouteConflicts.Find(engine.RuleSet));

        Assert.Equal("*.example.com", conflict.Winner.Value);
        Assert.Equal("cdn.example.com", conflict.Loser.Value);
        Assert.Equal("cdn.example.com", conflict.Example);
    }

    [Fact]
    public void AddressRulesDoNotArgueWithNameRules()
    {
        // Они отвечают на разные вопросы и срабатывают при разных сведениях
        // о соединении: одно знает имя, другое — адрес.
        File.WriteAllLines(Path.Combine(_root, "config", "lists", "сети.txt"), ["203.0.113.0/24"]);

        var engine = Load("""
            rules:
              - match: domain
                value: "*.example.com"
                mode: proxy
              - match: ipset
                value: "config/lists/сети.txt"
                mode: direct
            default:
              mode: desync
            """);

        Assert.Empty(RouteConflicts.Find(engine.RuleSet));
    }

    [Fact]
    public void OnlyPartOfAListCanBeShadowed()
    {
        // Список из трёх имён, перекрыто одно. Говорить «перекрыто целиком»
        // здесь значило бы предложить выбросить правило, которое на две
        // трети работает.
        Write("свой.txt", "one.example", "two.example", "three.example");

        var engine = Load("""
            rules:
              - match: domain
                value: "two.example"
                mode: proxy
              - match: hostlist
                value: "config/lists/свой.txt"
                mode: direct
            default:
              mode: desync
            """);

        var conflict = Assert.Single(RouteConflicts.Find(engine.RuleSet));

        Assert.False(conflict.Entirely);
        Assert.Equal(1, conflict.Names);
        Assert.Equal("two.example", conflict.Example);
    }

    [Fact]
    public void ADisabledRuleArguesWithNobody()
    {
        var engine = Load("""
            rules:
              - match: domain
                value: "*.example.com"
                mode: proxy
              - match: domain
                value: "cdn.example.com"
                mode: direct
                enabled: false
            default:
              mode: desync
            """);

        Assert.Empty(RouteConflicts.Find(engine.RuleSet));
    }

    [Fact]
    public void TheShippedRulesDoNotArgueWithThemselves()
    {
        // Базовый набор — наша работа целиком, и спорить внутри себя ему
        // незачем. Тест сторожит именно это: спор со своим слоем — дело
        // человека, а спор заводского с заводским — наша опечатка.
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "rules.yaml");

        if (!File.Exists(path))
            return;

        var engine = RuleSetLoader.LoadFromFile(path);

        Assert.Empty(RouteConflicts.Find(engine.RuleSet));
    }
}
