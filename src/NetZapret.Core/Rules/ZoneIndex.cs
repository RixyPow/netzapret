namespace NetZapret.Core.Rules;

/// <summary>
/// Первое по порядку правило, чья зона покрывает имя, — по хвостам имени, а не перебором.
/// </summary>
/// <remarks>
/// <para>
/// Зона покрывает имя, если совпадает с ним или с его хвостом после точки:
/// <c>openai.com</c> покрывает <c>api.openai.com</c>. Прежде щит
/// (<c>HostsFile</c>) и споры правил (<see cref="RouteConflicts"/>) искали
/// такое правило, перебирая подряд все зоны всех правил для каждого имени.
/// На сотне правил это незаметно. С маршрутом «Реестр блокировок РФ»
/// (117 тысяч имён, 15.10) — миллиарды сравнений на каждый запуск движков.
/// </para>
/// <para>
/// Здесь каждая зона помнит первое правило, где она встретилась; имя
/// проверяется само и по своим хвостам, и берётся наименьший номер. Ответ
/// тот же, что у перебора: первое правило, покрывающее имя.
/// </para>
/// </remarks>
public sealed class ZoneIndex
{
    private readonly Dictionary<string, int> _first = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="zonesByRule">Зоны каждого правила — в порядке вычисления.</param>
    public ZoneIndex(IEnumerable<IEnumerable<string>> zonesByRule)
    {
        int rule = 0;

        foreach (var zones in zonesByRule)
        {
            foreach (var entry in zones)
            {
                var zone = entry.TrimStart('*', '.');

                if (zone.Length > 0)
                    _first.TryAdd(zone, rule);
            }

            rule++;
        }
    }

    /// <summary>Номер первого правила, покрывающего имя; <c>-1</c> — ни одного.</summary>
    public int First(string name)
    {
        int best = Find(name);

        for (int dot = name.IndexOf('.'); dot >= 0; dot = name.IndexOf('.', dot + 1))
        {
            int at = Find(name[(dot + 1)..]);

            if (at >= 0 && (best < 0 || at < best))
                best = at;
        }

        return best;
    }

    private int Find(string zone) => _first.TryGetValue(zone, out var at) ? at : -1;
}
