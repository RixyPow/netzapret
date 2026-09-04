using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Списки принадлежат нам, и правила ссылаются только на них.
/// </summary>
/// <remarks>
/// Требование звучит как чистоплюйство, а держится на двух измеренных бедах.
/// Правило, ссылающееся на файл чужой установки, без неё не совпадает
/// ни с чем — молча, оставаясь на вид живым; и состав такого списка задаёт
/// не тот, кто отвечает за последствия.
/// </remarks>
public sealed class OwnListsTests
{
    /// <summary>
    /// Корень установки: каталог, в котором лежит <c>config</c>.
    /// </summary>
    /// <remarks>
    /// Тот же поиск, что делает при запуске сама программа. Тест идёт
    /// от каталога сборки вверх, потому что запускается он не оттуда,
    /// где живут файлы.
    /// </remarks>
    private static string? Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "config", "lists")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void EveryListTheCatalogNamesActuallyExists()
    {
        var root = Root();

        Assert.True(root is not null, "Рабочая копия не найдена: config/lists не лежит ни в одном каталоге выше.");

        var missing = ServiceCatalog.All
            .SelectMany(service => service.Parts.Select(part => (service.Name, part.Name, part.List)))
            .Where(x => !File.Exists(Path.Combine(root!, x.List.Replace('/', Path.DirectorySeparatorChar))))
            .Select(x => $"{x.Item1} · {x.Item2}: {x.List}")
            .ToList();

        Assert.True(missing.Count == 0, "Каталог ссылается на файлы, которых нет:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void NoListIsEmpty()
    {
        var root = Root();

        Assert.True(root is not null, "Рабочая копия не найдена: config/lists не лежит ни в одном каталоге выше.");

        // Пустой список хуже отсутствующего: отсутствующий заметен по жалобе
        // загрузчика, а пустой загружается успешно и не совпадает ни с чем.
        var empty = ServiceCatalog.All
            .SelectMany(service => service.Parts)
            .Select(part => part.List)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(list =>
            {
                var path = Path.Combine(root!, list.Replace('/', Path.DirectorySeparatorChar));

                return !File.Exists(path) || !File.ReadAllLines(path).Any(line =>
                {
                    var trimmed = line.Trim();
                    return trimmed.Length > 0 && !trimmed.StartsWith('#');
                });
            })
            .ToList();

        Assert.True(empty.Count == 0, "Пустые списки: " + string.Join(", ", empty));
    }

    [Fact]
    public void TheMainNameComesFirst()
    {
        var root = Root();

        Assert.True(root is not null, "Рабочая копия не найдена: config/lists не лежит ни в одном каталоге выше.");

        // Первая строка попадает в быструю проверку, и она должна быть тем
        // именем, по которому сервис узнают. Проверяется на тех, где основной
        // домен известен наверняка; остальные списки в тест не тянутся, чтобы
        // он не превратился в перепись каталога.
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["config/lists/discord.txt"] = "discord.com",
            ["config/lists/youtube.txt"] = "youtube.com",
            ["config/lists/telegram.txt"] = "telegram.org",
            ["config/lists/whatsapp.txt"] = "whatsapp.com",
            ["config/lists/roblox.txt"] = "roblox.com",
            ["config/lists/steam.txt"] = "steampowered.com",
            ["config/lists/github.txt"] = "github.com",
            ["config/lists/itch.txt"] = "itch.io",
            ["config/lists/twitter.txt"] = "x.com",
            ["config/lists/rutracker.txt"] = "rutracker.org",
        };

        foreach (var (list, main) in expected)
        {
            var path = Path.Combine(root!, list.Replace('/', Path.DirectorySeparatorChar));

            var first = File.ReadAllLines(path)
                .Select(line => line.Trim())
                .First(line => line.Length > 0 && !line.StartsWith('#'));

            Assert.Equal(main, first);
        }
    }

    [Fact]
    public void ItchZoneIsBackInTheList()
    {
        var root = Root();

        Assert.True(root is not null, "Рабочая копия не найдена: config/lists не лежит ни в одном каталоге выше.");

        // Отдельным тестом, потому что убрал я его сам, приняв за зеркало.
        // Это раздача файлов и картинок самого itch.io, и без неё отчёт
        // про него наполовину слеп.
        var itch = File.ReadAllLines(Path.Combine(root!, "config", "lists", "itch.txt"));

        Assert.Contains("itch.zone", itch);
    }

    [Fact]
    public void AnOldReferenceToAZapretListIsUnderstood()
    {
        // Файл rules.user.yaml переживает обновление намеренно, и записи вида
        // lists/whatsapp.txt остались бы в нём навсегда. Работать они
        // продолжали бы, а каталог сравнивает значение правила со своим путём —
        // и часть числилась бы ненаправленной при живом правиле.
        var rules = RuleSetLoader.LoadRawRules(Written("""
            rules:
              - match: hostlist
                value: "lists/whatsapp.txt"
                mode: proxy
            """));

        Assert.Equal("config/lists/whatsapp.txt", rules.Single().Value);
    }

    [Fact]
    public void AReferenceToSomeoneElsesListIsLeftAlone()
    {
        // Переписывается только то, что у нас действительно есть. Ссылка
        // на чужой список указывает ровно туда, куда написана, и подмена
        // превратила бы её в ссылку в никуда.
        var rules = RuleSetLoader.LoadRawRules(Written("""
            rules:
              - match: hostlist
                value: "lists/refilter-domains_all.txt"
                mode: desync
            """));

        Assert.Equal("lists/refilter-domains_all.txt", rules.Single().Value);
    }

    [Fact]
    public void TheRussianNetworkSetMovesToo()
    {
        // Сервисом оно не выражается, а переехать должно наравне с прочими:
        // правило на пропавший файл увело бы весь российский трафик в туннель.
        var rules = RuleSetLoader.LoadRawRules(Written("""
            rules:
              - match: ipset
                value: "lists/ipset-ru.txt"
                mode: direct
            """));

        Assert.Equal("config/lists/ipset-ru.txt", rules.Single().Value);
    }

    [Fact]
    public void CaptureEntriesMoveWithTheRules()
    {
        // Секция перехвата ссылается на те же файлы, и оставить её позади
        // значило бы: правило Telegram нашлось, а трафик до него не дошёл.
        var engine = RuleSetLoader.Load("""
            capture:
              - lists/ipset-telegram.txt
            rules: []
            """);

        Assert.Equal("config/lists/ipset-telegram.txt", engine.RuleSet.CaptureEntries.Single());
    }

    private static string Written(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"netzapret-rules-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);

        return path;
    }
}
