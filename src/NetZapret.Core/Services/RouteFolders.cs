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
