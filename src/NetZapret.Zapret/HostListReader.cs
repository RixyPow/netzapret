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

        return found;
    }

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
