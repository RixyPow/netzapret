namespace NetZapret.Core.Rules;

/// <summary>
/// Имена своих рецептов десинка: каждое — только у правила, которое выигрывает его первым.
/// </summary>
/// <remarks>
/// <para>
/// Профили winws2 собираются по рецепту, и порядок профилей — порядок первого
/// появления рецепта, а не порядок правил. Пока имя клалось в список каждого
/// правила, которое его покрывает, решал winws2 — первый совпавший профиль, —
/// а не движок правил. 27.10 seriesgraph.com лежал и в «pass» (список
/// seriesgraph.txt), и в «seqovl-4pda» (правило <c>*.seriesgraph.com</c>,
/// записанное подбором). Движок отдавал имя второму — своё имя раньше списка
/// сервиса, — а профиль «pass» стоял первым, потому что его рецепт раньше
/// встретился у <c>*.outlook.live.com</c>. Подобранный рецепт не применялся,
/// и сайт после перезапуска замерзал по-прежнему.
/// </para>
/// <para>
/// Теперь имя остаётся только у правила, первым покрывающего его в порядке
/// вычисления (<see cref="ZoneIndex"/> по всем правилам набора, в том числе
/// «напрямую» и «через VPN»): тогда порядок профилей больше ничего не решает.
/// </para>
/// </remarks>
public static class OwnRecipeNames
{
    /// <summary>Правила «десинк» со своим рецептом и имена, которые достаются именно им.</summary>
    /// <param name="ruleSet">Набор в порядке вычисления — как его собирает <see cref="RuleEngine.Build"/>.</param>
    public static IReadOnlyList<(RoutingRule Rule, IReadOnlyList<string> Names)> Winning(RuleSet ruleSet)
    {
        var rules = ruleSet.Rules;
        var index = new ZoneIndex(rules.Select(Zones));
        var result = new List<(RoutingRule, IReadOnlyList<string>)>();

        for (int at = 0; at < rules.Count; at++)
        {
            var rule = rules[at];

            if (rule.Mode != RoutingMode.Desync || rule.Match is not (MatchKind.Domain or MatchKind.HostList)
                || string.IsNullOrWhiteSpace(rule.Recipe))
            {
                continue;
            }

            var names = Zones(rule)
                .Select(zone => zone.StartsWith("*.", StringComparison.Ordinal) ? zone[2..] : zone)
                .Where(name => name.Contains('*') || index.First(name) is var first && (first < 0 || first == at))
                .ToList();

            result.Add((rule, names));
        }

        return result;
    }

    private static IEnumerable<string> Zones(RoutingRule rule) => rule.Match switch
    {
        MatchKind.Domain => [rule.Value],
        MatchKind.HostList => rule.HostListDomains,
        _ => [],
    };
}
