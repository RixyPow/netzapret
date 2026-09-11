using NetZapret.Core.Rules;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Выбор рецепта десинка для отдельного имени.
/// </summary>
/// <remarks>
/// До него <c>mode: desync</c> означал ровно одно — «мимо туннеля». Чем чинить
/// имя, решал пресет, и если оно не попадало ни в один его список, не делалось
/// ничего. Хуже: попасть можно в секцию с рецептом <c>pass</c>, которая имя
/// забирает себе и пропускает нетронутым — ровно это и случилось
/// с api.cloudflareclient.com.
/// </remarks>
public class DesyncRecipeTests
{
    private const string Preset = """
        #name=Проба
        --wf-tcp=80,443

        --new
        --name=Cloudflare TCP
        --filter-tcp=80,443-65535
        --hostlist=lists/cloudflare.txt
        --lua-desync=pass

        --new
        --name=Claude
        --filter-tcp=80,443
        --hostlist=lists/claude.txt
        --lua-desync=fake:blob=tls_google:repeats=6
        --lua-desync=multidisorder:pos=1,host+2:seqovl=1

        --new
        --name=Notion
        --filter-tcp=80,443
        --hostlist=lists/notion.txt
        --lua-desync=fake:blob=tls_google:repeats=6
        --lua-desync=multidisorder:pos=1,host+2:seqovl=1

        --new
        --name=Discord
        --filter-tcp=80,443
        --hostlist=lists/discord.txt
        --lua-desync=split:pos=2
        """;

    private static ZapretPreset Load()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-preset-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(path, Preset);
            return new PresetReader().Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Одинаковые наборы схлопываются в один рецепт: в пресете на полсотни
    /// секций их обычно меньше десятка, и список, где сорок строк повторяют
    /// друг друга, выбирать не помогает.
    /// </summary>
    [Fact]
    public void IdenticalRecipesAreOfferedOnce()
    {
        var recipes = DesyncRecipes.FromPresetFile(Load());

        // Claude и Notion делят один набор, Discord свой, Cloudflare —
        // пропускающая и рецептом не является.
        Assert.Equal(2, recipes.Count);

        var shared = recipes.Single(r => r.UsedBy.Count > 1);

        Assert.Equal(["Claude", "Notion"], shared.UsedBy);
    }

    /// <summary>
    /// Пропускающая секция в рецепты не попадает: у неё одно назначение —
    /// не трогать, и для этого есть отдельный выбор, а не рецепт «ничего».
    /// </summary>
    [Fact]
    public void PassThroughSectionIsNotARecipe()
    {
        Assert.DoesNotContain(
            DesyncRecipes.FromPresetFile(Load()),
            recipe => recipe.Name == "Cloudflare TCP");
    }

    /// <summary>
    /// Показывается то, что рецепт делает, а не сервис, у которого его взяли.
    /// Секции пресета названы по тому, что чинят, и рецепту доставалось имя
    /// вроде «AnyDesk UDP» — при том что чинить им собрались другое имя.
    /// </summary>
    [Fact]
    public void RecipesAreTitledByWhatTheyDo()
    {
        var recipes = DesyncRecipes.FromPresetFile(Load());

        var shared = recipes.Single(r => r.UsedBy.Count > 1);
        var alone = recipes.Single(r => r.UsedBy.Count == 1);

        Assert.Equal("fake + multidisorder", shared.Title);
        Assert.Equal("split", alone.Title);

        // А хранится по-прежнему имя секции: только оно устойчиво.
        Assert.Equal("Claude", shared.Name);

        // И видно, где этот же набор применяется, — по знакомому и выбирают.
        Assert.Equal("в пресете: Claude, Notion", shared.Where);
    }

    /// <summary>
    /// Одного приёма мало, когда его делят несколько наборов: «fake»
    /// встречается у половины пресета с разными настройками.
    /// </summary>
    [Fact]
    public void SameTechniqueWithDifferentSettingsIsDisambiguated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-preset-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(path, """
                #name=Проба
                --wf-tcp=80,443

                --new
                --name=Первый
                --hostlist=lists/a.txt
                --lua-desync=fake:repeats=2

                --new
                --name=Второй
                --hostlist=lists/b.txt
                --lua-desync=fake:repeats=6
                """);

            var titles = DesyncRecipes.FromPresetFile(new PresetReader().Load(path))
                .Select(r => r.Title)
                .ToList();

            Assert.Equal(2, titles.Distinct().Count());
            Assert.All(titles, t => Assert.Contains("как у", t, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RecipeIsFoundByNameAndMissingOneIsNull()
    {
        var preset = Load();

        Assert.NotNull(DesyncRecipes.Find(preset, "Claude"));
        Assert.NotNull(DesyncRecipes.Find(preset, "claude"));
        Assert.Null(DesyncRecipes.Find(preset, "Такого нет"));
        Assert.Null(DesyncRecipes.Find(preset, DesyncRecipes.FromPreset));
    }

    /// <summary>
    /// Секции по UDP в выбор не попадают.
    /// </summary>
    /// <remarks>
    /// Выбранный рецепт проверяется рукопожатием TLS, а ему в UDP проверять
    /// нечего: приём получал «не помогает» независимо от собственных достоинств
    /// и стоял в списке наравне с работающими. У голоса Discord транспорт
    /// назван не прямо, а классификатором — <c>stun</c> и <c>discord</c> оба
    /// живут в UDP.
    /// </remarks>
    [Fact]
    public void UdpSectionsAreNotOfferedAsRecipes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-preset-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(path, """
                #name=Проба
                --wf-tcp=80,443

                --new
                --name=Сайт
                --filter-tcp=80,443
                --hostlist=lists/site.txt
                --lua-desync=split:pos=2

                --new
                --name=Голосовые звонки/чаты
                --filter-l7=stun,discord
                --lua-desync=fake:blob=quic_google:repeats=10

                --new
                --name=AnyDesk UDP
                --filter-udp=443-65535
                --lua-desync=fake:blob=quic_google:repeats=4

                --new
                --name=Без фильтра
                --hostlist=lists/other.txt
                --lua-desync=multidisorder:pos=1
                """);

            var names = DesyncRecipes.FromPresetFile(new PresetReader().Load(path))
                .SelectMany(r => r.UsedBy)
                .ToList();

            Assert.Contains("Сайт", names);

            // Фильтра нет вовсе — значит секция ловит всё подряд, TCP в том
            // числе, и проверить её рукопожатием можно.
            Assert.Contains("Без фильтра", names);

            Assert.DoesNotContain("Голосовые звонки/чаты", names);
            Assert.DoesNotContain("AnyDesk UDP", names);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// В <c>--filter-l7</c> бывает и TCP: <c>http</c> с <c>tls</c> ходят по нему,
    /// и такую секцию проверить рукопожатием как раз можно. Отбор идёт
    /// по названным протоколам, а не по самому факту классификатора.
    /// </summary>
    [Fact]
    public void TcpLevelSevenSectionStaysInTheChoice()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-preset-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(path, """
                #name=Проба
                --wf-tcp=80,443

                --new
                --name=Через классификатор
                --filter-l7=http,tls
                --hostlist=lists/site.txt
                --lua-desync=split:pos=2
                """);

            var names = DesyncRecipes.FromPresetFile(new PresetReader().Load(path))
                .SelectMany(r => r.UsedBy)
                .ToList();

            Assert.Contains("Через классификатор", names);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Свои профили обязаны стоять перед пресетовскими. winws2 отдаёт пакет
    /// первому профилю, чей фильтр совпал, и дальше не смотрит: стоя после,
    /// наш рецепт не сработал бы на именах, которые пресет уже забрал себе
    /// под «пропустить» — а это ровно тот случай, ради которого выбор и нужен.
    /// </summary>
    [Fact]
    public void OwnProfilesComeBeforeThePreset()
    {
        var arguments = WinwsCommandLine.Build(
            Load(),
            excludeList: null,
            own:
            [
                new OwnDesyncProfile
                {
                    Name = "Claude",
                    Steps = ["fake:blob=tls_google:repeats=6"],
                    HostListPath = @"C:\runtime\desync\Claude.txt",
                },
            ]);

        var line = arguments.ToList();

        int ours = line.FindIndex(a => a.Contains("NetZapret: Claude", StringComparison.Ordinal));
        int theirs = line.FindIndex(a => a.Contains("Cloudflare TCP", StringComparison.Ordinal));

        Assert.True(ours >= 0, "своего профиля нет в командной строке");
        Assert.True(theirs >= 0, "профилей пресета нет в командной строке");
        Assert.True(ours < theirs, $"свой профиль ({ours}) должен идти раньше пресетовского ({theirs})");

        // --out-range обязателен. Без него рецепт применяется к каждому
        // исходящему пакету подряд, а не только к рукопожатию: домен при этом
        // открывался в curl, а sing-box получал tls: handshake failure —
        // то есть имя открыто, а пользоваться им движок не мог.
        Assert.Contains("--out-range=-d8", line);
        Assert.True(line.IndexOf("--out-range=-d8") > ours, "--out-range должен быть внутри нашего профиля");

        // И глобальные ключи остаются впереди всех: они не принадлежат
        // ни одному профилю.
        Assert.True(line.IndexOf("--wf-tcp=80,443") < ours);
    }

    /// <summary>Профиль без шагов не выпускается: он был бы фильтром без действия.</summary>
    [Fact]
    public void ProfileWithoutStepsIsSkipped()
    {
        var arguments = WinwsCommandLine.Build(
            Load(),
            excludeList: null,
            own:
            [
                new OwnDesyncProfile
                {
                    Name = "Пустой",
                    Steps = [],
                    HostListPath = @"C:\runtime\desync\empty.txt",
                },
            ]);

        Assert.DoesNotContain(arguments, a => a.Contains("Пустой", StringComparison.Ordinal));
    }

    /// <summary>
    /// Рецепт хранится только у десинка. У прочих режимов ему нечего означать,
    /// а оставленный при смене режима он всплыл бы потом необъяснимой строкой.
    /// </summary>
    [Fact]
    public void RecipeSurvivesSavingAndOnlyForDesync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-rules-{Guid.NewGuid():N}.yaml");

        try
        {
            var file = UserRulesFile.Load(path);

            file.Set(MatchKind.Domain, "*.api.cloudflareclient.com", RoutingMode.Desync, "Claude");
            file.Set(MatchKind.Domain, "*.example.com", RoutingMode.Proxy, "Claude");
            file.Save();

            var read = UserRulesFile.Load(path);

            var desync = read.Entries.Single(e => e.Value.Contains("cloudflareclient", StringComparison.Ordinal));
            var proxy = read.Entries.Single(e => e.Value.Contains("example.com", StringComparison.Ordinal));

            Assert.Equal("Claude", desync.Recipe);
            Assert.Null(proxy.Recipe);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Списки пишутся по рецепту, а не по имени: у профиля winws2 один
    /// hostlist, и десять имён с общим рецептом должны попасть в один файл.
    /// </summary>
    [Fact]
    public void NamesAreGroupedByRecipeIntoOneListEach()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}");

        try
        {
            var profiles = WinwsCommandLine.WriteOwnLists(
                [
                    ("Claude", ["fake:blob=tls_google"], ["one.example", "two.example"]),
                    ("Discord", ["split:pos=2"], ["three.example"]),
                ],
                root);

            Assert.Equal(2, profiles.Count);

            var claude = profiles.Single(p => p.Name == "Claude");

            Assert.Equal(
                ["one.example", "two.example"],
                File.ReadAllLines(claude.HostListPath).Where(l => l.Length > 0));

            Assert.True(Path.IsPathFullyQualified(claude.HostListPath));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Имя файла — только латиница и цифры, а путь — с прямыми слэшами.
    /// </summary>
    /// <remarks>
    /// Дело не в файловой системе: Windows приняла бы и кириллицу, и скобки.
    /// Их не принимает winws2 — он собран под Cygwin, где обратный слэш это
    /// знак экранирования. На имени «googlevideo.com-(CDN-сервера).txt» путь
    /// доезжал до движка как «C:UsersrogfaProjects…», без единого разделителя,
    /// и список к профилю не прицеплялся. Снаружи это выглядело неработающим
    /// рецептом: профиль есть, имя не совпадает ни с чем.
    /// </remarks>
    [Fact]
    public void ListPathSurvivesTheEnginesArgumentParsing()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}");

        try
        {
            var profiles = WinwsCommandLine.WriteOwnLists(
                [("googlevideo.com (CDN сервера)", ["multidisorder:pos=1"], ["one.example"])],
                root);

            var name = Path.GetFileName(profiles.Single().HostListPath);

            Assert.All(name, c => Assert.True(
                char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_',
                $"в имени файла остался знак «{c}»: {name}"));

            // Списки пресета тоже приходят ключом --hostlist=, поэтому ищем
            // именно наш — по каталогу, в который его записали.
            var arguments = WinwsCommandLine.Build(Load(), null, profiles).ToList();

            var hostlist = arguments.Single(a =>
                a.StartsWith("--hostlist=", StringComparison.Ordinal)
                && a.Contains(name, StringComparison.Ordinal));

            Assert.DoesNotContain('\\', hostlist);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Имена, от которых после отсева не остаётся ничего, не сливаются
    /// в один файл: хвост считается от полного имени.
    /// </summary>
    [Fact]
    public void NamesThatLoseEverythingStayDistinct()
    {
        var first = WinwsCommandLine.Sanitize("Голосовые звонки");
        var second = WinwsCommandLine.Sanitize("Голосовые чаты");

        Assert.NotEqual(first, second);
        Assert.Equal(first, WinwsCommandLine.Sanitize("Голосовые звонки"));
    }

    /// <summary>
    /// Осиротевшие списки убираются: рецепт могли переименовать или снять,
    /// и старый файл остался бы лежать ничьим.
    /// </summary>
    [Fact]
    public void StaleListsAreRemoved()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netzapret-own-{Guid.NewGuid():N}");

        try
        {
            WinwsCommandLine.WriteOwnLists([("Старый", ["split:pos=2"], ["one.example"])], root);

            var kept = WinwsCommandLine.WriteOwnLists([("Новый", ["split:pos=2"], ["one.example"])], root);

            Assert.Equal(
                [Path.GetFileName(kept.Single().HostListPath)],
                Directory.GetFiles(root).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
