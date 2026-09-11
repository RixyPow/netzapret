using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Порты своего профиля берутся у секции, которую он подменяет.
/// </summary>
/// <remarks>
/// Прежде тут стояло зашитое <c>80,443</c>. Свой профиль встаёт перед
/// пресетовскими и забирает имя себе, то есть обязан покрывать то же, что
/// покрыла бы секция; стоя уже, он берёт часть трафика, а остальное проходит
/// вообще без обработки. У Discord секции объявлены на восьми портах, и пять
/// из них — запасные HTTPS у Cloudflare, куда клиент уходит сам, когда сеть
/// ведёт себя плохо, то есть ровно в наших условиях.
/// </remarks>
public class PresetPortsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"netzapret-ports-{Guid.NewGuid():N}");

    private ZapretPreset Load(string text)
    {
        Directory.CreateDirectory(Path.Combine(_root, "lists"));

        File.WriteAllText(
            Path.Combine(_root, "lists", "discord.txt"),
            "discord.com" + Environment.NewLine + "discord.gg" + Environment.NewLine);

        File.WriteAllText(
            Path.Combine(_root, "lists", "site.txt"), "example.com" + Environment.NewLine);

        var path = Path.Combine(_root, "preset.txt");
        File.WriteAllText(path, text);

        return new PresetReader().Load(path);
    }

    private const string Preset = """
        #name=Проба
        --wf-tcp=80,443

        --new
        --name=Сайт
        --filter-tcp=80,443
        --hostlist=lists/site.txt
        --lua-desync=split:pos=2

        --new
        --name=discord.com
        --filter-tcp=80,443,1080,2053,2083,2087,2096,8443
        --hostlist=lists/discord.txt
        --lua-desync=hostfakesplit_multi:repeats=2

        --new
        --name=Discord UDP
        --filter-udp=443-65535
        --ipset=lists/ipset-discord.txt
        --lua-desync=fake:blob=quic_google
        """;

    /// <summary>Восемь портов секции, а не два зашитых.</summary>
    [Fact]
    public void PortsComeFromTheSectionThatOwnsTheName()
    {
        Assert.Equal(
            "80,443,1080,2053,2083,2087,2096,8443",
            PresetPorts.ForDomains(Load(Preset), _root, ["discord.com"]));
    }

    /// <summary>
    /// Запись списка — зона: <c>discord.gg</c> покрывает и <c>gateway.discord.gg</c>,
    /// и порты ему полагаются те же.
    /// </summary>
    [Fact]
    public void SubdomainInheritsTheZonePorts()
    {
        Assert.Equal(
            "80,443,1080,2053,2083,2087,2096,8443",
            PresetPorts.ForDomains(Load(Preset), _root, ["gateway.discord.gg"]));
    }

    /// <summary>
    /// Имя встречается в нескольких секциях — берётся объединение, а не первая
    /// попавшаяся: решать по расположению в файле значило бы решать случайно.
    /// Лишний порт безвреден, на нём просто не окажется трафика.
    /// </summary>
    [Fact]
    public void PortsFromSeveralSectionsAreUnited()
    {
        var ports = PresetPorts.ForDomains(Load(Preset), _root, ["discord.com", "example.com"]);

        Assert.StartsWith("80,443", ports);
        Assert.Contains("8443", ports);

        // И без повторов: 80 с 443 есть у обеих секций.
        var parts = ports.Split(',');
        Assert.Equal(parts.Length, parts.Distinct().Count());
    }

    /// <summary>
    /// Секции по UDP портов не дают: <c>--filter-tcp</c> у них нет вовсе,
    /// а <c>--filter-udp</c> к профилю по именам отношения не имеет.
    /// </summary>
    [Fact]
    public void UdpSectionContributesNothing()
    {
        Assert.Equal(
            PresetPorts.Default,
            PresetPorts.ForDomains(Load(Preset), _root, ["ничего-такого.example"]));
    }

    /// <summary>
    /// Списка может не быть: пресет пишут под полную установку Zapret,
    /// а у нас встроенная копия. Это не повод остаться вовсе без портов.
    /// </summary>
    [Fact]
    public void MissingListFallsBackInsteadOfThrowing()
    {
        var preset = Load("""
            #name=Проба
            --wf-tcp=80,443

            --new
            --name=Нет такого списка
            --filter-tcp=80,443,8443
            --hostlist=lists/отсутствует.txt
            --lua-desync=split:pos=2
            """);

        Assert.Equal(PresetPorts.Default, PresetPorts.ForDomains(preset, _root, ["discord.com"]));
    }

    /// <summary>Имена в пресете записаны и прямо, без файла.</summary>
    [Fact]
    public void InlineDomainsCountToo()
    {
        var preset = Load("""
            #name=Проба
            --wf-tcp=80,443

            --new
            --name=Cloudflare WARP API
            --filter-tcp=443,2053
            --hostlist-domains=api.cloudflareclient.com
            --lua-desync=multidisorder:pos=1
            """);

        Assert.Equal(
            "443,2053",
            PresetPorts.ForDomains(preset, _root, ["api.cloudflareclient.com"]));
    }

    /// <summary>Порты доезжают до командной строки, а не теряются по дороге.</summary>
    [Fact]
    public void PortsReachTheCommandLine()
    {
        var arguments = WinwsCommandLine.Build(
            Load(Preset),
            excludeList: null,
            own:
            [
                new OwnDesyncProfile
                {
                    Name = "Голос",
                    Steps = ["split:pos=2"],
                    HostListPath = Path.Combine(_root, "own.txt"),
                    Ports = "80,443,8443",
                },
            ]);

        Assert.Contains("--filter-tcp=80,443,8443", arguments);
    }

    /// <summary>Без портов профиль остаётся на прежнем умолчании.</summary>
    [Fact]
    public void ProfileWithoutPortsKeepsTheDefault()
    {
        var arguments = WinwsCommandLine.Build(
            Load(Preset),
            excludeList: null,
            own:
            [
                new OwnDesyncProfile
                {
                    Name = "Голос",
                    Steps = ["split:pos=2"],
                    HostListPath = Path.Combine(_root, "own.txt"),
                },
            ]);

        Assert.Contains("--filter-tcp=" + PresetPorts.Default, arguments);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
