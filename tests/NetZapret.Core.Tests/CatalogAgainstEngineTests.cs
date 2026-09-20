using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Каталог сверяется с настоящими модулями и настоящими пресетами.
/// </summary>
/// <remarks>
/// <para>
/// Остальные проверки каталога работают на выдуманных пресетах — так
/// проверяют правило. Здесь наоборот: берутся файлы из репозитория,
/// те самые, что уедут в поставку. Правило может быть верным, а рецепт
/// при этом ссылаться на приём, которого в движке нет.
/// </para>
/// <para>
/// Пропускаются, если движок рядом не распакован: собрать репозиторий
/// можно и без него, и красная проверка сказала бы там не о рецептах,
/// а об отсутствии папки.
/// </para>
/// </remarks>
public sealed class CatalogAgainstEngineTests
{
    /// <summary>Корень репозитория — по solution-файлу, а не по числу «..».</summary>
    /// <remarks>
    /// Счёт «..» ломается от смены целевой платформы или конфигурации:
    /// путь сборки тогда становится на уровень длиннее или короче.
    /// </remarks>
    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string? LuaFolder()
    {
        if (Repository() is not { } root)
            return null;

        var folder = Path.Combine(root, "build", "engines", "zapret", "lua");

        return Directory.Exists(folder) ? folder : null;
    }

    private static ZapretPreset? Universal()
    {
        if (Repository() is not { } root)
            return null;

        var file = Path.Combine(root, "presets", "Universal V8.txt");

        return File.Exists(file) ? new PresetReader().Load(file) : null;
    }

    [Fact]
    public void Every_technique_in_the_catalogue_exists_in_the_engine()
    {
        if (LuaFolder() is not { } folder)
            return;

        var providers = LuaModules.Providers(LuaModules.Scan(folder));
        var unknown = new List<string>();

        foreach (var recipe in RecipeCatalog.All)
        {
            foreach (var name in recipe.Functions.Where(n => !providers.ContainsKey(n)))
                unknown.Add($"{recipe.Id}: {name}");
        }

        // winws2 на такое не жалуется в строчку — он не поднимается вовсе.
        Assert.Empty(unknown);
    }

    [Fact]
    public void Every_sample_in_the_catalogue_is_declared_by_the_default_preset()
    {
        if (Universal() is not { } preset)
            return;

        var declared = LuaModules.BlobsOf(preset);
        var missing = new List<string>();

        foreach (var recipe in RecipeCatalog.All)
        {
            foreach (var blob in recipe.Blobs.Where(b => !declared.Contains(b)))
                missing.Add($"{recipe.Id}: {blob}");
        }

        // Ровно эта проверка краснела бы на «blob=bin_max» из примеров
        // zapret-16kb.lua: у нас тот же файл зовётся tls_max.
        Assert.Empty(missing);
    }

    [Fact]
    public void The_default_preset_runs_most_of_the_catalogue()
    {
        if (LuaFolder() is not { } folder || Universal() is not { } preset)
            return;

        var providers = LuaModules.Providers(LuaModules.Scan(folder));

        var runnable = RecipeCatalog.All
            .Count(r => RecipeCatalog.Check(r, preset, providers).Runnable);

        // Не «все» намеренно: рецепты из zapret-16kb.lua Universal V8
        // не подключает, и каталог для того и нужен, чтобы это показать.
        // Не «хотя бы один» — такой порог прошёл бы и сломанный каталог.
        Assert.InRange(runnable, 5, RecipeCatalog.All.Count);
    }

    [Fact]
    public void What_the_default_preset_cannot_run_is_only_a_missing_module()
    {
        if (LuaFolder() is not { } folder || Universal() is not { } preset)
            return;

        var providers = LuaModules.Providers(LuaModules.Scan(folder));

        foreach (var recipe in RecipeCatalog.All)
        {
            var check = RecipeCatalog.Check(recipe, preset, providers);

            if (check.Runnable)
                continue;

            // Недоступность должна быть поправимой: модуль на диске есть,
            // его лишь не подключили. Неизвестный приём или необъявленный
            // образец — это наша ошибка в каталоге, а не выбор пресета.
            Assert.Empty(check.MissingFunctions);
            Assert.Empty(check.MissingBlobs);
            Assert.NotEmpty(check.MissingModules);
        }
    }

    [Fact]
    public void The_engine_offers_more_than_the_catalogue_takes()
    {
        if (LuaFolder() is not { } folder)
            return;

        var modules = LuaModules.Scan(folder);

        // Отдельной проверкой: пустая папка сделала бы три проверки выше
        // зелёными, не проверив ничего — они все начинаются с выхода
        // по «не нашли».
        Assert.True(modules.Count >= 10, $"модулей найдено {modules.Count}");
        Assert.True(LuaModules.Providers(modules).Count >= 50);
    }
}
