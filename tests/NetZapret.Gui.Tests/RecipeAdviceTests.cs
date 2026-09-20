using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Какой рецепт окно советует, когда открывают несколько.
/// </summary>
/// <remarks>
/// <para>
/// Прежде совет был «берите любой из отмеченных — если сомневаетесь, тот,
/// что применяется к знакомому сервису». Любой — значит гадать, а «знакомый
/// сервис» человек узнаёт не всегда: имена секций пресета названы по тому,
/// что они чинят, и «obsidian.md» ничего не говорит тому, кто чинит Spotify.
/// </para>
/// <para>
/// Теперь советуется быстрейший, и это замер, а не мнение: рецепт,
/// при котором рукопожатие проходит вдвое быстрее, и в работе ведёт себя
/// лучше. Строка выбора — единственная чистая часть окна, и проверяется
/// именно она.
/// </para>
/// </remarks>
public sealed class RecipeAdviceTests
{
    private static RecipeRow Row(string title, double? seconds, bool current = false) => new()
    {
        Name = title,
        Title = title,

        // Шаги строка носит сама с 21.09: прежде проверка искала их
        // по имени в пресете, а рецепта из каталога там нет вовсе.
        Steps = ["multidisorder:pos=1,host+2"],

        Summary = title,
        UsedBy = string.Empty,
        Current = current,
        Took = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };

    /// <summary>Советуется тот, что открыл быстрее всех.</summary>
    [Fact]
    public void The_fastest_working_recipe_is_advised()
    {
        RecipeRow[] rows =
        [
            Row("медленный", 3.4),
            Row("быстрый", 0.7),
            Row("средний", 1.9),
        ];

        Assert.Equal("быстрый", Best(rows)!.Title);
    }

    /// <summary>
    /// Неработавшие в расчёт не идут вовсе.
    /// </summary>
    /// <remarks>
    /// У неработающего время означает срок ожидания, а не скорость: он
    /// «быстро не смог». Пустить его в сравнение значило бы советовать
    /// самый быстрый отказ.
    /// </remarks>
    [Fact]
    public void Recipes_that_did_not_work_are_not_considered()
    {
        RecipeRow[] rows =
        [
            Row("не помог", null),
            Row("помог", 2.5),
            Row("тоже не помог", null),
        ];

        Assert.Equal("помог", Best(rows)!.Title);
    }

    /// <summary>Не помог ни один — советовать нечего.</summary>
    [Fact]
    public void Nothing_working_means_nothing_advised()
    {
        Assert.Null(Best([Row("а", null), Row("б", null)]));
    }

    /// <summary>
    /// Быстрейшим может оказаться тот, что уже применяется.
    /// </summary>
    /// <remarks>
    /// Это не бесполезный ответ, а самый ценный: он означает, что менять
    /// нечего и беда не в рецепте. Без пометки человек выбрал бы ровно
    /// то, что и так действует, и не понял, почему ничего не изменилось.
    /// </remarks>
    [Fact]
    public void The_recipe_already_in_use_is_named_as_such()
    {
        RecipeRow[] rows =
        [
            Row("чужой", 2.0),
            Row("нынешний", 0.5, current: true),
        ];

        var best = Best(rows)!;

        Assert.Equal("нынешний", best.Title);
        Assert.True(best.Current);
    }

    /// <summary>Ровно один рецепт помечается нынешним, и только он.</summary>
    [Fact]
    public void Only_one_recipe_is_the_current_one()
    {
        RecipeRow[] rows = [Row("а", 1.0), Row("б", 2.0, current: true), Row("в", 3.0)];

        Assert.Single(rows, r => r.Current);
    }

    /// <summary>
    /// Та самая выборка, что делает окно, — а не её пересказ.
    /// </summary>
    /// <remarks>
    /// Здесь стояла копия в одну строку, и она проверяла сама себя: поменяй
    /// окно правило выбора — тест остался бы зелёным.
    /// </remarks>
    private static RecipeRow? Best(IEnumerable<RecipeRow> rows) => RecipeRow.Best(rows);
}
