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

    /// <summary>
    /// Слои из файлов: заводской и свой (как у владельца — rules.yaml и rules.user.yaml).
    /// </summary>
    private RuleEngine LoadLayers(string baseYaml, Action<UserRulesFile> user)
    {
        var basePath = Path.Combine(_root, "config", "rules.yaml");
        var userPath = Path.Combine(_root, "config", "rules.user.yaml");
        File.WriteAllText(basePath, baseYaml);

        var file = UserRulesFile.Load(userPath);
        user(file);
        file.Save();

        var engine = RuleSetLoader.LoadLayered(basePath, userPath);
        RuleSetExpander.Expand(engine.RuleSet, _root);

        return engine;
    }

    private static Connections.ConnectionEvent To(string host) => Connections.ConnectionEvent.Describe(host);

    /// <summary>
    /// Узкая часть, записанная в файл после широкой, всё равно решает свои имена
    /// (14.10: «Обновления Discord → напрямую» стояло после «Сайт и переписка → десинк»
    /// и было мертво — след слияния и раздела частей Discord).
    /// </summary>
    [Fact]
    public void ANarrowPartBeatsItsWideListEvenWrittenBelowIt()
    {
        Write("discord.txt", "discord.com", "discordapp.com");
        Write("discord-updates.txt", "updates.discord.com");

        var engine = LoadLayers("rules: []\ndefault:\n  mode: desync\n", file =>
        {
            file.Set(MatchKind.HostList, "config/lists/discord.txt", RoutingMode.Desync);
            file.Set(MatchKind.HostList, "config/lists/discord-updates.txt", RoutingMode.Direct);
        });

        Assert.Equal(RoutingMode.Direct, engine.Evaluate(To("updates.discord.com")).Mode);
        Assert.Equal(RoutingMode.Desync, engine.Evaluate(To("discord.com")).Mode);
        Assert.Empty(RouteConflicts.Find(engine.RuleSet));
    }

    /// <summary>
    /// Свой путь сервиса поверх заводского правила на тот же список — выбор, а не спор
    /// (14.10: github.txt «через VPN» против заводского «десинк, pass»).
    /// </summary>
    [Fact]
    public void AUserChoiceOverTheSameBaseListIsNotAConflict()
    {
        Write("github.txt", "github.com", "githubusercontent.com");
        Write("chatgpt.txt", "chatgpt.com");

        var engine = LoadLayers("""
            rules:
              - match: hostlist
                value: "config/lists/github.txt"
                mode: desync
              - match: domain
                value: "*.chatgpt.com"
                mode: proxy
            default:
              mode: desync
            """, file =>
        {
            file.Set(MatchKind.HostList, "config/lists/github.txt", RoutingMode.Proxy);
            file.Set(MatchKind.HostList, "config/lists/chatgpt.txt", RoutingMode.Direct);
        });

        var conflicts = RouteConflicts.Find(engine.RuleSet);

        // github.txt против github.txt — выбор; chatgpt.txt против заводского
        // домена — тот самый спор, ради которого раздел заведён.
        var conflict = Assert.Single(conflicts);
        Assert.Equal("config/lists/chatgpt.txt", conflict.Winner.Value);
        Assert.Equal("*.chatgpt.com", conflict.Loser.Value);
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
