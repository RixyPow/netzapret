using NetZapret.Gui.Views;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Из чего выбирают рецепт для одного имени.
/// </summary>
/// <remarks>
/// <para>
/// Жалоба владельца 21.09: «почему в подборе рецептов не появились новые?»
/// Каталог был заведён днём раньше, но подключён только к показу на вкладке
/// «Десинк»; окно выбора по-прежнему брало наборы из одного пресета.
/// </para>
/// <para>
/// Проверяется слияние двух источников. Главное здесь — что из каталога
/// берутся только годные: рецепт с неподключённым модулем winws2 не запустит
/// вовсе, и проверка ответила бы «не помогает» о приёме, которого
/// не пробовали.
/// </para>
/// </remarks>
public sealed class RecipeChoicesTests
{
    private static ZapretPreset Preset(params ZapretSection[] sections) => new()
    {
        Name = "проверочный",
        FilePath = "нет",
        GlobalArguments =
        [
            "--lua-init=@lua/zapret-lib.lua",
            "--lua-init=@lua/zapret-antidpi.lua",
            "--blob=tls_google:@bin/tls_clienthello_www_google_com.bin",
            "--blob=tls_max:@bin/tls_clienthello_max_ru.bin",
        ],
        Sections = sections,
    };

    private static ZapretSection Section(string name, params string[] steps) => new()
    {
        Name = name,
        HostListPaths = [],
        InlineDomains = ["example.com"],
        IpSetPaths = [],
        DesyncRecipes = steps,
        RawArguments = ["--filter-tcp=443"],
    };

    private static readonly Dictionary<string, string> Providers = new(StringComparer.Ordinal)
    {
        ["multidisorder"] = "zapret-antidpi.lua",
        ["fake"] = "zapret-antidpi.lua",
        ["hostfakesplit"] = "zapret-antidpi.lua",
        ["syndata"] = "zapret-antidpi.lua",
        ["tls_multisplit_sni"] = "custom_funcs.lua",
        ["hostfakesplit_stealth"] = "zapret-multishake.lua",
        ["flood_white"] = "zapret-16kb.lua",
        ["ttl_ladder"] = "zapret-16kb.lua",
    };

    [Fact]
    public void Catalogue_recipes_join_the_preset_ones()
    {
        // Ровно та жалоба: прежде здесь были только наборы пресета.
        var choices = RecipeChoices.Build(
            Preset(Section("свой", "syndata:blob=tls_google")), Providers, applied: null);

        Assert.Contains(choices, c => c.Name == "свой");
        Assert.Contains(choices, c => c.Name == "multidisorder");
        Assert.True(choices.Count > 1);
    }

    [Fact]
    public void A_recipe_whose_module_is_not_connected_is_not_offered()
    {
        // zapret-16kb.lua наш пресет не подключает. Предложить flood_white
        // значило бы обещать приём, которого движок не найдёт, — и получить
        // «не помогает» о том, что не пробовали.
        var choices = RecipeChoices.Build(Preset(), Providers, applied: null);

        Assert.DoesNotContain(choices, c => c.Name == "flood-white");
        Assert.DoesNotContain(choices, c => c.Name == "ttl-ladder");
    }

    [Fact]
    public void Connecting_the_module_makes_them_appear()
    {
        // Обратная сторона той же проверки: дело в подключении, а не
        // в том, что мы их спрятали навсегда.
        var withModule = new ZapretPreset
        {
            Name = "проверочный",
            FilePath = "нет",
            GlobalArguments =
            [
                "--lua-init=@lua/zapret-lib.lua",
                "--lua-init=@lua/zapret-antidpi.lua",
                "--lua-init=@lua/zapret-16kb.lua",
                "--blob=tls_max:@bin/tls_clienthello_max_ru.bin",
            ],
            Sections = [],
        };

        var choices = RecipeChoices.Build(withModule, Providers, applied: null);

        Assert.Contains(choices, c => c.Name == "flood-white");
    }

    [Fact]
    public void The_same_steps_are_not_offered_twice()
    {
        // Набор из каталога может дословно совпасть с набором пресета.
        // Показать оба — предложить дважды одно и то же под разными именами.
        var preset = Preset(Section(
            "свой",
            "multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1"));

        var choices = RecipeChoices.Build(preset, Providers, applied: null);
        var same = choices.Count(c => string.Join('\n', c.Steps).StartsWith("multidisorder:pos=1,host+2"));

        Assert.Equal(1, same);
    }

    [Fact]
    public void The_preset_wins_a_tie()
    {
        // При совпадении остаётся набор пресета: он проверен на деле,
        // и подпись у него говорит, в какой секции он встречается.
        var preset = Preset(Section(
            "знакомая секция",
            "multidisorder:pos=1,host+2,sld+2,sld+5,sniext+1,sniext+2,endhost-2:seqovl=1"));

        var choice = RecipeChoices.Build(preset, Providers, applied: null)
            .Single(c => c.Steps.Count == 1
                && c.Steps[0].StartsWith("multidisorder:pos=1,host+2"));

        Assert.Equal("знакомая секция", choice.Name);
    }

    [Fact]
    public void Every_choice_carries_its_own_steps()
    {
        // Ради этого всё и переделывалось. Прежде проверка искала шаги
        // по имени в пресете, и рецепта из каталога там нет вовсе —
        // она получила бы пустой набор и ответила «не помогает».
        var choices = RecipeChoices.Build(
            Preset(Section("свой", "syndata:blob=tls_google")), Providers, applied: null);

        Assert.All(choices, c => Assert.NotEmpty(c.Steps));
    }

    [Fact]
    public void What_the_preset_already_applies_is_marked()
    {
        var preset = Preset(Section("Instagram", "multidisorder:pos=1,host+2"));

        var choices = RecipeChoices.Build(preset, Providers, applied: "Instagram");

        Assert.True(choices.Single(c => c.Name == "Instagram").Current);
        Assert.All(choices.Where(c => c.Name != "Instagram"), c => Assert.False(c.Current));
    }

    [Fact]
    public void Without_known_techniques_only_the_preset_remains()
    {
        // Движок не распакован — приёмов не видно. Это не отказ, а сужение
        // выбора до заведомо рабочего: наборы пресета уже в нём стоят.
        var choices = RecipeChoices.Build(
            Preset(Section("свой", "syndata:blob=tls_google")),
            new Dictionary<string, string>(),
            applied: null);

        Assert.Single(choices);
        Assert.Equal("свой", choices[0].Name);
    }
}
