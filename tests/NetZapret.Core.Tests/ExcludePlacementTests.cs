using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Куда ставится список исключений.
/// </summary>
/// <remarks>
/// Прежде он шёл в каждую секцию подряд. На пресете из полусотни профилей это
/// пятьдесят один лишний ключ — и ровно то единственное, чем наш запуск
/// отличался от чужого на том же пресете: ни одной потерянной строки, только
/// добавленные.
///
/// Вред у лишнего ключа есть. Исключение написано именами, а секция,
/// отбирающая трафик по адресам либо по классификатору L7, имён не видит:
/// сверять там нечего. Зато профиль становится хостлистовым, а такой ждёт
/// разобранного имени — и пакет без имени может ему уже не достаться.
/// Под это попадают голосовые секции Discord: одна отбирает по stun
/// и discord, другая по ipset, и в обеих имени не бывает никогда.
/// </remarks>
public class ExcludePlacementTests
{
    private const string Preset = """
        #name=Проба
        --wf-tcp=80,443

        --new
        --name=По именам
        --filter-tcp=80,443
        --hostlist=lists/site.txt
        --lua-desync=split:pos=2

        --new
        --name=Именами прямо в строке
        --filter-tcp=443
        --hostlist-domains=example.com
        --lua-desync=split:pos=2

        --new
        --name=Голосовые звонки/чаты
        --filter-l7=stun,discord
        --lua-desync=fake:blob=quic_google

        --new
        --name=Discord UDP
        --filter-udp=443-65535
        --ipset=lists/ipset-discord.txt
        --lua-desync=fake:blob=quic_google
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

    /// <summary>Секции по адресам и по классификатору исключения не получают.</summary>
    [Fact]
    public void ExcludeGoesOnlyToSectionsThatFilterByName()
    {
        var preset = Load();

        var arguments = WinwsCommandLine.Build(preset, @"C:\runtime\desync-exclude.txt", own: []);

        // Разбираем по профилям и смотрим, у кого исключение появилось.
        var withExclude = new List<string>();
        string? name = null;
        bool seen = false;

        foreach (var a in arguments)
        {
            if (a == "--new")
            {
                if (seen && name is not null)
                    withExclude.Add(name);

                name = null;
                seen = false;

                continue;
            }

            if (a.StartsWith("--name="))
                name = a[7..];

            if (a.StartsWith("--hostlist-exclude="))
                seen = true;
        }

        if (seen && name is not null)
            withExclude.Add(name);

        Assert.Equal(["По именам", "Именами прямо в строке"], withExclude);
    }

    /// <summary>
    /// Сама проверка: хостлист либо имена в строке — да, ipset и L7 — нет.
    /// </summary>
    [Fact]
    public void OnlyHostListSectionsFilterByName()
    {
        var preset = Load();

        Assert.True(WinwsCommandLine.FiltersByName(
            preset.Sections.Single(s => s.Name == "По именам")));

        Assert.True(WinwsCommandLine.FiltersByName(
            preset.Sections.Single(s => s.Name == "Именами прямо в строке")));

        Assert.False(WinwsCommandLine.FiltersByName(
            preset.Sections.Single(s => s.Name == "Голосовые звонки/чаты")));

        Assert.False(WinwsCommandLine.FiltersByName(
            preset.Sections.Single(s => s.Name == "Discord UDP")));
    }

    /// <summary>
    /// Свой профиль исключение получает всегда: он по именам и работает.
    /// </summary>
    [Fact]
    public void OwnProfileStillGetsTheExclude()
    {
        var arguments = WinwsCommandLine.Build(
            Load(),
            @"C:\runtime\desync-exclude.txt",
            own:
            [
                new OwnDesyncProfile
                {
                    Name = "Свой",
                    Steps = ["split:pos=2"],
                    HostListPath = @"C:\runtime\desync\own.txt",
                },
            ]);

        int ours = arguments.ToList().FindIndex(a => a.Contains("NetZapret: Свой", StringComparison.Ordinal));
        int exclude = arguments.ToList().FindIndex(a => a.StartsWith("--hostlist-exclude="));

        Assert.True(ours >= 0);
        Assert.True(exclude > ours, "исключение должно попасть в свой профиль");
    }

    /// <summary>Без списка исключений строка остаётся ровно такой, как в файле.</summary>
    [Fact]
    public void WithoutExcludeNothingIsAdded()
    {
        var arguments = WinwsCommandLine.Build(Load(), excludeList: null, own: []);

        Assert.DoesNotContain(arguments, a => a.StartsWith("--hostlist-exclude="));
    }
}
