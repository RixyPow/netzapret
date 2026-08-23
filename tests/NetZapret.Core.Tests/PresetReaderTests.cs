using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Форма пресета взята из реального Universal V6.
/// </summary>
public sealed class PresetReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-preset-{Guid.NewGuid():N}");

    public PresetReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "presets", "winws2"));
        Directory.CreateDirectory(Path.Combine(_root, "lists"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string WritePreset(string name, string content)
    {
        var path = Path.Combine(_root, "presets", "winws2", name + ".txt");
        File.WriteAllText(path, content);
        return path;
    }

    private void WriteList(string name, string content) =>
        File.WriteAllText(Path.Combine(_root, "lists", name), content);

    private const string SamplePreset = """
        # Preset: Universal Test
        # BuiltinVersion: 2.26
        # IconColor: #ff7b72
        # Description: Тестовый пресет.

        --lua-init=@lua/zapret-lib.lua
        --wf-tcp-out=80,443-65535
        --blob=tls_google:@bin/tls_clienthello_www_google_com.bin

        --new

        --name=Исключения (RU сайты)
        --filter-tcp=80,443-65535
        --ipset=lists/ipset-ru.txt
        --lua-desync=pass

        --new

        # Комментарий внутри секции: не метаданные, хотя двоеточие есть.
        --name=youtube.com (интерфейс)
        --filter-tcp=80,443
        --hostlist=lists/youtube.txt
        --out-range=-d8
        --lua-desync=multidisorder:pos=1,host+2:seqovl=1

        --new

        --name=Discord
        --filter-tcp=443
        --hostlist=lists/discord.txt
        --ipset=lists/ipset-discord.txt
        --lua-desync=send:repeats=3
        --lua-desync=syndata:blob=tls_google

        --new

        --name=Claude
        --filter-tcp=80,443
        --hostlist=lists/claude.txt
        --lua-desync=fake:blob=tls_google:ip_autottl=-1,3-20:repeats=6:tcp_ack=-66000
        --lua-desync=multidisorder:pos=1,host+2

        --new

        --name=skinsrestorer.net
        --hostlist-domains=skinsrestorer.net,example.org
        --lua-desync=hostfakesplit:host=ozon.ru
        """;

    [Fact]
    public void HeaderMetadataIsRead()
    {
        var preset = new PresetReader().Load(WritePreset("sample", SamplePreset));

        Assert.Equal("Universal Test", preset.Name);
        Assert.Equal("2.26", preset.BuiltinVersion);
        Assert.Equal("#ff7b72", preset.IconColor);
        Assert.Equal("Тестовый пресет.", preset.Description);
    }

    [Fact]
    public void ArgumentsBeforeFirstSeparatorAreGlobal()
    {
        var preset = new PresetReader().Load(WritePreset("sample", SamplePreset));

        Assert.Equal(3, preset.GlobalArguments.Count);
        Assert.Contains("--wf-tcp-out=80,443-65535", preset.GlobalArguments);
    }

    [Fact]
    public void SectionsKeepFileOrder()
    {
        // Порядок значим: в winws2 выигрывает первая совпавшая секция.
        var preset = new PresetReader().Load(WritePreset("sample", SamplePreset));

        Assert.Equal(5, preset.Sections.Count);
        Assert.Equal("Исключения (RU сайты)", preset.Sections[0].Name);
        Assert.Equal("youtube.com (интерфейс)", preset.Sections[1].Name);
        Assert.Equal("skinsrestorer.net", preset.Sections[4].Name);
    }

    [Fact]
    public void PassThroughSectionIsDistinguishedFromHavingNoRecipes()
    {
        var preset = new PresetReader().Load(WritePreset("sample", SamplePreset));

        Assert.True(preset.Sections[0].IsPassThrough);
        Assert.False(preset.Sections[1].IsPassThrough);
        Assert.Equal(4, preset.ActiveSections.Count());
    }

    [Fact]
    public void OnlyFakePrefixCountsAsFakePackets()
    {
        // Измерение 2026-08-23: Discord с syndata:blob под TUN работал,
        // Claude с fake:blob — нет. Признаком служит префикс, а не наличие blob.
        var preset = new PresetReader().Load(WritePreset("sample", SamplePreset));

        var discord = preset.Sections.Single(s => s.Name == "Discord");
        var claude = preset.Sections.Single(s => s.Name == "Claude");
        var youtube = preset.Sections.Single(s => s.Name.StartsWith("youtube"));

        Assert.False(discord.UsesFakePackets);
        Assert.True(claude.UsesFakePackets);
        Assert.False(youtube.UsesFakePackets);
    }

    [Fact]
    public void InlineDomainsAreSplitOnCommas()
    {
        var preset = new PresetReader().Load(WritePreset("sample", SamplePreset));
        var section = preset.Sections.Single(s => s.Name == "skinsrestorer.net");

        Assert.Equal(2, section.InlineDomains.Count);
        Assert.Contains("example.org", section.InlineDomains);
    }

    [Fact]
    public void CommentsInsideSectionsAreNotTreatedAsMetadata()
    {
        var preset = new PresetReader().Load(WritePreset("sample", SamplePreset));

        // Описание должно остаться из шапки, а не подхватиться из комментария секции.
        Assert.Equal("Тестовый пресет.", preset.Description);
    }

    [Fact]
    public void CoveredDomainsSkipPassThroughSectionsByDefault()
    {
        WriteList("youtube.txt", "youtube.com\nytimg.com\n# комментарий\n\n");
        WriteList("discord.txt", "discord.com\n");
        WriteList("claude.txt", "claude.ai\n");

        var reader = new PresetReader();
        var preset = reader.Load(WritePreset("sample", SamplePreset));
        var domains = reader.CollectCoveredDomains(preset, _root);

        Assert.Contains("youtube.com", domains);
        Assert.Contains("claude.ai", domains);
        Assert.Contains("skinsrestorer.net", domains);
        Assert.Equal(6, domains.Count);
    }

    [Fact]
    public void CoveredAddressesComeFromIpsetsAndSkipPassThrough()
    {
        WriteList("ipset-ru.txt", "5.255.255.0/24\n");
        WriteList("ipset-discord.txt", "162.159.129.0/24\n# yt3\n84.17.42.0/24\n");

        var reader = new PresetReader();
        var preset = reader.Load(WritePreset("sample", SamplePreset));
        var addresses = reader.CollectCoveredAddresses(preset, _root);

        // ipset-ru принадлежит pass-секции: эти адреса десинком не покрыты.
        Assert.DoesNotContain("5.255.255.0/24", addresses);
        Assert.Contains("162.159.129.0/24", addresses);
        Assert.Equal(2, addresses.Count);
    }

    [Fact]
    public void BareAddressesGetAFullPrefix()
    {
        // route_exclude_address принимает только префиксы, а в ipset'ах
        // встречаются голые адреса — например в lists/ipset-claude.txt.
        WriteList("ipset-discord.txt", "185.68.247.42\n2001:db8::1\n");

        var reader = new PresetReader();
        var preset = reader.Load(WritePreset("sample", SamplePreset));
        var addresses = reader.CollectCoveredAddresses(preset, _root);

        Assert.Contains("185.68.247.42/32", addresses);
        Assert.Contains("2001:db8::1/128", addresses);
    }

    [Fact]
    public void DuplicateAddressesAcrossSectionsAreCollapsed()
    {
        WriteList("ipset-discord.txt", "162.159.129.0/24\n162.159.129.0/24\n");

        var reader = new PresetReader();
        var preset = reader.Load(WritePreset("sample", SamplePreset));

        Assert.Single(reader.CollectCoveredAddresses(preset, _root));
    }

    [Fact]
    public void MissingListFilesAreIgnored()
    {
        // Пресет может ссылаться на список, которого нет в установке.
        var reader = new PresetReader();
        var preset = reader.Load(WritePreset("sample", SamplePreset));

        Assert.Empty(reader.CollectCoveredAddresses(preset, _root));
        Assert.Single(reader.CollectCoveredDomains(preset, _root), d => d == "skinsrestorer.net");
    }

    [Fact]
    public void PresetWithoutMetadataFallsBackToFileName()
    {
        var path = WritePreset("Fallback Name", "--name=x\n--lua-desync=pass\n");
        var preset = new PresetReader().Load(path);

        Assert.Equal("Fallback Name", preset.Name);
    }

    [Fact]
    public void ListEnumeratesEveryPresetInTheDirectory()
    {
        WritePreset("one", SamplePreset);
        WritePreset("two", SamplePreset);

        var presets = new PresetReader().List(Path.Combine(_root, "presets", "winws2"));

        Assert.Equal(2, presets.Count);
    }

    [Fact]
    public void MissingDirectoryYieldsEmptyList()
    {
        Assert.Empty(new PresetReader().List(Path.Combine(_root, "nope")));
    }
}
