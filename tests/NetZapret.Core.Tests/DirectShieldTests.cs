using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// «Напрямую» — щит первым профилем winws2, а не только исключение
/// в секциях по именам.
/// </summary>
/// <remarks>
/// 23.09 лёг Valorant (VAL 43): Riot поставили «напрямую», исключение
/// выключило щит <c>pass</c> секции Riot, а платформу Riot на Cloudflare
/// 104.18.0.0/16 забрала секция по адресам «discord.com (IP fallback)» —
/// исключений по именам такие секции не видят.
/// </remarks>
public sealed class DirectShieldTests : IDisposable
{
    private const string ShieldName = "--name=NetZapret: не трогать";

    /// <summary>Голова как у V9: глобальные ключи и первый профиль до первого --new.</summary>
    private const string Preset = """
        #name=Проба
        --lua-init=@lua/zapret-lib.lua
        --wf-tcp-out=80,443-65535
        --blob=quic_google:@bin/quic_initial_www_google_com.bin
        --name=git.zapret.moe
        --filter-tcp=443
        --hostlist=lists/git-zapret-moe.txt
        --out-range=-d8
        --lua-desync=hostfakesplit:host=ozon.ru

        --new
        --name=Riot / Valorant TCP
        --filter-tcp=80,443-65535
        --hostlist=lists/riot-valorant.txt
        --lua-desync=pass

        --new
        --name=discord.com (IP fallback)
        --filter-tcp=443
        --ipset=lists/ipset-discord.txt
        --lua-desync=fake:blob=quic_google
        """;

    /// <summary>Ключи профиля: ни один не должен стоять раньше щита.</summary>
    private static readonly string[] ProfileKeys =
    [
        "--name=", "--filter-", "--hostlist", "--ipset", "--lua-desync=",
        "--out-range=", "--payload=", "--skip",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-shield-{Guid.NewGuid():N}");

    public DirectShieldTests() => Directory.CreateDirectory(_root);

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

    private ZapretPreset Load(string text = Preset)
    {
        var path = Path.Combine(_root, "preset.txt");
        File.WriteAllText(path, text);

        return new PresetReader().Load(path);
    }

    private string Exclude(params string[] lines)
    {
        var path = Path.Combine(_root, "desync-exclude.txt");
        File.WriteAllLines(path, lines);

        return path;
    }

    /// <summary>Командная строка по профилям: разделитель — --new.</summary>
    private static List<List<string>> Profiles(IReadOnlyList<string> arguments)
    {
        var profiles = new List<List<string>> { new() };

        foreach (var argument in arguments)
        {
            if (argument == "--new")
                profiles.Add([]);
            else
                profiles[^1].Add(argument);
        }

        return profiles;
    }

    /// <summary>
    /// Щит — первый профиль, раньше пресета и своих рецептов; первый профиль
    /// пресета переезжает за него целиком и в прежнем порядке.
    /// </summary>
    [Fact]
    public void ShieldIsTheFirstProfile()
    {
        var exclude = Exclude("playvalorant.com", "riotgames.com");

        var arguments = WinwsCommandLine.Build(
            Load(),
            exclude,
            own:
            [
                new OwnDesyncProfile
                {
                    Name = "Свой",
                    Steps = ["split:pos=2"],
                    HostListPath = Path.Combine(_root, "own.txt"),
                },
            ]);

        var profiles = Profiles(arguments);
        var shield = profiles[0];

        Assert.Contains(ShieldName, shield);
        Assert.Contains($"--hostlist={WinwsCommandLine.Forward(exclude)}", shield);
        Assert.Equal("--lua-desync=pass", Assert.Single(shield, a => a.StartsWith("--lua-desync=")));
        Assert.Contains("--filter-tcp=*", shield);
        Assert.Contains("--filter-udp=443", shield);

        // Глобальные ключи остались в голове и перед щитом.
        Assert.Equal(
            [
                "--lua-init=@lua/zapret-lib.lua",
                "--wf-tcp-out=80,443-65535",
                "--blob=quic_google:@bin/quic_initial_www_google_com.bin",
            ],
            shield.Take(3));

        Assert.Equal(
            [
                "--name=git.zapret.moe",
                "--filter-tcp=443",
                "--hostlist=lists/git-zapret-moe.txt",
                "--out-range=-d8",
                "--lua-desync=hostfakesplit:host=ozon.ru",
            ],
            profiles[1]);

        Assert.Equal("--name=NetZapret: Свой", profiles[2][0]);
        Assert.Contains("--name=discord.com (IP fallback)", profiles[^1]);
    }

    /// <summary>Пресет без профиля в голове: щит занимает её место, лишнего --new нет.</summary>
    [Fact]
    public void HeadWithoutProfileGetsNoExtraSeparator()
    {
        var preset = Load("""
            #name=Проба
            --wf-tcp=80,443

            --new
            --name=Discord UDP
            --filter-udp=443-65535
            --ipset=lists/ipset-discord.txt
            --lua-desync=fake:blob=quic_google
            """);

        var profiles = Profiles(WinwsCommandLine.Build(preset, Exclude("example.com"), own: []));

        Assert.Equal(2, profiles.Count);
        Assert.Equal("--wf-tcp=80,443", profiles[0][0]);
        Assert.Contains(ShieldName, profiles[0]);
        Assert.Contains("--name=Discord UDP", profiles[1]);
    }

    /// <summary>
    /// Пустой список — щита нет. Как winws2 читает пустой --hostlist,
    /// не проверено, а прочти он его как «все имена», pass выключил бы
    /// десинк целиком.
    /// </summary>
    [Fact]
    public void EmptyListGivesNoShield()
    {
        var preset = Load();

        foreach (var exclude in new[] { null, Exclude("# пусто", "", "  "), Path.Combine(_root, "нет.txt") })
        {
            var arguments = WinwsCommandLine.Build(preset, exclude, own: []);

            Assert.DoesNotContain(ShieldName, arguments);

            // Голова ровно как в файле — ничего не переставлено.
            Assert.Equal(preset.GlobalArguments, arguments.Take(preset.GlobalArguments.Count));
        }
    }

    /// <summary>
    /// На всех пресетах из поставки: щит первый, ничего не потеряно,
    /// добавлен только он сам и разделитель за ним.
    /// </summary>
    [Fact]
    public void EveryShippedPresetGetsTheShieldFirstAndLosesNothing()
    {
        if (Repository() is not { } root)
            return;

        var files = Directory.GetFiles(Path.Combine(root, "presets"), "*.txt");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var preset = new PresetReader().Load(file);

            // Один и тот же файл: пустой — щита нет, с именем — есть. Путь
            // тот же, и исключения в секциях совпадают до буквы.
            var exclude = Exclude("# пусто");
            var plain = WinwsCommandLine.Build(preset, exclude, own: []).ToList();

            Exclude("playvalorant.com");
            var shielded = WinwsCommandLine.Build(preset, exclude, own: []).ToList();

            int shield = shielded.IndexOf(ShieldName);
            Assert.True(shield >= 0, $"{preset.Name}: щита нет");

            var before = shielded.Take(shield).ToList();

            Assert.DoesNotContain(before, a => a == "--new");
            Assert.DoesNotContain(before, a => ProfileKeys.Any(key => a.StartsWith(key, StringComparison.Ordinal)));

            Assert.Equal(
                "--lua-desync=pass",
                shielded.First(a => a.StartsWith("--lua-desync=", StringComparison.Ordinal)));

            // Сверка по составу: убрать щит и один --new — получится то же,
            // что без щита, до последнего ключа.
            var rest = new List<string>(shielded);
            rest.RemoveRange(shield, 6);

            if (rest.Count != plain.Count)
                rest.Remove("--new");

            Assert.Equal(plain.Order(StringComparer.Ordinal), rest.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>Корень репозитория — по solution-файлу.</summary>
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
}
