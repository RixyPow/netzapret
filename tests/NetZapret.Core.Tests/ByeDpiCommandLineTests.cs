using System.Text.RegularExpressions;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Корень Zapret из репозитория — как его соберёт <c>android/build.ps1</c>:
/// списки и образцы Zapret GUI из <c>vendor/zapret-gui</c>, свои — из
/// <c>config/</c> с приставкой <c>nz-</c>. Копируется только то, что называют
/// пресеты, — реестр блокировок в том числе.
/// </summary>
public sealed class ZapretRootFixture : IDisposable
{
    public string? Repository { get; }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"netzapret-zapret-{Guid.NewGuid():N}");

    public ZapretRootFixture()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
            {
                Repository = directory.FullName;
                break;
            }
        }

        Directory.CreateDirectory(Root);

        if (Repository is null)
            return;

        var reference = new Regex(@"(?:^|[=,@])((?:lists|bin)/[^\s,:]+)");
        var vendor = Path.Combine(Repository, "vendor", "zapret-gui");

        foreach (var preset in Directory.EnumerateFiles(Path.Combine(Repository, "presets"), "*.txt"))
        {
            foreach (var line in File.ReadLines(preset).Where(l => !l.TrimStart().StartsWith('#')))
            {
                foreach (Match match in reference.Matches(line))
                {
                    var path = match.Groups[1].Value;
                    var name = Path.GetFileName(path);
                    var source = name.StartsWith("nz-", StringComparison.Ordinal)
                        ? Path.Combine(Repository, "config", path.StartsWith("bin/", StringComparison.Ordinal) ? "blobs" : "lists", name["nz-".Length..])
                        : Path.Combine(vendor, path.Replace('/', Path.DirectorySeparatorChar));
                    var target = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));

                    if (File.Exists(source) && !File.Exists(target))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(source, target);
                    }
                }
            }
        }
    }

    public string Preset(string name) => Path.Combine(Repository!, "presets", name + ".txt");

    public IEnumerable<string> Presets() =>
        Repository is null ? [] : Directory.EnumerateFiles(Path.Combine(Repository, "presets"), "*.txt").OrderBy(p => p);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// Перевод пресета в группы ByeDPI — десинк телефона без root (<c>byedpi.md</c> §3–4).
/// </summary>
public sealed class ByeDpiCommandLineTests : IClassFixture<ZapretRootFixture>, IDisposable
{
    private const int Port = 41234;

    private readonly ZapretRootFixture _zapret;
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"netzapret-byedpi-{Guid.NewGuid():N}");

    public ByeDpiCommandLineTests(ZapretRootFixture zapret)
    {
        _zapret = zapret;
        Directory.CreateDirectory(_work);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ByeDpiRequest Request(string preset, ByeDpiStrategy strategy = ByeDpiStrategy.Preset) => new()
    {
        PresetPath = preset,
        ZapretRoot = _zapret.Root,
        WorkDirectory = "/data/user/0/org.netzapret/files/runtime/byedpi",
        Port = Port,
        Strategy = strategy,
    };

    private ByeDpiPlan? V12Lite(Func<ByeDpiRequest, ByeDpiRequest>? change = null)
    {
        if (_zapret.Repository is null)
            return null;

        var request = Request(_zapret.Preset("Universal V12 Lite"));
        return ByeDpiCommandLine.Build(change is null ? request : change(request));
    }

    /// <summary>Группы строки: ByeDPI начинает новую на каждом <c>-A</c>.</summary>
    private static List<List<string>> Groups(IReadOnlyList<string> arguments)
    {
        var groups = new List<List<string>> { new() };

        for (int i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == "-A")
            {
                groups.Add([]);
                i++;
                continue;
            }

            groups[^1].Add(arguments[i]);
        }

        return groups;
    }

    private static bool HasPair(IReadOnlyList<string> words, string key, string value) =>
        Enumerable.Range(0, Math.Max(0, words.Count - 1)).Any(i => words[i] == key && words[i + 1] == value);

    [Fact]
    public void V12LiteFitsIntoTheGroupsByeDpiCanReach()
    {
        if (V12Lite() is not { } plan)
            return;

        // Пустая группа в конце — её ByeDPI дописывает сам.
        int groups = Groups(plan.Arguments).Count + 1;

        Assert.True(groups <= ByeDpiCommandLine.GroupLimit, $"групп {groups}");
        Assert.True(groups <= 64);

        // Склейка действительно склеивает: секций TCP в V12 Lite больше шестидесяти.
        Assert.True(groups < 40, $"групп {groups}");
    }

    [Fact]
    public void EveryGroupWithAListIsTcpOnly()
    {
        if (V12Lite() is not { } plan)
            return;

        var groups = Groups(plan.Arguments);

        // Голова — слушатель и общие ключи — живёт в первой группе.
        foreach (var group in groups.Where(g => g.Contains("-H")))
            Assert.True(HasPair(group, "-K", "t,h"), string.Join(' ', group));

        // Без UDP групп UDP нет вовсе.
        Assert.DoesNotContain(groups, g => HasPair(g, "-K", "u"));
    }

    [Fact]
    public void ListFilesHoldOnlyNamesByeDpiAccepts()
    {
        if (V12Lite() is not { } plan)
            return;

        Assert.NotEmpty(plan.Files);

        var name = new Regex("^[a-z0-9.-]+$");
        var address = new Regex(@"^[0-9a-f.:]+/\d+$");

        foreach (var (file, content) in plan.Files)
        {
            Assert.False(string.IsNullOrEmpty(content), file);
            Assert.DoesNotContain('\r', content);
            Assert.EndsWith("\n", content);

            var lines = content.TrimEnd('\n').Split('\n');
            Assert.Equal(lines.Length, lines.Distinct().Count());

            foreach (var line in lines)
            {
                Assert.DoesNotContain("#", line);
                Assert.Matches(file.EndsWith("-hosts.txt", StringComparison.Ordinal) ? name : address, line);
            }
        }
    }

    [Fact]
    public void ListArgumentsPointIntoTheWorkDirectory()
    {
        if (V12Lite() is not { } plan)
            return;

        var arguments = plan.Arguments;
        var named = new List<string>();

        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] is "-H" or "-j")
            {
                var path = arguments[i + 1];
                var file = Path.GetFileName(path);

                Assert.StartsWith("/data/user/0/org.netzapret/files/runtime/byedpi", path);
                Assert.True(plan.Files.ContainsKey(file), file);
                named.Add(file);
            }
        }

        Assert.Equal(plan.Files.Keys.OrderBy(k => k), named.OrderBy(k => k));
    }

    [Fact]
    public void ArgumentsStartWithTheListenerAndEndWithFallbacks()
    {
        if (V12Lite() is not { } plan)
            return;

        Assert.Equal(
            ["-i", "socks5://127.0.0.1", "-p", "41234", "-N", "-c", "1024", "-T", "3"],
            plan.Arguments.Take(9));

        var fallbacks = ByeDpiCommandLine.Fallbacks.Split(' ').Append("-U").ToList();
        Assert.Equal(fallbacks, plan.Arguments.TakeLast(fallbacks.Count));

        // Секция 0 (до первого --new) не потерялась: git.zapret.moe — первая группа.
        Assert.Contains("git.zapret.moe", plan.Files["g00-hosts.txt"].Split('\n'));
    }

    /// <summary>
    /// Имена pass — напрямую, кроме тех, что забирает секция с рецептом выше.
    /// </summary>
    [Fact]
    public void PassHostsAreThePassNamesNobodyAboveTakes()
    {
        if (V12Lite() is not { } plan)
            return;

        Assert.Contains("riotgames.com", plan.PassHosts);
        Assert.Contains("valorant.com", plan.PassHosts);
        Assert.Contains("apple.com", plan.PassHosts);
        Assert.Contains("icloud.com", plan.PassHosts);

        // GitHub: «Выбрано вручную: github» с рецептом стоит над секцией GitHub
        // с pass и забирает github.com — прямой маршрут отнял бы у него рецепт.
        Assert.DoesNotContain("github.com", plan.PassHosts);
        Assert.DoesNotContain("githubusercontent.com", plan.PassHosts);
        Assert.Contains("scoop.sh", plan.PassHosts);

        // Секции с рецептом — не pass.
        Assert.DoesNotContain("youtube.com", plan.PassHosts);
        Assert.DoesNotContain("discord.com", plan.PassHosts);
    }

    [Fact]
    public void Md5SigOnlyWhenTheKernelHasIt()
    {
        if (V12Lite() is not { } without || V12Lite(r => r with { Md5Sig = true }) is not { } with)
            return;

        Assert.DoesNotContain("-S", without.Arguments);
        Assert.Contains(without.Warnings, w => w.Contains("tcp_md5", StringComparison.Ordinal));

        // hostfakesplit с tcp_md5 переводится в -f — там -S и действует.
        Assert.Contains("-S", with.Arguments);
    }

    [Fact]
    public void UdpGroupsOnlyWhenUdpGoesToByeDpi()
    {
        if (V12Lite(r => r with { Udp = true }) is not { } plan)
            return;

        var groups = Groups(plan.Arguments);
        var udp = groups.Where(g => HasPair(g, "-K", "u")).ToList();

        Assert.NotEmpty(udp);
        Assert.DoesNotContain("-U", plan.Arguments);

        // UDP по именам ByeDPI не отбирает: у групп UDP — только адреса.
        Assert.DoesNotContain(udp, g => g.Contains("-H"));
        Assert.Equal(ByeDpiCommandLine.UdpGroup.Split(' ').Skip(2), plan.Arguments.TakeLast(3));
    }

    [Fact]
    public void UntranslatableSectionsAreNamed()
    {
        if (V12Lite() is not { } plan)
            return;

        // send + syndata без root нет: группа пустая, и об этом сказано.
        Assert.Contains(plan.Warnings, w => w.Contains("discord.media", StringComparison.Ordinal) && w.Contains("syndata", StringComparison.Ordinal));
    }

    [Fact]
    public void DefaultStrategyIsTheDefaultLineVerbatim()
    {
        if (_zapret.Repository is null)
            return;

        var request = Request(_zapret.Preset("Universal V12 Lite"), ByeDpiStrategy.Default);
        var udp = ByeDpiCommandLine.Build(request with { Udp = true });
        var noUdp = ByeDpiCommandLine.Build(request);

        var expected = new List<string> { "-i", "socks5://127.0.0.1", "-p", "41234" };
        expected.AddRange(ByeDpiCommandLine.DefaultStrategy.Split(' '));

        Assert.Equal(expected, udp.Arguments);
        Assert.Equal(expected.Append("-U"), noUdp.Arguments);
        Assert.Empty(udp.Files);

        Assert.Equal(
            "-N -c 1024 -T 3 -K t,h -o1 -r-5+se "
            + "-A t,s -K t,h -d1 -n www.google.com -Q rand -f-1 "
            + "-A t,s -K t,h -d1 -d3+s -s6+s -d9+s -s12+s -d15+s -s20+s -d25+s -s30+s -d35+s -r1+s "
            + "-A t,s -K t,h -q1+s -s25+s -o5+s "
            + "-A n -K u -a1",
            ByeDpiCommandLine.DefaultStrategy);

        // Имена pass пресета sing-box ведёт напрямую и при строке по умолчанию.
        Assert.Contains("riotgames.com", udp.PassHosts);
    }

    [Fact]
    public void CustomLineKeepsOurListenerAndNoDomain()
    {
        var request = new ByeDpiRequest
        {
            PresetPath = Path.Combine(_work, "нет.txt"),
            ZapretRoot = _work,
            WorkDirectory = _work,
            Port = Port,
            Strategy = ByeDpiStrategy.Custom,
            CustomArguments = "-i 0.0.0.0 --port=1080 -Kt,h -o1 -r-5+se -S -H:\"ggpht.com googlevideo.com\" -An -d1 --ip=::",
        };

        var plan = ByeDpiCommandLine.Build(request);

        Assert.Equal(["-i", "socks5://127.0.0.1", "-p", "41234", "-N"], plan.Arguments.Take(5));
        Assert.DoesNotContain("0.0.0.0", plan.Arguments);
        Assert.DoesNotContain("1080", plan.Arguments);
        Assert.DoesNotContain("::", plan.Arguments);
        Assert.Single(plan.Arguments, "-i");
        Assert.Single(plan.Arguments, "-p");
        Assert.DoesNotContain("-S", plan.Arguments);
        Assert.True(HasPair(plan.Arguments, "-K", "t,h"));
        Assert.True(HasPair(plan.Arguments, "-H", ":ggpht.com googlevideo.com"));
        Assert.True(HasPair(plan.Arguments, "-A", "n"));
        Assert.Equal("-U", plan.Arguments[^1]);

        var md5 = ByeDpiCommandLine.Build(request with { Md5Sig = true, Udp = true });
        Assert.Contains("-S", md5.Arguments);
        Assert.DoesNotContain("-U", md5.Arguments);
    }

    [Fact]
    public void NormalizeCleansWhatByeDpiWouldMisread()
    {
        var text = ByeDpiCommandLine.Normalize(
        [
            "# Список: см. github.com",
            "Example.COM",
            "*.wild.example.org   # комментарий после имени",
            ".lead.example.net",
            "trailing.example.",
            "под_черк.example.com",
            "_dmarc.example.com",
            "scontent-*.fbcdn.net",
            "пример.рф",
            "example.com",
            "",
            "\ttab.example\tsecond.example",
        ]);

        Assert.Equal(
            "example.com\nwild.example.org\nlead.example.net\ntrailing.example\nxn--e1afmkfd.xn--p1ai\ntab.example\nsecond.example\n",
            text);

        Assert.Equal(string.Empty, ByeDpiCommandLine.Normalize(["# только комментарий"]));
    }

    [Fact]
    public void AddressesBecomeNetworks()
    {
        var text = ByeDpiCommandLine.NormalizeAddresses(
        [
            "# адреса",
            "1.2.3.4",
            "10.1.2.3/8",
            "0.0.0.0/0",
            "2001:db8::1/32",
            "not-an-address",
            "1.2.3.4/32",
        ]);

        Assert.Equal("1.2.3.4/32\n10.0.0.0/8\n2001:db8::/32\n", text);
    }

    [Fact]
    public void PunycodeWithoutIcu()
    {
        Assert.Equal("e1afmkfd", ByeDpiCommandLine.Punycode("пример"));
        Assert.Equal("e1afmkfd", ByeDpiCommandLine.Punycode("ПРИМЕР"));
        Assert.Equal("p1ai", ByeDpiCommandLine.Punycode("рф"));
        Assert.Equal("bcher-kva", ByeDpiCommandLine.Punycode("bücher"));
        Assert.Equal("80aswg", ByeDpiCommandLine.Punycode("сайт"));

        // Свой кодировщик и IdnMapping дают одно и то же.
        var idn = new System.Globalization.IdnMapping();

        foreach (var label in new[] { "москва", "испытание", "münchen", "日本語" })
            Assert.Equal(idn.GetAscii(label), "xn--" + ByeDpiCommandLine.Punycode(label));
    }

    /// <summary>
    /// Склейка не меняет, кто заберёт имя: секция поднимается к группе выше,
    /// только если между ними никто другой это имя не забирает.
    /// </summary>
    [Fact]
    public void MergeKeepsTheFirstMatch()
    {
        Directory.CreateDirectory(Path.Combine(_work, "lists"));
        File.WriteAllText(Path.Combine(_work, "lists", "a.txt"), "a.example\n");
        File.WriteAllText(Path.Combine(_work, "lists", "b.txt"), "b.example\n");
        File.WriteAllText(Path.Combine(_work, "lists", "c.txt"), "c.example\n");
        File.WriteAllText(Path.Combine(_work, "lists", "b-under-c.txt"), "x.c.example\n");
        File.WriteAllText(Path.Combine(_work, "lists", "nets.txt"), "192.0.2.0/24\n");

        string Preset(string middle) => $"""
            --wf-tcp-out=80,443
            --new
            --name=A
            --filter-tcp=443
            --hostlist=lists/a.txt
            --lua-desync=multidisorder:pos=1,host+2
            --new
            --name=B
            --filter-tcp=443
            {middle}
            --lua-desync=hostfakesplit:host=ozon.ru
            --new
            --name=C
            --filter-tcp=443
            --hostlist=lists/c.txt
            --lua-desync=multidisorder:pos=1,midsld
            """;

        int GroupsFor(string middle)
        {
            var path = Path.Combine(_work, "preset.txt");
            File.WriteAllText(path, Preset(middle));

            var plan = ByeDpiCommandLine.Build(new ByeDpiRequest
            {
                PresetPath = path,
                ZapretRoot = _work,
                WorkDirectory = _work,
                Port = Port,
            });

            int fallbacks = ByeDpiCommandLine.Fallbacks.Split(' ').Count(w => w == "-A");
            return Groups(plan.Arguments).Count - fallbacks;
        }

        // A и C — один перевод (многоступенчатый -d/-s), B им не мешает: две группы.
        Assert.Equal(2, GroupsFor("--hostlist=lists/b.txt"));

        // B забирает x.c.example раньше C: C поднять нельзя — три группы.
        Assert.Equal(3, GroupsFor("--hostlist=lists/b-under-c.txt"));

        // B по адресам: адрес имени заранее не известен — тоже нельзя.
        Assert.Equal(3, GroupsFor("--ipset=lists/nets.txt"));
    }

    [Fact]
    public void TooManyGroupsIsAnErrorNotSilence()
    {
        Directory.CreateDirectory(Path.Combine(_work, "lists"));
        File.WriteAllText(Path.Combine(_work, "lists", "same.txt"), "same.example\n");

        var text = new System.Text.StringBuilder("--wf-tcp-out=443\n");

        // Больше ByeDpiCommandLine.GroupLimit (64 с правкой сдвига бита ByeDPI):
        // чередование рецептов не даёт склеить соседние секции.
        for (int i = 0; i < 80; i++)
        {
            text.Append($"--new\n--name=s{i}\n--filter-tcp=443\n--hostlist=lists/same.txt\n");
            text.Append(i % 2 == 0 ? "--lua-desync=multidisorder:pos=1\n" : "--lua-desync=hostfakesplit:host=ozon.ru\n");
        }

        var path = Path.Combine(_work, "big.txt");
        File.WriteAllText(path, text.ToString());

        Assert.Throws<InvalidOperationException>(() => ByeDpiCommandLine.Build(new ByeDpiRequest
        {
            PresetPath = path,
            ZapretRoot = _work,
            WorkDirectory = _work,
            Port = Port,
        }));
    }

    [Fact]
    public void MissingPresetFallsBackToTheDefaultLine()
    {
        var plan = ByeDpiCommandLine.Build(new ByeDpiRequest
        {
            PresetPath = Path.Combine(_work, "нет.txt"),
            ZapretRoot = _work,
            WorkDirectory = _work,
            Port = Port,
            Udp = true,
        });

        Assert.Equal(ByeDpiCommandLine.DefaultStrategy.Split(' '), plan.Arguments.Skip(4));
        Assert.NotEmpty(plan.Warnings);
        Assert.Empty(plan.PassHosts);
    }
}
