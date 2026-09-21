namespace NetZapret.Core.Rules;

/// <summary>
/// Ищет имена, названные дважды и по-разному.
/// </summary>
/// <remarks>
/// <para>
/// Ради этого книга маршрутов и заводилась. Не удобство: молчаливая
/// поломка становится громкой.
/// </para>
/// <para>
/// 21.09 у владельца Instagram был прибит в <c>hosts</c> и потому выведен
/// из-под десинка целиком — <c>multidisorder</c> к нему не применялся
/// ни разу, браузер шёл на прибитый адрес голым, и рукопожатие убивалось.
/// Проверка рецептов при этом показывала, что рецепт работает: она
/// поднимает свой winws2 без списка исключений. Разбор занял час.
/// </para>
/// <para>
/// Ни один из пяти источников правил не мог этого сказать — они не сведены,
/// и порядок между ними лежит в коде. Сведённые в книгу, те же сведения
/// дают ответ при чтении файла.
/// </para>
/// </remarks>
public static class RouteClashes
{
    /// <param name="members">
    /// Чем наполнена группа: имя группы → домены. Без него ловятся только
    /// точные повторы; с ним — ещё и домен, спорящий со своей группой.
    /// </param>
    public static IReadOnlyList<RouteClash> Find(
        IEnumerable<RouteEntry> entries,
        Func<string, IReadOnlyList<string>>? members = null)
    {
        var all = entries.ToList();
        var found = new List<RouteClash>();
        var seen = new Dictionary<string, RouteEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in all)
        {
            if (!seen.TryGetValue(entry.Name, out var first))
            {
                seen[entry.Name] = entry;
                continue;
            }

            // Повтор с тем же маршрутом — не противоречие, а лишняя строка.
            // Говорить о ней незачем: она ничего не меняет и ничего
            // не ломает, а список жалоб размывает.
            if (first.Choice == entry.Choice)
                continue;

            found.Add(new RouteClash
            {
                Name = entry.Name,
                First = first.Choice,
                Second = entry.Choice,
                Outcome = Outcome(first.Choice, entry.Choice),
            });
        }

        if (members is not null)
            found.AddRange(Inherited(all, members));

        return found;
    }

    /// <summary>
    /// Домен, спорящий со своей же группой.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Случай на стыке и самый частый из настоящих: <c>discord: desync</c>
    /// рядом с <c>discord.com: vpn</c>. Точного повтора здесь нет — имена
    /// разные, — а спорят они об одном и том же соединении.
    /// </para>
    /// <para>
    /// Само по себе это не беда: отдельная строка на домен затем и нужна,
    /// чтобы вывести его из общего правила группы, и побеждает она —
    /// точное имя точнее группы. Беда лишь там, где маршруты несовместимы
    /// по существу, а не просто разные: пин с туннелем, пин с десинком.
    /// </para>
    /// </remarks>
    private static IEnumerable<RouteClash> Inherited(
        IReadOnlyList<RouteEntry> entries,
        Func<string, IReadOnlyList<string>> members)
    {
        var byDomain = entries
            .Where(e => !e.IsGroup)
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        if (byDomain.Count == 0)
            yield break;

        foreach (var group in entries.Where(e => e.IsGroup))
        {
            foreach (var domain in members(group.Name))
            {
                if (!byDomain.TryGetValue(domain, out var own) || own.Choice == group.Choice)
                    continue;

                // Только несовместимые. Разные, но совместимые маршруты —
                // это уточнение, а не спор: точное имя перекрывает группу,
                // и так и задумано.
                if (!Incompatible(group.Choice, own.Choice))
                    continue;

                yield return new RouteClash
                {
                    Name = domain,
                    First = group.Choice,
                    Second = own.Choice,
                    Outcome = $"в группе «{group.Name}» — {RouteBookFile.NameOf(group.Choice)}, "
                        + $"а отдельной строкой — {RouteBookFile.NameOf(own.Choice)}: "
                        + Outcome(group.Choice, own.Choice),
                };
            }
        }
    }

    /// <summary>
    /// Несовместимы ли маршруты по существу.
    /// </summary>
    /// <remarks>
    /// Пин несовместим со всем, что требует видеть имя: он задаёт адрес,
    /// и после него ни туннель, ни десинк до имени не доберутся. Прочие
    /// пары просто разные — одна перекрывает другую, и это законно.
    /// </remarks>
    public static bool Incompatible(RouteChoice one, RouteChoice other) =>
        Pair(one, other, RouteChoice.Pin, RouteChoice.Vpn)
        || Pair(one, other, RouteChoice.Pin, RouteChoice.Desync);

    /// <summary>
    /// Чем противоречие кончится на деле.
    /// </summary>
    /// <remarks>
    /// Словами, а не «конфликт»: человеку нужно знать не то, что мы
    /// растерялись, а что с его именем произойдёт. Пары названы по тому,
    /// как они ломаются, и каждая проверена на живом случае.
    /// </remarks>
    public static string Outcome(RouteChoice one, RouteChoice other)
    {
        if (Pair(one, other, RouteChoice.Pin, RouteChoice.Vpn))
        {
            return "прибитое имя не получает fakeip, и в туннель движок его "
                + "не заводит — правило «через VPN» не сделает ничего";
        }

        if (Pair(one, other, RouteChoice.Pin, RouteChoice.Desync))
        {
            return "прибитое имя выводится из-под десинка: пин задаёт адрес, "
                + "а десинк судит по имени — рецепт не применится ни разу";
        }

        if (Pair(one, other, RouteChoice.Vpn, RouteChoice.Desync))
        {
            return "имя уйдёт в туннель, и десинк его не увидит: внутрь туннеля "
                + "фильтр не заглядывает";
        }

        if (Pair(one, other, RouteChoice.Direct, RouteChoice.Desync))
        {
            return "«напрямую» выводит имя из-под десинка — рецепт не применится";
        }

        if (Pair(one, other, RouteChoice.Direct, RouteChoice.Vpn))
        {
            return "«напрямую» и «через VPN» взаимно исключают друг друга — "
                + "сработает та строка, что стоит первой";
        }

        return "сработает та строка, что стоит первой";
    }

    private static bool Pair(RouteChoice one, RouteChoice other, RouteChoice a, RouteChoice b) =>
        (one == a && other == b) || (one == b && other == a);
}
