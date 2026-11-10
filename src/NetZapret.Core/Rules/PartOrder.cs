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

    /// <summary>Место части в каталоге — порядок узких частей одной широкой.</summary>
    private static readonly Dictionary<string, int> CatalogPlace = ServiceCatalog.All
        .SelectMany(s => s.Parts)
        .Select((p, i) => (p.List, i))
        .GroupBy(x => x.List, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First().i, StringComparer.OrdinalIgnoreCase);

    /// <summary>Узкие части внутри соседки: список, список соседки, широкий список.</summary>
    private static readonly (string List, string Sibling, string Within)[] Nested = ServiceCatalog.All
        .SelectMany(s => s.Parts)
        .Where(p => p.InsideOf is not null && p.Within is not null)
        .Select(p => (p.List, p.InsideOf!, p.Within!))
        .ToArray();

    /// <summary>Тот же список, где каждая узкая часть стоит раньше своей широкой.</summary>
    /// <param name="defaultMode">Умолчание движка — путь наследника, когда у широкой части правила нет.</param>
    public static List<RoutingRule> Apply(List<RoutingRule> rules, RoutingMode defaultMode)
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

        // Узкие части одной широкой — в порядке каталога. Они бывают вложены
        // друг в друга: updates.discord.com («Обновления») лежит внутри
        // discord.com («Квесты и API», 05.11). Без этого «API через VPN» и
        // «Обновления напрямую» решались бы очерёдностью строк в файле.
        // Непересекающимся частям их взаимный порядок безразличен.
        foreach (var siblings in result
            .Select((rule, at) => (rule, at))
            .Where(x => x.rule.Match == MatchKind.HostList && WithinOf.ContainsKey(x.rule.Value))
            .GroupBy(x => (Within: WithinOf[x.rule.Value].ToLowerInvariant(), x.rule.Source))
            .ToList())
        {
            var places = siblings.Select(x => x.at).ToList();
            var ordered = siblings.Select(x => x.rule).OrderBy(r => CatalogPlace[r.Value]).ToList();

            for (int k = 0; k < places.Count; k++)
                result[places[k]] = ordered[k];
        }

        Inherit(result, defaultMode);

        return result;
    }

    /// <summary>
    /// Узкая часть внутри соседки идёт путём своей широкой, а не соседки.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядок выше решает, только когда у обеих узких частей есть правила.
    /// Без своего правила имена «Обновлений» (updates.discord.com) забирала
    /// зона discord.com «Квестов и API»: 10.11 у владельца API шли через VPN,
    /// «Сайт и переписка» — десинком, и «по умолчанию» у «Обновлений» значило
    /// VPN. Части же каталога решают каждая за свои имена: путь API — выбор
    /// для запросов к API, а не для обновлений клиента.
    /// </para>
    /// <para>
    /// Наследник встаёт прямо перед правилом соседки и берёт путь правила
    /// широкого списка (а нет его — умолчание движка). Своё правило узкой
    /// части стоит раньше соседки (порядок каталога выше) и решает само;
    /// наследник за ним ничего не меняет. Он ставится и тогда — чтобы
    /// «Маршруты», пересобирая движок без своего правила части
    /// (ServiceRouting.Without), нашли его уже со списком: списки читаются
    /// после сборки, и новый наследник был бы пуст.
    /// </para>
    /// </remarks>
    private static void Inherit(List<RoutingRule> result, RoutingMode defaultMode)
    {
        // Наследники прошлой сборки — со списками, уже прочитанными для них.
        var earlier = new Dictionary<string, RoutingRule>(StringComparer.OrdinalIgnoreCase);

        for (int i = result.Count - 1; i >= 0; i--)
        {
            if (!result[i].Inherited)
                continue;

            earlier.TryAdd(result[i].Value, result[i]);
            result.RemoveAt(i);
        }

        foreach (var (list, sibling, within) in Nested)
        {
            int at = result.FindIndex(r => r.Match == MatchKind.HostList && Same(r.Value, sibling));

            if (at < 0)
                continue;

            var wide = result.Find(r => r.Match == MatchKind.HostList && Same(r.Value, within));

            var heir = new RoutingRule
            {
                Match = MatchKind.HostList,
                Value = list,
                Mode = wide?.Mode ?? defaultMode,
                Server = wide?.Server,
                Recipe = wide?.Recipe,
                Source = RuleSource.Base,
                Inherited = true,
                Ordinal = result[at].Ordinal,
            };

            heir.Compile();

            if (earlier.TryGetValue(list, out var before) && before.HostListDomains.Count > 0)
            {
                heir.LoadHostList(before.HostListDomains);
                heir.LoadHostListAddresses(before.HostListCidrs);
            }

            result.Insert(at, heir);
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
