using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Сохранённый рецепт находится по обоим источникам.
/// </summary>
/// <remarks>
/// <para>
/// Жалоба владельца 21.09: выбрал рецепт из каталога, а в маршрутах
/// встало «десинк: tls-multisplit-sni — нет в пресете». Надпись была
/// не худшей частью беды: сборка конфига искала рецепт там же, не находила
/// и профиль не создавала — выбор не применялся вовсе.
/// </para>
/// <para>
/// Это тот самый отказ, что однажды уже стоил голоса Discord: правило
/// записано, в окне написано, а движок делает ничего.
/// </para>
/// </remarks>
public sealed class RecipeResolverTests
{
    private static ZapretPreset Preset(params string[] header) => new()
    {
        Name = "проверочный",
        FilePath = "нет",
        GlobalArguments = header.Length > 0 ? header :
        [
            "--lua-init=@lua/zapret-lib.lua",
            "--lua-init=@lua/zapret-antidpi.lua",
            "--lua-init=@lua/custom_funcs.lua",
            "--blob=tls_google:@bin/tls_clienthello_www_google_com.bin",
            "--blob=tls_max:@bin/tls_clienthello_max_ru.bin",
        ],
        Sections =
        [
            new ZapretSection
            {
                Name = "Instagram",
                HostListPaths = [],
                InlineDomains = ["instagram.com"],
                IpSetPaths = [],
                DesyncRecipes = ["multidisorder:pos=1,host+2"],
                RawArguments = ["--filter-tcp=443"],
            },
        ],
    };

    private static readonly Dictionary<string, string> Providers = new(StringComparer.Ordinal)
    {
        ["multidisorder"] = "zapret-antidpi.lua",
        ["tls_multisplit_sni"] = "custom_funcs.lua",
        ["flood_white"] = "zapret-16kb.lua",
    };

    [Fact]
    public void A_recipe_chosen_from_the_catalogue_is_found()
    {
        // Ровно жалоба. Прежде здесь было «не найден».
        var found = RecipeResolver.Find(Preset(), "tls-multisplit-sni", Providers);

        Assert.True(found.Found);
        Assert.Equal("каталог", found.Source);
        Assert.NotEmpty(found.Steps);
    }

    [Fact]
    public void A_section_of_the_preset_is_still_found()
    {
        var found = RecipeResolver.Find(Preset(), "Instagram", Providers);

        Assert.True(found.Found);
        Assert.Contains("проверочный", found.Source);
        Assert.Equal(["multidisorder:pos=1,host+2"], found.Steps);
    }

    [Fact]
    public void The_preset_is_asked_first()
    {
        // Имя из каталога, совпавшее с именем секции, должно достаться
        // секции: она описывает то, что на этой машине вправду настроено.
        var preset = new ZapretPreset
        {
            Name = "проверочный",
            FilePath = "нет",
            GlobalArguments = ["--lua-init=@lua/zapret-antidpi.lua"],
            Sections =
            [
                new ZapretSection
                {
                    Name = "multidisorder",
                    HostListPaths = [],
                    InlineDomains = ["example.com"],
                    IpSetPaths = [],
                    DesyncRecipes = ["fake:blob=tls_google"],
                    RawArguments = ["--filter-tcp=443"],
                },
            ],
        };

        var found = RecipeResolver.Find(preset, "multidisorder", Providers);

        Assert.Equal(["fake:blob=tls_google"], found.Steps);
    }

    [Fact]
    public void A_recipe_needing_an_unconnected_module_is_named_as_such()
    {
        // Беда особого рода: шаги известны, но winws2 с ними не поднимется
        // вовсе — и без десинка останется не одно имя, а все. Отличать её
        // от «не найден» обязательно: лечится она подключением модуля.
        var found = RecipeResolver.Find(Preset(), "flood-white", Providers);

        Assert.True(found.Found);
        Assert.Equal(["zapret-16kb.lua"], found.MissingModules);
    }

    [Fact]
    public void A_connected_module_leaves_no_complaint()
    {
        var found = RecipeResolver.Find(
            Preset(
                "--lua-init=@lua/zapret-antidpi.lua",
                "--lua-init=@lua/zapret-16kb.lua",
                "--blob=tls_max:@bin/tls_clienthello_max_ru.bin"),
            "flood-white",
            Providers);

        Assert.True(found.Found);
        Assert.Empty(found.MissingModules);
    }

    [Fact]
    public void Without_known_techniques_no_shortage_is_claimed()
    {
        // Движка рядом нет, приёмы читать неоткуда. Объявлять нехватку,
        // которую не проверяли, нельзя: она отправила бы подключать модуль,
        // который, быть может, давно подключён.
        var found = RecipeResolver.Find(Preset(), "flood-white", providers: null);

        Assert.True(found.Found);
        Assert.Empty(found.MissingModules);
    }

    [Fact]
    public void An_unknown_name_is_found_nowhere()
    {
        Assert.False(RecipeResolver.Find(Preset(), "такого нет", Providers).Found);
        Assert.False(RecipeResolver.Find(Preset(), null, Providers).Found);
        Assert.False(RecipeResolver.Find(Preset(), "   ", Providers).Found);
    }

    [Fact]
    public void Every_catalogue_recipe_resolves_by_its_own_name()
    {
        // Иначе выбор из каталога применился бы не у всех, а у некоторых,
        // и разница вскрылась бы только на живой машине.
        var preset = Preset();

        Assert.All(RecipeCatalog.All, recipe =>
            Assert.True(
                RecipeResolver.Find(preset, recipe.Id, Providers).Found,
                $"не нашёлся: {recipe.Id}"));
    }
}
