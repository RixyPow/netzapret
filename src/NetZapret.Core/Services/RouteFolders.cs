using NetZapret.Core.Rules;

namespace NetZapret.Core.Services;

/// <summary>
/// Раскладка маршрутов «Маршрутов» по папкам — с тем, что человек переложил сам.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 13.10: «YouTube — это папка, а внутри маршруты; добавим
/// возможность засовывать маршруты в папки и вытаскивать из папок».
/// Папки каталога (<see cref="ServiceCatalog"/>) остаются как есть, а
/// перекладка хранится поверх них в настройках
/// (<see cref="AppSettings.RouteFolders"/>): ключ строки → папка, пустая
/// строка — вне папки.
/// </para>
/// <para>
/// Только вид. Правила, их порядок и списки перекладка не трогает:
/// маршрут «Видеопоток», положенный в папку «Мои», идёт туда же, куда шёл.
/// </para>
/// </remarks>
public static class RouteFolders
{
    /// <summary>
    /// Ключ строки «Маршрутов» для правила — тот, под которым перекладка
    /// и порядок лежат в настройках.
    /// </summary>
    /// <remarks>
    /// Тот же, что строит окно (<c>RouteKeys</c>): свой домен и свой список —
    /// <c>own|</c>, список программы и часть каталога — <c>hostlist|</c>,
    /// адресная часть — <c>ipset|</c>, своя подсеть — <c>net|</c>. Здесь — для
    /// загрузки маршрутов файлом (<see cref="Rules.RouteBundles"/>): папку
    /// привезённым маршрутам надо записать теми же ключами, иначе они
    /// легли бы мимо неё. Согласие с окном сторожит тест окна.
    /// </remarks>
    public static string KeyOf(MatchKind match, string value) => match switch
    {
        MatchKind.Domain => "own|" + value,
        MatchKind.HostList when OwnLists.IsOwn(value) && !ProgramRoutes.IsProgramList(value) => "own|" + value,
        MatchKind.HostList => "hostlist|" + value,
        MatchKind.IpSet => "ipset|" + value,
        MatchKind.Ip => "net|" + value,
        _ => "prog|" + value,
    };

    /// <summary>Ключ места пустой папки в порядке: у заведённой папки без маршрутов место всё равно есть.</summary>
    public static string Place(string folder) => "folder|" + folder;

    /// <summary>
    /// Папка каталога, в которой часть лежит без перекладки; <c>null</c> — сама по себе.
    /// </summary>
    /// <remarks>
    /// Как раскладывает окно: часть полки с разными сервисами
    /// (<see cref="ServiceDefinition.Grouping"/>) и единственная часть сервиса
    /// стоят отдельно, прочие — в папке сервиса. Свои маршруты — вне папок.
    /// </remarks>
    public static string? HomeOf(MatchKind match, string value)
    {
        if (match is not (MatchKind.HostList or MatchKind.IpSet))
            return null;

        var list = value.Replace('\\', '/').Trim();

        foreach (var service in ServiceCatalog.All)
        {
            if (service.Parts.Any(p => p.ByAddress == (match == MatchKind.IpSet)
                && string.Equals(p.List, list, StringComparison.OrdinalIgnoreCase)))
            {
                return service.Grouping || service.Parts.Count == 1 ? null : service.Name;
            }
        }

        return null;
    }

    /// <summary>
    /// Раскладывает маршруты по папкам.
    /// </summary>
    /// <param name="items">Маршруты по порядку: папка каталога (<c>null</c> — вне папки), ключ, сам маршрут.</param>
    /// <param name="moved">Перекладка человека: ключ → папка, пустая строка — вне папки.</param>
    /// <returns>
    /// Папки и одиночные маршруты по порядку первого появления. У одиночного
    /// <c>Folder</c> — <c>null</c>, и маршрут в нём один.
    /// </returns>
    /// <remarks>
    /// Папка встаёт там, где появился первый её маршрут, — переложенный
    /// в новую папку маршрут создаёт её на своём прежнем месте, а не в конце
    /// списка, где её не найти.
    /// </remarks>
    public static IReadOnlyList<(string? Folder, IReadOnlyList<T> Items)> Arrange<T>(
        IEnumerable<(string? Folder, string Key, T Item)> items,
        IReadOnlyDictionary<string, string> moved)
    {
        var groups = new List<(string? Folder, List<T> Items)>();
        var byName = new Dictionary<string, List<T>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (catalog, key, item) in items)
        {
            var folder = moved.TryGetValue(key, out var chosen) ? chosen : catalog;

            if (string.IsNullOrWhiteSpace(folder))
            {
                groups.Add((null, [item]));
                continue;
            }

            folder = folder.Trim();

            if (byName.TryGetValue(folder, out var list))
            {
                list.Add(item);
                continue;
            }

            list = [item];
            byName[folder] = list;
            groups.Add((folder, list));
        }

        return groups.Select(g => (g.Folder, (IReadOnlyList<T>)g.Items)).ToList();
    }

    /// <summary>
    /// Порядок человека поверх порядка каталога (перетаскивание, 13.10).
    /// </summary>
    /// <param name="items">Маршруты в порядке каталога.</param>
    /// <param name="saved">Ключи в порядке, который человек оставил перетаскиванием.</param>
    /// <remarks>
    /// Маршрут, которого в сохранённом порядке нет, — новый сервис после
    /// обновления или свой домен — встаёт за последним из тех, кто стоял
    /// перед ним в каталоге: туда же, где был бы без перетаскивания, и не
    /// разрезая чужую папку.
    /// </remarks>
    public static IReadOnlyList<(string? Folder, string Key, T Item)> Order<T>(
        IReadOnlyList<(string? Folder, string Key, T Item)> items,
        IReadOnlyList<string> saved)
    {
        if (saved.Count == 0)
            return items;

        var place = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < saved.Count; i++)
            place.TryAdd(saved[i], i);

        int last = -1;
        var keyed = new List<(int Anchor, int Late, int Index, (string? Folder, string Key, T Item) Item)>();

        for (int i = 0; i < items.Count; i++)
        {
            if (place.TryGetValue(items[i].Key, out var at))
            {
                last = Math.Max(last, at);
                keyed.Add((at, 0, i, items[i]));
            }
            else
            {
                keyed.Add((last, 1, i, items[i]));
            }
        }

        return keyed
            .OrderBy(k => k.Anchor)
            .ThenBy(k => k.Late)
            .ThenBy(k => k.Index)
            .Select(k => k.Item)
            .ToList();
    }

    /// <summary>
    /// Переносит ключи перед или за целью — целым куском, в прежнем порядке.
    /// </summary>
    /// <param name="order">Все ключи в нынешнем порядке.</param>
    /// <param name="moving">Что переносится: маршрут или все маршруты папки.</param>
    /// <param name="target">Ключ, у которого встать.</param>
    /// <param name="after">За целью, а не перед ней.</param>
    /// <returns>Новый порядок; цель среди переносимых — прежний.</returns>
    public static IReadOnlyList<string> MoveKeys(
        IReadOnlyList<string> order,
        IReadOnlyCollection<string> moving,
        string target,
        bool after)
    {
        if (moving.Contains(target))
            return order;

        var block = order.Where(moving.Contains).ToList();

        // Переносимого в порядке нет — он новый, встаёт как есть.
        block.AddRange(moving.Where(k => !order.Contains(k)));

        var rest = order.Where(k => !moving.Contains(k)).ToList();
        int at = rest.IndexOf(target);

        if (at < 0)
            return [.. rest, .. block];

        rest.InsertRange(after ? at + 1 : at, block);
        return rest;
    }

    /// <summary>
    /// Перекладывает маршрут: в папку, вне папки или обратно на место каталога.
    /// </summary>
    /// <param name="folder">Папка; пустая строка — вне папки.</param>
    /// <param name="catalogFolder">Где маршрут лежит по каталогу; совпало — запись снимается.</param>
    public static IReadOnlyDictionary<string, string> Move(
        IReadOnlyDictionary<string, string> moved,
        string key,
        string folder,
        string? catalogFolder)
    {
        var result = new Dictionary<string, string>(moved, StringComparer.Ordinal);
        folder = folder.Trim();

        if (string.Equals(folder, catalogFolder?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            result.Remove(key);
        else
            result[key] = folder;

        return result;
    }
}
