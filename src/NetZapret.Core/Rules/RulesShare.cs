namespace NetZapret.Core.Rules;

/// <summary>Чем отличаются чужие маршруты от нынешних.</summary>
/// <param name="Changed">То же правило с другим маршрутом, рецептом или включённостью.</param>
public sealed record RulesDiff(
    IReadOnlyList<UserRuleEntry> Added,
    IReadOnlyList<UserRuleEntry> Removed,
    IReadOnlyList<(UserRuleEntry Before, UserRuleEntry After)> Changed)
{
    public bool Same => Added.Count == 0 && Removed.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// Поделиться маршрутами: файл <c>rules.user.yaml</c> как есть.
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 30.09: книгу маршрутов — отдельный формат «имя: маршрут»
/// с переводом туда и обратно — вырезать и делиться самим файлом правил.
/// Книга маршрутами не управляла, а переводчик отстал на два новшества:
/// правила для программ при вывозе пропадали, а свои списки при ввозе
/// указывали на несуществующий файл и молча переставали ловить.
/// Файл правил переводить не надо — он и есть маршруты.
/// </para>
/// <para>
/// В нём нет ни ссылок, ни ключей, ни адресов машины: тип, значение,
/// маршрут, рецепт (проверено на файле владельца, 102 правила). Списки
/// указаны путями каталога, которые едут вместе с программой.
/// </para>
/// </remarks>
public static class RulesShare
{
    /// <summary>
    /// Читает чужой файл маршрутов; не разобрался — исключение с причиной.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Не <see cref="UserRulesFile.Load"/>: тот испорченный файл отодвигает
    /// в <c>.broken</c>, а файл, выбранный для загрузки, — чужой, и трогать
    /// его нельзя.
    /// </para>
    /// <para>
    /// Правила по программе не загружаются: обход по программе на
    /// переработке, и у себя мы их сняли (<see cref="ProgramRulesOff"/>).
    /// Чужой файл вернул бы их в обход этого.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<UserRuleEntry> Read(string path) =>
        RuleSetLoader.LoadRawRules(path)
            .Where(r => r.Match != MatchKind.Process)
            .Select(r => new UserRuleEntry
            {
                Match = r.Match,
                Value = r.Value,
                Mode = r.Mode,
                Recipe = r.Recipe,
                Enabled = r.Enabled,
            })
            .ToList();

    /// <summary>Что добавится, что уйдёт и что сменит маршрут.</summary>
    /// <remarks>
    /// Одно правило — это пара «тип + значение», как в <see cref="UserRulesFile.Set"/>.
    /// Путь сравнивается без учёта регистра и направления черты: так его
    /// сравнивает и сам файл правил.
    /// </remarks>
    public static RulesDiff Compare(IReadOnlyList<UserRuleEntry> current, IReadOnlyList<UserRuleEntry> incoming)
    {
        var before = current
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var after = incoming
            .GroupBy(Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var added = incoming.Where(e => !before.ContainsKey(Key(e))).DistinctBy(Key, StringComparer.OrdinalIgnoreCase).ToList();
        var removed = current.Where(e => !after.ContainsKey(Key(e))).DistinctBy(Key, StringComparer.OrdinalIgnoreCase).ToList();

        var changed = after
            .Where(p => before.TryGetValue(p.Key, out var was) && Differs(was, p.Value))
            .Select(p => (before[p.Key], p.Value))
            .ToList();

        return new RulesDiff(added, removed, changed);
    }

    private static string Key(UserRuleEntry entry) => $"{entry.Match}|{entry.Value.Replace('\\', '/').Trim()}";

    private static bool Differs(UserRuleEntry one, UserRuleEntry other) =>
        one.Mode != other.Mode
        || one.Enabled != other.Enabled
        || !string.Equals(one.Recipe ?? string.Empty, other.Recipe ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Правила на списки, файлов которых здесь нет.
    /// </summary>
    /// <remarks>
    /// Правило на несуществующий список не совпадает ни с чем — молча
    /// (CLAUDE.md, «Код»). Так выходит со своими списками чужой машины
    /// (<c>config/lists/own/</c> в файл не входят) и со списком, который
    /// в другой версии программы назван иначе. Сказать об этом надо
    /// до замены, а не после вечера разбора.
    /// </remarks>
    /// <param name="root">Корень установки, от которого считаются пути; <c>null</c> — рабочая папка.</param>
    public static IReadOnlyList<UserRuleEntry> MissingLists(IEnumerable<UserRuleEntry> rules, string? root = null) =>
        rules
            .Where(e => e.Match is MatchKind.HostList or MatchKind.IpSet)
            .Where(e => !File.Exists(Path.Combine(root ?? string.Empty, e.Value.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

    /// <summary>Сколько правил указывает на свои списки — их доменов в файле нет.</summary>
    public static int OnOwnLists(IEnumerable<UserRuleEntry> rules) =>
        rules.Count(e => e.Match == MatchKind.HostList && OwnLists.IsOwn(e.Value));

    /// <summary>Имя правила для людей: у списка — название файла, у прочих — значение.</summary>
    public static string NameOf(UserRuleEntry entry) =>
        entry.Match is MatchKind.HostList or MatchKind.IpSet
            ? Path.GetFileNameWithoutExtension(entry.Value.Replace('\\', '/'))
            : entry.Value.Trim().Trim('"');

    /// <summary>Правило коротко, для перечня в вопросе: «discord → десинк».</summary>
    public static string Describe(UserRuleEntry entry) =>
        $"{NameOf(entry)} → {entry.DescribeMode()}" + (entry.Enabled ? string.Empty : " (выключено)");

    /// <summary>Смена коротко: «discord: десинк → VPN».</summary>
    public static string Describe(UserRuleEntry before, UserRuleEntry after)
    {
        var was = before.DescribeMode() + (before.Enabled ? string.Empty : " (выкл.)");
        var now = after.DescribeMode() + (after.Enabled ? string.Empty : " (выкл.)");

        // Тот же маршрут — значит сменился рецепт.
        if (was == now)
        {
            was += $" ({before.Recipe ?? "рецепт пресета"})";
            now += $" ({after.Recipe ?? "рецепт пресета"})";
        }

        return $"{NameOf(after)}: {was} → {now}";
    }

    /// <summary>
    /// Копия нынешнего файла перед заменой; возвращает её путь или <c>null</c>, если копировать нечего.
    /// </summary>
    /// <remarks>
    /// В <c>runtime\</c>, а не рядом в <c>config\</c>: тот не весь в .gitignore,
    /// и копия личного файла уехала бы в коммит (CLAUDE.md, «Секреты»).
    /// </remarks>
    public static string? Backup(string? rulesPath = null, string? folder = null)
    {
        var source = rulesPath ?? UserRulesFile.DefaultPath;

        if (!File.Exists(source))
            return null;

        var target = Path.Combine(folder ?? "runtime", $"rules.user.yaml.bak-{DateTime.Now:yyyyMMdd-HHmmss}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.Copy(source, target, overwrite: true);

        return target;
    }
}
