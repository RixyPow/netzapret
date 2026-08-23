namespace NetZapret.Zapret;

/// <summary>
/// Пресет winws2, разобранный из файла формата Zapret 2 GUI.
/// </summary>
/// <remarks>
/// Формат: шапка из комментариев с метаданными, затем глобальные ключи,
/// затем секции, разделённые <c>--new</c>. Разбор только на чтение —
/// редактировать пресеты остаётся Zapret GUI.
/// </remarks>
public sealed record ZapretPreset
{
    public required string Name { get; init; }

    public required string FilePath { get; init; }

    public string? Description { get; init; }

    /// <summary>Версия из <c># BuiltinVersion:</c>.</summary>
    public string? BuiltinVersion { get; init; }

    /// <summary>Цвет значка из <c># IconColor:</c>, для будущего интерфейса.</summary>
    public string? IconColor { get; init; }

    /// <summary>Ключи до первого <c>--new</c>: инициализация Lua, блобы, фильтр WinDivert.</summary>
    public required IReadOnlyList<string> GlobalArguments { get; init; }

    /// <summary>Секции в порядке из файла — порядок значим при сопоставлении.</summary>
    public required IReadOnlyList<ZapretSection> Sections { get; init; }

    /// <summary>Секции, которые действительно применяют десинк.</summary>
    public IEnumerable<ZapretSection> ActiveSections => Sections.Where(s => !s.IsPassThrough);

    public override string ToString() => $"{Name} ({Sections.Count} секций)";
}

/// <summary>
/// Одна секция пресета: что сопоставляем и какой рецепт применяем.
/// </summary>
public sealed record ZapretSection
{
    /// <summary>Значение <c>--name</c>; у безымянной секции — пустая строка.</summary>
    public required string Name { get; init; }

    /// <summary>Пути к файлам <c>--hostlist</c> относительно корня Zapret.</summary>
    public required IReadOnlyList<string> HostListPaths { get; init; }

    /// <summary>Домены, заданные прямо в <c>--hostlist-domains</c>.</summary>
    public required IReadOnlyList<string> InlineDomains { get; init; }

    /// <summary>Пути к файлам <c>--ipset</c>.</summary>
    public required IReadOnlyList<string> IpSetPaths { get; init; }

    /// <summary>Рецепты <c>--lua-desync</c> по порядку.</summary>
    public required IReadOnlyList<string> DesyncRecipes { get; init; }

    /// <summary>
    /// Секция явно отключает десинк (<c>--lua-desync=pass</c>).
    /// </summary>
    /// <remarks>
    /// Отличать от «рецептов нет» обязательно: <c>pass</c> — осознанное
    /// исключение, означающее «сервис работает и без вмешательства».
    /// Такие домены не нужно ни чинить десинком, ни гнать через VPN.
    /// </remarks>
    public bool IsPassThrough { get; init; }

    /// <summary>Все аргументы секции как есть — запуск не должен зависеть от полноты разбора.</summary>
    public required IReadOnlyList<string> RawArguments { get; init; }

    /// <summary>
    /// Использует ли секция рецепт с поддельным пакетом.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Существенно для сосуществования с туннелем: замер 2026-08-23 показал,
    /// что под TUN ломаются именно рецепты с <c>fake</c>, тогда как чистые
    /// split и disorder переживают его. См. docs/coexistence.md.
    /// </para>
    /// <para>
    /// Считается только префикс <c>fake</c>. Наличие <c>:blob=</c> признаком не
    /// является: <c>syndata:blob=...</c> тоже подставляет блоб, но Discord с
    /// таким рецептом под TUN работал. Ориентир — измерение, а не форма записи.
    /// </para>
    /// </remarks>
    public bool UsesFakePackets =>
        DesyncRecipes.Any(r => r.StartsWith("fake", StringComparison.OrdinalIgnoreCase));

    public override string ToString() =>
        $"{Name} [{(IsPassThrough ? "pass" : string.Join(", ", DesyncRecipes.Select(Head)))}]";

    private static string Head(string recipe)
    {
        int colon = recipe.IndexOf(':');
        return colon < 0 ? recipe : recipe[..colon];
    }
}
