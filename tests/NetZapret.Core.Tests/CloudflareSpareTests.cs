using System.Net;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Три правки «Сайт не открывается?» 05.11 по ntc.party: кто закрыл — провайдер
/// или наша секция по адресам (№52), запасной адрес Cloudflare вместо VPN
/// и вердикт «режут длинное приветствие».
/// </summary>
public sealed class CloudflareSpareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-syn-{Guid.NewGuid():N}");

    public CloudflareSpareTests() => Directory.CreateDirectory(Path.Combine(_root, "lists"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static TargetReport Report(BlockKind kind, string? answered = null) => new()
    {
        Host = "example.org",
        Tcp = new ProbeOutcome { Ok = true },
        Tls = new ProbeOutcome { Ok = kind == BlockKind.None },
        Http = new ProbeOutcome { Ok = true },
        Data = new ProbeOutcome { Ok = kind == BlockKind.None },
        Kind = kind,
        Answered = answered,
    };

    private static RouteTry[] Kinds(RoutePlan plan) => plan.Steps.Select(s => s.Kind).ToArray();

    // Адреса из замера 05.11: свои адреса сайтов на Cloudflare, запасные, CloudFront.
    [Theory]
    [InlineData("104.16.25.14", true)]
    [InlineData("188.114.96.11", true)]
    [InlineData("172.67.68.209", true)]
    [InlineData("162.159.137.232", true)]
    [InlineData("8.47.69.6", true)]
    [InlineData("8.6.112.6", true)]
    [InlineData("65.9.62.123", false)]
    [InlineData("149.154.167.220", false)]
    [InlineData("::1", false)]
    public void Cloudflare_networks_are_known(string address, bool cloudflare) =>
        Assert.Equal(cloudflare, CloudflareNet.Contains(address));

    [Fact]
    public void Spares_are_cloudflare_themselves() =>
        Assert.All(CloudflareNet.Spares, a => Assert.True(CloudflareNet.Contains(a)));

    /// <summary>Длинное — двумя пакетами (больше MSS 1460), короткое — одним, без X25519MLKEM768.</summary>
    [Fact]
    public void The_compact_hello_fits_one_packet_and_has_no_post_quantum_key()
    {
        var full = BrowserHello.Build("example.com");
        var brief = BrowserHello.Build("example.com", compact: true);

        Assert.True(full.Length > 1460, $"длинное {full.Length} Б");
        Assert.True(brief.Length < 1000, $"короткое {brief.Length} Б");

        // Длины записи и рукопожатия сходятся с тем, что за ними.
        Assert.Equal(brief.Length - 5, (brief[3] << 8) | brief[4]);
        Assert.Equal(brief.Length - 9, (brief[6] << 16) | (brief[7] << 8) | brief[8]);

        // Ключ гибрида: группа 0x11EC и длина 1216 (1184 ML-KEM + 32 X25519).
        // Не просто «11EC»: случайные байты приветствия дают его через раз на полсотни.
        Assert.Contains("11EC04C0", Hex(full), StringComparison.Ordinal);
        Assert.DoesNotContain("11EC04C0", Hex(brief), StringComparison.Ordinal);
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);

    [Fact]
    public void A_cut_cloudflare_address_tries_the_spare_before_vpn()
    {
        var plan = RoutePlan.For(Report(BlockKind.TlsByAddress), RoutingMode.Desync, desync: true, tunnel: true, cloudflare: true);

        Assert.Equal([RouteTry.CloudflareSpare, RouteTry.Vpn], Kinds(plan));
    }

    [Fact]
    public void Not_cloudflare_or_through_vpn_gets_no_spare()
    {
        Assert.Equal([RouteTry.Vpn],
            Kinds(RoutePlan.For(Report(BlockKind.Full), RoutingMode.Desync, desync: true, tunnel: true, cloudflare: false)));

        Assert.Empty(Kinds(RoutePlan.For(Report(BlockKind.Full), RoutingMode.Proxy, desync: true, tunnel: true, cloudflare: true)));
    }

    /// <summary>Режут имя — запасной адрес не спасает (rutracker, canva 05.11): его нет в плане.</summary>
    [Fact]
    public void A_cut_name_gets_no_spare()
    {
        var plan = RoutePlan.For(Report(BlockKind.TlsDpi), RoutingMode.Desync, desync: true, tunnel: true, cloudflare: true);

        Assert.DoesNotContain(RouteTry.CloudflareSpare, Kinds(plan));
    }

    [Fact]
    public void A_cut_long_hello_tries_preset_spare_then_vpn()
    {
        var plan = RoutePlan.For(Report(BlockKind.LongHello), RoutingMode.Direct, desync: true, tunnel: true, cloudflare: true);

        Assert.Equal([RouteTry.Preset, RouteTry.CloudflareSpare, RouteTry.Vpn], Kinds(plan));
        Assert.Contains("длинное приветствие", plan.Advice);
    }

    [Fact]
    public void A_long_hello_verdict_reads_as_closed_with_its_own_words()
    {
        var report = Report(BlockKind.LongHello) with { Tls = new ProbeOutcome { Ok = true, Detail = "короткое принято" } };

        Assert.Equal("режут длинное приветствие браузера", report.Describe());
        Assert.Equal("режут длинное", report.Brief());
        Assert.Equal(Reach.Closed, report.Reach);
        Assert.Equal("короткое принято", report.Why);
        Assert.Contains("запасной адрес Cloudflare", report.Remedy());
    }

    [Theory]
    [InlineData("80,443-65535", 443, true)]
    [InlineData("80,443-65535", 80, true)]
    [InlineData("80,443-65535", 81, false)]
    [InlineData("443", 443, true)]
    [InlineData("2053,2083", 443, false)]
    public void Ports_are_read_like_winws2(string spec, int port, bool included) =>
        Assert.Equal(included, SynSections.PortsInclude(spec, port));

    private ZapretPreset Preset(string body)
    {
        File.WriteAllText(Path.Combine(_root, "lists", "ipset-discord.txt"), "162.159.0.0/16\n104.16.0.0/13\n");
        File.WriteAllText(Path.Combine(_root, "lists", "ipset-other.txt"), "8.47.69.0/24\n");
        File.WriteAllText(Path.Combine(_root, "lists", "discord.txt"), "discord.com\n");

        var path = Path.Combine(_root, "preset.txt");
        File.WriteAllText(path, "# Preset: проба\n--wf-tcp-out=443\n--new\n" + body);

        return new PresetReader().Load(path);
    }

    /// <summary>
    /// Как в №52: секция по имени выше на SYN не совпадает, адрес забирает
    /// «discord.com (IP fallback)», и это часть «Discord — Запасной путь по адресам».
    /// </summary>
    [Fact]
    public void The_syn_goes_to_the_first_address_section_and_names_its_part()
    {
        var preset = Preset("""
            --name=discord.com
            --filter-tcp=443
            --hostlist=lists/discord.txt
            --lua-desync=multisplit
            --new
            --name=Discord TLS
            --filter-tcp=443
            --filter-l7=tls
            --ipset=lists/ipset-discord.txt
            --lua-desync=fake
            --new
            --name=discord.com (IP fallback)
            --filter-tcp=80,443-65535
            --ipset=lists/ipset-discord.txt
            --lua-desync=send:repeats=3
            --lua-desync=syndata:blob=tls_google
            """);

        var found = SynSections.Find(preset, _root, IPAddress.Parse("162.159.137.232"), 443);

        Assert.NotNull(found);
        Assert.Equal("discord.com (IP fallback)", found.Section);
        Assert.Equal("ipset-discord.txt", found.List);
        Assert.Equal("Discord", found.Service);
        Assert.Equal("Запасной путь по адресам", found.Part?.Name);
    }

    [Fact]
    public void An_address_in_no_list_or_off_port_has_no_section()
    {
        var preset = Preset("""
            --name=discord.com (IP fallback)
            --filter-tcp=443
            --ipset=lists/ipset-discord.txt
            --lua-desync=send:repeats=3
            """);

        Assert.Null(SynSections.Find(preset, _root, IPAddress.Parse("8.8.8.8"), 443));
        Assert.Null(SynSections.Find(preset, _root, IPAddress.Parse("162.159.137.232"), 2053));
    }

    /// <summary>Первой совпала секция pass — она и берёт соединение, десинк его не трогает.</summary>
    [Fact]
    public void A_pass_section_first_means_not_ours()
    {
        var preset = Preset("""
            --name=пропуск
            --filter-tcp=443
            --ipset=lists/ipset-discord.txt
            --lua-desync=pass
            --new
            --name=discord.com (IP fallback)
            --filter-tcp=443
            --ipset=lists/ipset-discord.txt
            --lua-desync=send:repeats=3
            """);

        Assert.Null(SynSections.Find(preset, _root, IPAddress.Parse("162.159.137.232"), 443));
    }

    [Fact]
    public void The_outcome_line_says_what_happened()
    {
        static SiteFixResult Result(RoutingMode? applied = null, string? pinned = null, string? ours = null) =>
            new("example.org", [], applied, false, string.Empty, Pinned: pinned, Ours: ours);

        Assert.Equal(" → записан маршрут Proxy", Result(RoutingMode.Proxy).Outcome());
        Assert.Contains("запасному адресу Cloudflare 8.47.69.6", Result(pinned: "8.47.69.6").Outcome());
        Assert.Contains("discord.com (IP fallback)", Result(ours: "discord.com (IP fallback)").Outcome());
        Assert.Equal(" → маршрут не менялся", Result().Outcome());
    }
}
