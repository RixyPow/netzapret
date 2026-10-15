namespace NetZapret.Zapret;

/// <summary>
/// Читает доменные списки Zapret — те же файлы, что получает winws2.
/// </summary>
/// <remarks>
/// <para>
/// Читаются как есть и не копируются: списки собраны проектом Zapret
/// и обновляются вместе с ним, а копия у нас устареет молча.
/// </para>
/// <para>
/// Формат простой: одно имя в строке, пустые строки и начинающиеся с решётки
/// пропускаются. Встречаются записи с ведущей точкой (<c>.example.com</c>) —
/// точка убирается, зоной имя считается и без неё.
/// </para>
/// </remarks>
public static class HostListReader
{
    /// <summary>
    /// Загружает домены из списка.
    /// </summary>
    /// <param name="path">Путь, обычно вида <c>lists/discord-media.txt</c>.</param>
    /// <param name="zapretRoot">Корень установки; относительно него разрешается путь.</param>
    /// <param name="problems">Что не так со списком — пусто, если всё в порядке.</param>
    public static IReadOnlyList<string> Read(
        string path,
        string? zapretRoot,
        out IReadOnlyList<string> problems)
    {
        var found = new List<string>();
        var issues = new List<string>();
        problems = issues;

        var full = Resolve(path, zapretRoot);

        if (full is null)
        {
            issues.Add($"список доменов не найден: {path}");
            return found;
        }

        // Прочитанный и не изменившийся с тех пор файл — тот же список, а не
        // копия: правила, щит, подписи «Маршрутов» и «Наблюдение» читают одни
        // и те же файлы, и реестр блокировок (117 тысяч имён) лежал в окне
        // копией на каждого — замер 15.10: 7 МБ за чтение. С одним экземпляром
        // и множество зон у правил одно (RoutingRule.LoadHostList).
        FileInfo info;
        try
        {
            info = new FileInfo(full);
            info.Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add($"{path}: {ex.Message}");
            return found;
        }

        var key = Path.GetFullPath(full);

        if (Kept.TryGetValue(key, out var kept) && kept.Written == info.LastWriteTimeUtc && kept.Length == info.Length
            && kept.Names.TryGetTarget(out var shared))
        {
            if (shared.Length == 0)
                issues.Add($"{path}: список пуст");

            return shared;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(full);
        }
        catch (IOException ex)
        {
            issues.Add($"{path}: {ex.Message}");
            return found;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Ведущая точка означает ту же зону: у winws2 это равнозначные записи.
            if (line.StartsWith('.'))
                line = line[1..];

            if (line.Length > 0 && seen.Add(line))
                found.Add(line);
        }

        if (found.Count == 0)
            issues.Add($"{path}: список пуст");

        var names = found.ToArray();

        // Только что записанный файл не запоминается: две записи подряд могут
        // лечь в одну отметку времени при той же длине, и вторая читалась бы
        // как первая. Свой список, правленный окном, перечитается и так.
        if (DateTime.UtcNow - info.LastWriteTimeUtc > TimeSpan.FromSeconds(2))
            Kept[key] = new KeptList(info.LastWriteTimeUtc, info.Length, new WeakReference<string[]>(names));

        return names;
    }

    /// <summary>
    /// Прочитанный файл: когда он был записан, сколько весил и что в нём.
    /// </summary>
    /// <remarks>
    /// Список — слабой ссылкой: один на всех, пока он кому-то нужен, и не
    /// держится, когда не нужен никому. Окно в трее с 15.10 закрывается
    /// по-настоящему, и сильная ссылка оставила бы за процессом реестр
    /// блокировок с его множествами (~25 МБ, замер того же дня) навсегда.
    /// </remarks>
    private sealed record KeptList(DateTime Written, long Length, WeakReference<string[]> Names);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, KeptList> Kept =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Разрешает путь: сперва относительно корня Zapret, затем как есть.
    /// </summary>
    /// <remarks>
    /// Пути в правилах пишутся как в пресетах — <c>lists/имя.txt</c>, и это
    /// путь от корня установки. Проверка «как есть» оставлена ради своих
    /// списков, лежащих рядом с конфигом.
    /// </remarks>
    private static string? Resolve(string path, string? zapretRoot)
    {
        if (!string.IsNullOrEmpty(zapretRoot))
        {
            var candidate = Path.Combine(zapretRoot, path);
            if (File.Exists(candidate))
                return candidate;
        }

        return File.Exists(path) ? path : null;
    }
}
