namespace NetZapret.Zapret;

/// <summary>Секция пресета, которая перехватит имя, и чем именно.</summary>
public sealed record PresetMatch
{
    /// <summary>Порядковый номер секции в файле, считая с единицы.</summary>
    public required int Ordinal { get; init; }

    /// <summary>Значение <c>--name</c>; у безымянной — пустая строка.</summary>
    public required string Name { get; init; }


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
