namespace NetZapret.Zapret;

/// <summary>Чем оказался сохранённый выбор рецепта.</summary>
public sealed record RecipeLookup
{
    /// <summary>Шаги; пусто — рецепт не найден.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Откуда взят — для сообщений.</summary>
    public required string Source { get; init; }

    /// <summary>Нашёлся ли вообще.</summary>
    public bool Found => Steps.Count > 0;

    /// <summary>
    /// Модули, которых пресету не хватает, чтобы это выполнить.
    /// </summary>
    /// <remarks>
    /// Пусто и у ненайденного, и у исправного. Непустое означает беду
    /// особого рода: шаги известны, но winws2 с ними не поднимется вовсе —
    /// а он несёт весь десинк, не только это имя.
    /// </remarks>
    public IReadOnlyList<string> MissingModules { get; init; } = [];

    public static RecipeLookup Nothing { get; } = new()
    {
        Steps = [],
        Source = "не найден",
    };
}

/// <summary>
/// Находит рецепт по сохранённому имени — в пресете или в каталоге.
/// </summary>
/// <remarks>
/// <para>
/// Источников два с 21.09, и потому поиск обязан быть один. Пока рецепты
/// брались только из пресета, каждое место искало их само и это сходило
/// с рук; стоило добавить каталог, как разошлись ответы: окно выбора
/// предлагало рецепт, а сборка конфига его не находила и молча не создавала
/// профиль. Владелец увидел «десинк: tls-multisplit-sni — нет в пресете»
/// при том, что сам его только что и выбрал.
/// </para>
/// <para>
/// Пресет спрашивается первым. Имена там — имена секций, и совпадение
/// с нашим именем из каталога маловероятно, но если оно случится, верить
/// надо пресету: он описывает то, что на этой машине вправду настроено.
/// </para>
/// </remarks>
public static class RecipeResolver
{
    public static RecipeLookup Find(
        ZapretPreset preset,
        string? name,
        IReadOnlyDictionary<string, string>? providers = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return RecipeLookup.Nothing;

        if (DesyncRecipes.Find(preset, name) is { Steps.Count: > 0 } own)
        {
            return new RecipeLookup
            {
                Steps = own.Steps,
                Source = $"пресет «{preset.Name}»",
            };
        }

        if (RecipeCatalog.Find(name) is not { } fromCatalog)
            return RecipeLookup.Nothing;

        // Модули проверяются, только если есть чем: список приёмов читается
        // с диска, и без движка рядом его просто нет. Молчать в таком случае
        // честнее, чем объявлять нехватку, которую не проверяли.
        var missing = providers is null
            ? []
            : RecipeCatalog.Check(fromCatalog, preset, providers).MissingModules;

        return new RecipeLookup
        {
            Steps = fromCatalog.Steps,
            Source = "каталог",
            MissingModules = missing,
        };
    }

    /// <summary>Знаем ли мы такой рецепт вообще.</summary>
    public static bool Known(ZapretPreset preset, string? name) =>
        Find(preset, name).Found;
}
