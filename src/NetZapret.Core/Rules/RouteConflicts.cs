namespace NetZapret.Core.Rules;

/// <summary>Одно правило перекрывает другое, и они расходятся в маршруте.</summary>
public sealed record RouteConflict
{
    /// <summary>Правило, которое срабатывает.</summary>
    public required RoutingRule Winner { get; init; }

    /// <summary>Правило, до которого дело не доходит.</summary>
    public required RoutingRule Loser { get; init; }

    /// <summary>Любое из спорных имён — чтобы было видно, о чём речь.</summary>
    public required string Example { get; init; }

    /// <summary>Сколько имён проигравшего перекрыто.</summary>
    public required int Names { get; init; }

    /// <summary>Перекрыто целиком — проигравшее правило не действует вовсе.</summary>
    public required bool Entirely { get; init; }
}

/// <summary>
/// Ищет правила, спорящие об одном и том же имени.
/// </summary>
/// <remarks>
/// <para>
/// Молчаливая поломка, и найдена она на живом случае. В базовом наборе стоят
/// четыре правила — <c>*.chatgpt.com</c>, <c>*.openai.com</c>,
/// <c>*.oaistatic.com</c>, <c>*.oaiusercontent.com</c>, — все в туннель.
/// А в пользовательском слое лежит правило на список <c>chatgpt.txt</c>,
/// в котором те же четыре имени, и оно ведёт напрямую. Пользовательский слой
/// проверяется раньше, поэтому четыре заводских правила мертвы, и узнать
/// об этом было неоткуда: в файле они есть, в меню значатся, а не работают.
/// </para>
/// <para>
/// Спором считается не всякое перекрытие, а только расхождение в маршруте.
/// Правило на зону и правило на её поддомен с одинаковым режимом друг другу
/// не мешают — второе просто лишнее, и говорить о нём как о поломке значило бы
/// приучить пропускать этот раздел.
/// </para>
/// <para>
/// Сравниваются только правила по именам: домен с доменом, домен со списком,
/// список со списком. Правило по адресу с правилом по имени не спорит —
/// они отвечают на разные вопросы и срабатывают при разных сведениях
/// о соединении.
/// </para>
/// </remarks>
public static class RouteConflicts
{
    public static IReadOnlyList<RouteConflict> Find(RuleSet ruleSet)
    {
        // Порядок здесь тот же, в котором правила и вычисляются: RuleEngine.Build
        // уже разложил их по приоритету класса и по слою. Полагаться на него
        // обязательно — спор решается именно очерёдностью, и своя сортировка
        // отвечала бы на вопрос про другой движок.
        var byName = ruleSet.Rules
            .Where(r => r.Enabled && r.Match is MatchKind.Domain or MatchKind.HostList)
            .Select(rule => (Rule: rule, Zones: Zones(rule).ToList()))
            .Where(pair => pair.Zones.Count > 0)
            .ToList();

        var found = new List<RouteConflict>();

        for (int loser = 1; loser < byName.Count; loser++)
        {
            var shadowed = new List<(string Name, RoutingRule By)>();

            foreach (var zone in byName[loser].Zones)
            {
                for (int winner = 0; winner < loser; winner++)
                {
                    if (!byName[winner].Zones.Any(w => Covers(w, zone)))
                        continue;

                    // Первое совпавшее и решает: дальше по списку до этого
                    // имени дело уже не дойдёт, и правило, стоящее третьим,
                    // проигрывает не нам, а второму.
                    if (byName[winner].Rule.Mode != byName[loser].Rule.Mode)
                        shadowed.Add((zone, byName[winner].Rule));

                    break;
                }
            }

            foreach (var group in shadowed.GroupBy(s => s.By))
            {
                found.Add(new RouteConflict
                {
                    Winner = group.Key,
                    Loser = byName[loser].Rule,
                    Example = group.First().Name,
                    Names = group.Count(),
                    Entirely = shadowed.Count == byName[loser].Zones.Count,
                });
            }
        }

        return found;
    }

    /// <summary>Зоны, на которые правило распространяется.</summary>
    /// <remarks>
    /// Звёздочка и точка снимаются: <c>*.chatgpt.com</c> и <c>chatgpt.com</c> —
    /// одна и та же зона, и сравнивать их как разные строки значило бы
    /// не найти самый частый вид спора.
    /// </remarks>
    private static IEnumerable<string> Zones(RoutingRule rule) => rule.Match switch
    {
        MatchKind.Domain => [rule.Value.TrimStart('*', '.')],
        MatchKind.HostList => rule.HostListDomains.Select(d => d.TrimStart('*', '.')),
        _ => [],
    };

    /// <summary>Покрывает ли зона имя — сама себя или как суффикс.</summary>
    private static bool Covers(string zone, string name) =>
        name.Equals(zone, StringComparison.OrdinalIgnoreCase)
        || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);
}
