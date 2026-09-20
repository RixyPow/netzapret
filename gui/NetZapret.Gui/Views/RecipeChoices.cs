using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Один пункт выбора рецепта, ещё без вердиктов и кистей.</summary>
internal sealed record RecipeChoice
{
    /// <summary>Что ляжет в правило.</summary>
    public required string Name { get; init; }

    public required string Title { get; init; }

    /// <summary>Шаги <c>--lua-desync</c> по порядку.</summary>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Откуда взят — для подписи под строкой.</summary>
    public required string Source { get; init; }

    /// <summary>Пресет и так применяет этот набор к этому имени.</summary>
    public bool Current { get; init; }
}

/// <summary>
/// Из чего выбирают рецепт для одного имени.
/// </summary>
/// <remarks>
/// <para>
/// Источников два, и они дополняют друг друга. Пресет даёт наборы,
/// выверенные на живых сетях: автор подбирал их под конкретные блокировки.
/// Каталог даёт приёмы, которые движок умеет, а этот пресет не применяет, —
/// их иначе не предложить вовсе.
/// </para>
/// <para>
/// Из каталога берутся только те, что пресет потянет. Рецепт, чей модуль
/// не подключён, winws2 не запустит вовсе — проверка ответила бы
/// «не помогает», и это было бы неправдой: приём не пробовали, его
/// не существовало. Что подключить, чтобы он появился, говорит каталог
/// на вкладке «Десинк».
/// </para>
/// <para>
/// Повторы сводятся по шагам, а не по именам. Один и тот же набор
/// у нас зовётся «Разрез по имени», а в пресете — именем первой секции,
/// где он встретился; показать оба значило бы предложить дважды одно.
/// </para>
/// </remarks>
internal static class RecipeChoices
{
    public static IReadOnlyList<RecipeChoice> Build(
        ZapretPreset preset,
        IReadOnlyDictionary<string, string> providers,
        string? applied)
    {
        var choices = new List<RecipeChoice>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Пресет первым: его наборы проверены на деле, и при совпадении
        // шагов остаться должен он — вместе со ссылкой на секцию, по которой
        // человек и узнаёт знакомое.
        foreach (var recipe in DesyncRecipes.FromPresetFile(preset))
        {
            if (!seen.Add(Key(recipe.Steps)))
                continue;

            choices.Add(new RecipeChoice
            {
                Name = recipe.Name,
                Title = recipe.Title,
                Steps = recipe.Steps,
                Source = recipe.Where,
                Current = applied is not null
                    && string.Equals(recipe.Name, applied, StringComparison.OrdinalIgnoreCase),
            });
        }

        foreach (var recipe in RecipeCatalog.All)
        {
            if (!RecipeCatalog.Check(recipe, preset, providers).Runnable)
                continue;

            if (!seen.Add(Key(recipe.Steps)))
                continue;

            choices.Add(new RecipeChoice
            {
                Name = recipe.Id,
                Title = recipe.Title,
                Steps = recipe.Steps,
                Source = "из каталога: " + recipe.What,
            });
        }

        return choices;
    }

    /// <summary>
    /// Чем считать два набора одним.
    /// </summary>
    /// <remarks>
    /// Перевод строки разделителем — символ, которого в аргументах
    /// не бывает по построению. Пробел однажды уже превратился при правке
    /// в нулевой байт, и ключ перестал разбираться обратно.
    /// </remarks>
    private static string Key(IReadOnlyList<string> steps) => string.Join('\n', steps);
}
