using NetZapret.Core.Services;

namespace NetZapret.Core.Rules;

/// <summary>
/// Узкая часть сервиса проверяется раньше своей широкой (<see cref="ServicePart.Within"/>).
/// </summary>
/// <remarks>
/// <para>
/// Окно и так пишет правило узкой части перед широкой (<see cref="UserRulesFile.Set"/>
/// с <c>before</c>), но только новое: существующую строку <c>Set</c> правит
/// на её месте. 14.10 у владельца «Обновления → напрямую» (discord-updates.txt)
/// стояло в файле после «Сайт и переписка → десинк» (discord.txt) — след того,
/// что Discord сливали в одну часть (11.09) и делили снова (29.09), — и было
/// мертво: updates.discord.com решал широкий список. Найдено карточкой
/// «Правила спорят» в проверке блокировок.
/// </para>
/// <para>
/// Порядок наводится при сборке движка, а не в файле: так он верен для любого
/// файла — старого, принесённого «Загрузить маршруты» или правленного руками.
/// Только в одном слое: заводская узкая часть не обгоняет выбор человека
/// для всего сервиса, и наоборот — между слоями решает <see cref="RuleEngine.Tier"/>.
/// Узкое перед широким безопасно всегда: при одинаковом режиме порядок
/// ничего не решает, при разном узкое за широким не работало бы вовсе.
/// </para>
/// </remarks>
public static class PartOrder
{
    private static readonly Dictionary<string, string> WithinOf = ServiceCatalog.All
        .SelectMany(s => s.Parts)
        .Where(p => p.Within is not null)
        .GroupBy(p => p.List, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().Within!, StringComparer.OrdinalIgnoreCase);

    /// <summary>Тот же список, где каждая узкая часть стоит раньше своей широкой.</summary>
    public static List<RoutingRule> Apply(List<RoutingRule> rules)
    {
        var result = new List<RoutingRule>(rules);

        for (int i = 0; i < result.Count; i++)
        {
            var narrow = result[i];

            if (narrow.Match != MatchKind.HostList || !WithinOf.TryGetValue(narrow.Value, out var within))
                continue;

            int wide = result.FindIndex(r => r.Match == MatchKind.HostList
                && r.Source == narrow.Source
                && string.Equals(r.Value, within, StringComparison.OrdinalIgnoreCase));

            if (wide < 0 || wide > i)
                continue;

            result.RemoveAt(i);
            result.Insert(wide, narrow);
        }

        return result;
    }
}
