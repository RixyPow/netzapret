using NetZapret.Gui.Views;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Строки каталога рецептов на вкладке «Десинк».
/// </summary>
public sealed class CatalogRowsTests
{
    private static ZapretPreset Preset(params string[] header) => new()
    {
        Name = "проверочный",
        FilePath = "нет",
        GlobalArguments = header,
        Sections = [],
    };

    private static readonly ZapretPreset Bare = Preset(
        "--lua-init=@lua/zapret-lib.lua",
        "--lua-init=@lua/zapret-antidpi.lua",
        "--blob=tls_google:@bin/tls_clienthello_www_google_com.bin",
        "--blob=tls_max:@bin/tls_clienthello_max_ru.bin");

    private static readonly Dictionary<string, string> Providers = new(StringComparer.Ordinal)
    {
        ["multidisorder"] = "zapret-antidpi.lua",
        ["fake"] = "zapret-antidpi.lua",
        ["flood_white"] = "zapret-16kb.lua",
        ["ttl_ladder"] = "zapret-16kb.lua",
    };

    [Fact]
    public void Without_a_preset_nothing_is_judged()
    {
        // «Не запускать десинк» — законный выбор. Каталог при нём остаётся
        // перечнем: судить о доступности не по чему, а выдуманный отказ
        // пугал бы человека тем, о чём он не спрашивал.
        var rows = CatalogRows.Build(preset: null, Providers);

        Assert.Equal(RecipeCatalog.All.Count, rows.Count);
        Assert.All(rows, row => Assert.Empty(row.Complaint));
        Assert.All(rows, row => Assert.Null(row.Missing));
    }

    [Fact]
    public void Runnable_recipes_come_first()
    {
        var rows = CatalogRows.Build(Bare, Providers);
        var refused = rows.Select(r => r.Complaint.Length > 0).ToList();

        // Ни одного годного после первого отказа: вперемешку список
        // читался бы как «каталог наполовину не работает».
        Assert.Equal(refused.OrderBy(x => x), refused);
    }

    [Fact]
    public void A_refused_recipe_says_what_is_missing()
    {
        var row = CatalogRows.Build(Bare, Providers)
            .Single(r => r.Id == "flood-white");

        Assert.Contains("zapret-16kb.lua", row.Complaint);
        Assert.Equal("zapret-16kb.lua", row.Missing);
    }

    [Fact]
    public void The_fix_button_shows_only_where_it_would_help()
    {
        // Модуля нет и на диске — дописанная строка рецепт не оживит.
        // Кнопка обещала бы починку, которой не будет.
        var rows = CatalogRows.Build(Bare, providers: new Dictionary<string, string>());

        Assert.All(rows, row => Assert.Null(row.Missing));
        Assert.All(rows, row => Assert.NotEmpty(row.Complaint));
    }

    [Fact]
    public void A_missing_sample_is_not_offered_a_module()
    {
        // Не хватает заодно образца — одним модулем делу не помочь.
        var noBlobs = Preset(
            "--lua-init=@lua/zapret-lib.lua",
            "--lua-init=@lua/zapret-antidpi.lua");

        var row = CatalogRows.Build(noBlobs, Providers)
            .Single(r => r.Id == "flood-white");

        Assert.Null(row.Missing);
        Assert.Contains("образец", row.Complaint);
    }

    [Fact]
    public void A_runnable_recipe_carries_no_complaint_and_no_button()
    {
        var row = CatalogRows.Build(Bare, Providers)
            .Single(r => r.Id == "multidisorder");

        Assert.Empty(row.Complaint);
        Assert.Null(row.Missing);
        Assert.NotEmpty(row.Detail);
    }
}
