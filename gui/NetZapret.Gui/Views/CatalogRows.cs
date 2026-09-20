using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Строки каталога рецептов.
/// </summary>
/// <remarks>
/// <para>
/// Отдельно от вкладки затем, что это её единственная чистая часть
/// и единственная, которую можно проверить тестом. Оставь сборку строк
/// в обработчике — проверка повторила бы её своими словами и сторожила бы
/// собственную копию, а не то, что увидит человек.
/// </para>
/// <para>
/// Годные идут первыми. Недоступных в Universal V8 четыре из одиннадцати,
/// и вперемешку они читались бы как «каталог наполовину не работает»,
/// тогда как на деле это перечень того, что можно добавить.
/// </para>
/// </remarks>
internal static class CatalogRows
{
    public static IReadOnlyList<CatalogRow> Build(
        ZapretPreset? preset,
        IReadOnlyDictionary<string, string> providers)
    {
        return RecipeCatalog.All
            .Select(recipe => Row(recipe, preset, providers))
            .OrderBy(row => row.Complaint.Length > 0)
            .ToList();
    }

    /// <summary>
    /// Что написать про один рецепт.
    /// </summary>
    /// <remarks>
    /// Пресета может не быть вовсе — «не запускать десинк» законный выбор.
    /// Тогда каталог остаётся перечнем без вердиктов: судить о доступности
    /// не по чему, а выдумывать отказ значило бы пугать человека тем, чего
    /// он не просил.
    /// </remarks>
    private static CatalogRow Row(
        CatalogRecipe recipe,
        ZapretPreset? preset,
        IReadOnlyDictionary<string, string> providers)
    {
        var check = preset is null
            ? null
            : RecipeCatalog.Check(recipe, preset, providers);

        return new CatalogRow
        {
            Id = recipe.Id,
            Title = recipe.Title,
            What = recipe.What,
            Note = recipe.Note,
            Detail = recipe.Detail,
            Complaint = check is null || check.Runnable ? string.Empty : check.Complaint,

            // Предлагается ровно один модуль за раз, и только когда дело
            // в нём одном. Не хватает заодно образца — дописанная строка
            // рецепт не оживит, а кнопка это пообещает.
            Missing = check is { Runnable: false, MissingBlobs.Count: 0, MissingModules: [var one] }
                ? one
                : null,
        };
    }
}
