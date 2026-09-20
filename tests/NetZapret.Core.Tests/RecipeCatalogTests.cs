using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Каталог рецептов и то, потянет ли их пресет.
/// </summary>
/// <remarks>
/// <para>
/// Половина проверок здесь сверяет каталог с настоящими модулями Lua
/// и настоящими пресетами, а не с придуманными. Это намеренно. Цена
/// ошибки — не кривая строка в окне: winws2 на неизвестный приём
/// отвечает «desync function does not exist» и не поднимается вовсе,
/// то есть один опечатанный рецепт оставляет человека без десинка целиком.
/// </para>
/// <para>
/// Проверено на себе дважды. В примерах zapret-16kb.lua стоит
/// <c>blob=bin_max</c>, а тот же файл объявлен в наших пресетах как
/// <c>tls_max</c>; и <c>hostfakesplit_stealth</c> без <c>mode</c> — это
/// не скрытный режим, а обычный.
/// </para>
/// </remarks>
public sealed class RecipeCatalogTests
{
    private const string Header = """
        --wf-tcp-out=80,443
        --lua-init=@lua/zapret-lib.lua
        --lua-init=@lua/zapret-antidpi.lua
        --blob=tls_google:@bin/tls_clienthello_www_google_com.bin
        --blob=tls_max:@bin/tls_clienthello_max_ru.bin
        """;

    private static ZapretPreset Preset(string header) => new()
    {
        Name = "проверочный",
        FilePath = "нет",
        GlobalArguments = header.Split('\n').Select(l => l.TrimEnd('\r')).ToList(),
        Sections = [],
    };

    private static readonly Dictionary<string, string> Providers = new(StringComparer.Ordinal)
    {
        ["multidisorder"] = "zapret-antidpi.lua",
        ["fake"] = "zapret-antidpi.lua",
        ["flood_white"] = "zapret-16kb.lua",
    };

    [Fact]
    public void Every_recipe_has_its_own_name()
    {
        // Имя ложится в правило. Два рецепта под одним именем означали бы,
        // что сохранённый выбор указывает неизвестно на какой из них.
        var names = RecipeCatalog.All.Select(r => r.Id).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_recipe_says_what_it_does()
    {
        Assert.All(RecipeCatalog.All, recipe =>
        {
            Assert.NotEmpty(recipe.Steps);
            Assert.False(string.IsNullOrWhiteSpace(recipe.Title));
            Assert.False(string.IsNullOrWhiteSpace(recipe.What));
        });
    }

    [Fact]
    public void A_recipe_names_the_techniques_and_samples_it_uses()
    {
        var recipe = new CatalogRecipe
        {
            Id = "проба",
            Title = "проба",
            What = "проба",
            Steps = ["fake:blob=tls_google:repeats=6", "multidisorder:pos=1,host+2:seqovl=1"],
        };

        Assert.Equal(["fake", "multidisorder"], recipe.Functions);
        Assert.Equal(["tls_google"], recipe.Blobs);
    }

    [Fact]
    public void A_sample_named_in_seqovl_pattern_counts_too()
    {
        // Ссылка на блоб бывает не только через blob=. Пропусти её —
        // и рецепт объявят годным, а движок не поднимется.
        var recipe = new CatalogRecipe
        {
            Id = "проба",
            Title = "проба",
            What = "проба",
            Steps = ["tls_multisplit_sni:seqovl=652:seqovl_pattern=tls_google"],
        };

        Assert.Equal(["tls_google"], recipe.Blobs);
    }

    [Fact]
    public void A_recipe_whose_module_is_not_connected_is_refused_by_name()
    {
        var recipe = RecipeCatalog.Find("flood-white");

        Assert.NotNull(recipe);

        var check = RecipeCatalog.Check(recipe, Preset(Header), Providers);

        Assert.False(check.Runnable);
        Assert.Equal(["zapret-16kb.lua"], check.MissingModules);
        Assert.Contains("zapret-16kb.lua", check.Complaint);
    }

    [Fact]
    public void A_recipe_whose_sample_is_missing_is_refused_too()
    {
        var recipe = new CatalogRecipe
        {
            Id = "проба",
            Title = "проба",
            What = "проба",
            Steps = ["fake:blob=tls_nosuch:repeats=6"],
        };

        var check = RecipeCatalog.Check(recipe, Preset(Header), Providers);

        Assert.False(check.Runnable);
        Assert.Equal(["tls_nosuch"], check.MissingBlobs);
        Assert.Empty(check.MissingModules);
    }

    [Fact]
    public void A_connected_module_with_a_declared_sample_is_runnable()
    {
        var recipe = RecipeCatalog.Find("fake-multidisorder");

        Assert.NotNull(recipe);

        var check = RecipeCatalog.Check(recipe, Preset(Header), Providers);

        Assert.True(check.Runnable, check.Complaint);
        Assert.Empty(check.Complaint);
    }

    [Fact]
    public void An_unknown_technique_is_told_apart_from_an_unconnected_module()
    {
        // Разные беды и разные ответы. Модуль на диске есть — допишите
        // строку; модуля нет — дописывать нечего.
        var recipe = new CatalogRecipe
        {
            Id = "проба",
            Title = "проба",
            What = "проба",
            Steps = ["nosuchfunc"],
        };

        var check = RecipeCatalog.Check(recipe, Preset(Header), Providers);

        Assert.Equal(["nosuchfunc"], check.MissingFunctions);
        Assert.Empty(check.MissingModules);
        Assert.Contains("движок не знает", check.Complaint);
    }

    [Fact]
    public void Nothing_is_found_by_an_empty_name()
    {
        Assert.Null(RecipeCatalog.Find(null));
        Assert.Null(RecipeCatalog.Find("  "));
        Assert.Null(RecipeCatalog.Find("такого нет"));
    }

    [Fact]
    public void Recipes_that_forge_packets_are_marked()
    {
        // Признак нужен соседству с туннелем: под TUN ломаются именно
        // подделки, а чистые разрезы его переживают.
        Assert.False(RecipeCatalog.Find("multidisorder")!.Fakes);
        Assert.True(RecipeCatalog.Find("fake-multidisorder")!.Fakes);
        Assert.True(RecipeCatalog.Find("flood-white")!.Fakes);
    }
}
