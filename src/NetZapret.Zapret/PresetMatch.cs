namespace NetZapret.Zapret;

/// <summary>Секция пресета, которая перехватит имя, и чем именно.</summary>
public sealed record PresetMatch
{
    /// <summary>Порядковый номер секции в файле, считая с единицы.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Значение <c>--name</c>; у безымянной — пустая строка.</summary>
    public required string Name { get; init; }

    /// <summary>Из-за какого списка совпало — для проверки вывода.</summary>
    public required string Because { get; init; }

    /// <summary>Рецепты по порядку; пусто, если секция пропускает трафик.</summary>
    public required IReadOnlyList<string> Recipes { get; init; }

    public bool IsPassThrough { get; init; }

    /// <summary>Короткая запись для отчёта.</summary>
    public string Describe() => IsPassThrough
        ? $"«{Name}» → не трогает (pass)"
        : Recipes.Count == 0
            ? $"«{Name}» → рецептов нет"
            : $"«{Name}» → {string.Join(" + ", Recipes.Select(Shorten))}";

    /// <summary>
    /// Обрезает рецепт до узнаваемого, но читаемого вида.
    /// </summary>
    /// <remarks>
    /// Полный рецепт — строка на полтораста знаков; в таблице она вытеснит
    /// всё остальное. Имя приёма и первый его параметр опознаются с одного
    /// взгляда, а подробности всегда есть в самом файле пресета.
    /// </remarks>
    private static string Shorten(string recipe)
    {
        var parts = recipe.Split(':');

        return parts.Length <= 2 ? recipe : $"{parts[0]}:{parts[1]}…";
    }
}

/// <summary>
/// Определяет, какая секция пресета возьмёт на себя данное имя.
/// </summary>
/// <remarks>
/// <para>
/// Ради отладки пресетов. Правится секция, до которой исполнение не доходит, —
/// её перехватывает более ранняя по большому списку, — и увидеть это нечем:
/// winws2 о своём выборе не отчитывается. Пять попыток починить один сайт
/// ушли впустую именно так.
/// </para>
/// <para>
/// Считаем сами: пресет и порядок секций нам известны, правило winws2
/// «выигрывает первая совпавшая» воспроизводится однозначно. Ответ приблизителен
/// ровно в одном: секции с отбором по протоколу (<c>--filter-l7</c>) сюда
/// не попадают — они смотрят на содержимое пакета, а не на имя, и предсказать
/// их по домену нельзя.
/// </para>
/// </remarks>
public static class PresetMatcher
{
    /// <summary>
    /// Первая секция, чей список покрывает имя.
    /// </summary>
    /// <param name="preset">Разобранный пресет.</param>
    /// <param name="host">Имя без ведущих точек и звёздочек.</param>
    /// <param name="zapretRoot">Корень установки — относительно него лежат списки.</param>
    public static PresetMatch? For(ZapretPreset preset, string host, string? zapretRoot)
    {
        var sections = preset.Sections;

        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];

            if (Because(section, host, zapretRoot) is not { } because)
                continue;

            return new PresetMatch
            {
                Ordinal = i + 1,
                Name = string.IsNullOrWhiteSpace(section.Name) ? "без имени" : section.Name,
                Because = because,
                Recipes = section.IsPassThrough ? [] : section.DesyncRecipes,
                IsPassThrough = section.IsPassThrough,
            };
        }

        return null;
    }

    /// <summary>Почему секция берёт это имя; <c>null</c> — не берёт.</summary>
    private static string? Because(ZapretSection section, string host, string? zapretRoot)
    {
        foreach (var domain in section.InlineDomains)
        {
            if (Covers(domain, host))
                return $"--hostlist-domains {domain}";
        }

        foreach (var path in section.HostListPaths)
        {
            var domains = HostListReader.Read(path, zapretRoot, out _);

            if (domains.Any(d => Covers(d, host)))
                return path;
        }

        // Адресные секции пропускаем намеренно. Чтобы сказать, попадает ли имя
        // в подсеть, надо его сперва разрешить, а адрес у имени зависит от того,
        // что ответит резолвер в этот миг, — вывод получился бы верным для
        // одного замера и неверным для следующего.
        return null;
    }

    /// <summary>
    /// Покрывает ли запись списка данное имя.
    /// </summary>
    /// <remarks>
    /// Запись означает зону: <c>discord.com</c> берёт и <c>cdn.discord.com</c>.
    /// Ведущая точка у winws2 равнозначна её отсутствию, поэтому срезается.
    /// </remarks>
    private static bool Covers(string entry, string host)
    {
        var zone = entry.TrimStart('*', '.');

        return string.Equals(zone, host, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase);
    }
}
