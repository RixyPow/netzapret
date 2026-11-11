using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Свой рецепт для имён YouTube уносит шаг YT FIX секции, которую подменяет (11.11:
/// у человека из Тулы свои рецепты для googlevideo и youtube молча выключали YT FIX).
/// </summary>
public sealed class PresetCompanionsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-companions-{Guid.NewGuid():N}");

    private const string Preset = """
        #name=Проба
        --wf-tcp=80,443

        --new
        --name=googlevideo.com (CDN сервера)
        --filter-tcp=80,443
        --hostlist=lists/googlevideo.txt
        --lua-desync=nz_first2:reset:far=35
        --lua-desync=multidisorder:pos=1,host+2

        --new
        --name=Сайт
        --filter-tcp=80,443
        --hostlist=lists/site.txt
        --lua-desync=split:pos=2

        --new
        --name=Узлы видео Google
        --filter-tcp=443
        --ipset-ip=74.125.0.0/16
        --lua-desync=nz_first2:syn:far=35
        """;

    private ZapretPreset Load()
    {
        Directory.CreateDirectory(Path.Combine(_root, "lists"));
        File.WriteAllText(Path.Combine(_root, "lists", "googlevideo.txt"), "googlevideo.com" + Environment.NewLine);
        File.WriteAllText(Path.Combine(_root, "lists", "site.txt"), "example.com" + Environment.NewLine);

        var path = Path.Combine(_root, "preset.txt");
        File.WriteAllText(path, Preset);

        return new PresetReader().Load(path);
    }

    [Fact]
    public void ARecipeForVideoNodesCarriesTheNameStepBeforeItself()
    {
        var companions = PresetCompanions.ForDomains(Load(), _root, ["googlevideo.com"]);

        Assert.Equal(["nz_first2:reset:far=35"], companions);
        Assert.Equal(
            ["nz_first2:reset:far=35", "fake:blob=tls_google"],
            PresetCompanions.With(companions, ["fake:blob=tls_google"]));
    }

    [Fact]
    public void OtherNamesGetNothing()
    {
        var companions = PresetCompanions.ForDomains(Load(), _root, ["example.com"]);

        Assert.Empty(companions);
        Assert.Equal(["split:pos=3"], PresetCompanions.With(companions, ["split:pos=3"]));
    }

    /// <summary>«Не трогать» обещает, что имя не тронет никто; рецепт самой секции шаг уже несёт.</summary>
    [Fact]
    public void PassAndTheSectionsOwnRecipeAreLeftAsTheyAre()
    {
        var companions = PresetCompanions.ForDomains(Load(), _root, ["googlevideo.com"]);

        Assert.Equal([RecipeCatalog.Pass], PresetCompanions.With(companions, [RecipeCatalog.Pass]));
        Assert.Equal(
            ["nz_first2:reset:far=35", "multidisorder:pos=1,host+2"],
            PresetCompanions.With(companions, ["nz_first2:reset:far=35", "multidisorder:pos=1,host+2"]));
        Assert.Empty(PresetCompanions.With(companions, []));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
