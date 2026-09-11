namespace NetZapret.Zapret;

/// <summary>Один рецепт десинка: чем именно чинить имя.</summary>
public sealed record DesyncRecipe
{
    /// <summary>
    /// Как рецепт хранится в правиле.
    /// </summary>
    /// <remarks>
    /// Имя секции пресета: оно единственное здесь устойчиво. Показывать его
    /// человеку нельзя — секции называют по сервису, и рецепт получал имя
    /// вроде «obsidian.md» или «AnyDesk UDP», которое к чинимому домену
    /// отношения не имеет и только сбивает.
    /// </remarks>
    public required string Name { get; init; }

    /// <summary>Аргументы <c>--lua-desync</c> по порядку, без ключа.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Секции пресета, где встречается тот же набор.</summary>
    public required IReadOnlyList<string> UsedBy { get; init; }

    /// <summary>
    /// Как рецепт называется в окне: по тому, что он делает.
    /// </summary>
    /// <remarks>
    /// Приёмы и их порядок — единственное, чем рецепты действительно
    /// различаются. Повторы разводит <see cref="DesyncRecipes"/>, дописывая
    /// узнаваемый сервис.
    /// </remarks>
    public string Title { get; init; } = string.Empty;

    /// <summary>Набор целиком, с настройками — для тех, кто читает их глазами.</summary>
    public string Detail => Steps.Count == 0
        ? "ничего не делать"
        : string.Join("   ·   ", Steps);

    /// <summary>Приёмы по порядку, без настроек.</summary>
    public string Technique => Steps.Count == 0
        ? "ничего не делать"
        : string.Join(" + ", Steps.Select(Kind).Distinct());

    /// <summary>Где этот же набор применяется в пресете.</summary>
    public string Where => UsedBy.Count == 0
        ? string.Empty
        : "в пресете: " + string.Join(", ", UsedBy.Take(8))
            + (UsedBy.Count > 8 ? $" и ещё {UsedBy.Count - 8}" : string.Empty);

    /// <summary>Название приёма без его настроек.</summary>
    private static string Kind(string step)
    {
        int colon = step.IndexOf(':');

        return colon < 0 ? step : step[..colon];
    }
}

/// <summary>
/// Собирает список рецептов из пресета.
/// </summary>
/// <remarks>
/// <para>
/// Источник — сам пресет, а не наш придуманный перечень. В нём лежат наборы,
/// выверенные на живых сетях: автор пресета подбирал их под конкретные
/// блокировки и обновляет, когда те меняются. Свой список мы бы выдумали
/// один раз и оставили стареть.
/// </para>
/// <para>
/// Одинаковые наборы схлопываются: в пресете на полсотни секций рецептов
/// обычно меньше десятка, и список из пятидесяти строк, где сорок повторяют
/// друг друга, выбирать не помогает.
/// </para>
/// </remarks>
public static class DesyncRecipes
{
    /// <summary>Значение, означающее «решает пресет» — то есть выбора нет.</summary>
    public const string FromPreset = "";

    /// <summary>
    /// Чем склеивать шаги в ключ группировки.
    /// </summary>
    /// <remarks>
    /// Перевод строки, а не пробел, и это не вкус. Пробел уже однажды
    /// превратился при правке файла в нулевой байт, ключ перестал разбираться
    /// обратно, и каждый рецепт стал одношаговым: в окне вместо
    /// «fake + multidisorder» стояло «fake». Символ, которого в аргументах
    /// не бывает по построению, такой поломки не переживёт незамеченным —
    /// она сломает сборку, а не вывод.
    /// </remarks>
    private const char Separator = '\n';

    public static IReadOnlyList<DesyncRecipe> FromPresetFile(ZapretPreset preset)
    {
        var groups = new Dictionary<string, List<ZapretSection>>(StringComparer.Ordinal);

        foreach (var section in preset.Sections)
        {
            // Пропускающие секции рецептом не являются: у них ровно одно
            // назначение — не трогать. Для этого есть «мимо десинка».
            if (section.IsPassThrough || section.DesyncRecipes.Count == 0)
                continue;

            // Секции по UDP тоже мимо: выбранный рецепт проверяется
            // рукопожатием TLS, а ему в UDP проверять нечего. Такие приёмы
            // получали «не помогает» независимо от собственных достоинств —
            // и попадали в список наравне с работающими, сбивая выбор.
            // Заодно уходит и «как у „AnyDesk UDP“» в названиях.
            if (!section.CarriesTcp)
                continue;

            var key = string.Join(Separator, section.DesyncRecipes);

            if (!groups.TryGetValue(key, out var members))
                groups[key] = members = [];

            members.Add(section);
        }

        var recipes = groups
            .Select(pair => new DesyncRecipe
            {
                // Хранится имя секции — единственное, что здесь устойчиво.
                // Показывается другое: см. Title.
                Name = pair.Value[0].Name,
                Steps = pair.Key.Split(Separator),
                UsedBy = pair.Value.Select(s => s.Name).ToList(),
            })
            .ToList();

        // Название по приёмам, а не по сервису. Секции пресета названы по тому,
        // что чинят, и рецепту доставалось имя вроде «AnyDesk UDP» — при том
        // что чинить им собрались совсем другое имя. Человек читал его как
        // обещание и не понимал, при чём здесь AnyDesk.
        var shared = recipes
            .GroupBy(recipe => recipe.Technique, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return recipes
            .Select(recipe => recipe with
            {
                // Одного приёма мало, когда его делят несколько наборов:
                // «fake» встречается у половины пресета с разными настройками.
                // Тогда дописываем узнаваемый сервис — по нему и выбирают.
                Title = shared[recipe.Technique] > 1
                    ? $"{recipe.Technique} — как у «{recipe.UsedBy[0]}»"
                    : recipe.Technique,
            })
            .OrderBy(recipe => recipe.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>Находит рецепт по хранимому имени; <c>null</c> — такого нет.</summary>
    /// <remarks>
    /// Отсутствие возможно и в обычной работе: рецепт назван по секции
    /// пресета, а пресет сменили. Правило при этом остаётся, и молча
    /// применить чужой набор вместо названного было бы хуже, чем не
    /// применить ничего.
    /// </remarks>
    /// <summary>
    /// Рецепт по сохранённому имени.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ищется и среди хранимых имён, и среди <see cref="DesyncRecipe.UsedBy"/>.
    /// Имя рецепта — это имя первой секции в группе, а группу задаёт порядок
    /// секций в файле. Поменяй его — и рецепт, выбранный вчера, перестаёт
    /// находиться: в правиле лежит «discord.com», а группа зовётся теперь
    /// «updates.discord.com», потому что та секция стоит выше.
    /// </para>
    /// <para>
    /// Молча, что хуже всего: ненайденный рецепт означает пустой набор шагов,
    /// профиль с пустым набором не выпускается вовсе, и выбор просто
    /// не применяется. В меню при этом честно написано «десинк:
    /// hostfakesplit_multi». Ровно так пропал голос Discord: правка V8
    /// поставила <c>updates.discord.com</c> выше <c>discord.com</c>, и
    /// сохранённый выбор для голоса перестал что-либо значить.
    /// </para>
    /// <para>
    /// Своё имя проверяется первым: набор шагов у него тот же, но искать
    /// сперва по точному совпадению дешевле и понятнее.
    /// </para>
    /// </remarks>
    public static DesyncRecipe? Find(ZapretPreset preset, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var wanted = name.Trim();
        var recipes = FromPresetFile(preset);

        return recipes.FirstOrDefault(r =>
                   r.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            ?? recipes.FirstOrDefault(r =>
                   r.UsedBy.Any(section =>
                       section.Equals(wanted, StringComparison.OrdinalIgnoreCase)));
    }
}
