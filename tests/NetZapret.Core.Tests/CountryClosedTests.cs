using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Нейросети, закрытые по стране: вердикт по списку и пин первым советом.
/// </summary>
/// <remarks>
/// Обсуждение у Flowseal №17531 (01.11): проверка писала Gemini «доступен», а он
/// отказывал запросам. Решение владельца 05.11 — советовать пин, если его нет, потом VPN.
/// </remarks>
public class CountryClosedTests
{
    private static readonly HashSet<string> Zones = ["gemini.google.com", "chatgpt.com", "claude.ai"];

    [Theory]
    [InlineData("gemini.google.com")]
    [InlineData("GEMINI.google.com.")]
    [InlineData("cdn.chatgpt.com")]
    [InlineData("claude.ai")]
    public void Names_in_the_lists_are_covered_with_their_subdomains(string host) =>
        Assert.True(CountryClosed.Covers(host, Zones));

    [Theory]
    [InlineData("google.com")]
    [InlineData("www.google.com")]
    [InlineData("notchatgpt.com")]
    [InlineData("claude.ai.example.org")]
    public void Neighbours_are_not(string host) =>
        Assert.False(CountryClosed.Covers(host, Zones));

    /// <summary>Списки — те самые, что правила «через VPN» в rules.yaml.</summary>
    [Fact]
    public void Every_list_is_a_proxy_rule_and_lies_in_the_repository()
    {
        var root = RepoRoot();
        var rules = File.ReadAllText(Path.Combine(root, "config", "rules.yaml")).Replace("\r\n", "\n");

        foreach (var list in CountryClosed.Lists)
        {
            Assert.True(File.Exists(Path.Combine(root, list)), list);
            Assert.Contains($"value: {list}\n    mode: proxy", rules);
        }

        var zones = CountryClosed.Read(CountryClosed.Lists.Select(l => Path.Combine(root, l)));
        Assert.True(CountryClosed.Covers("gemini.google.com", zones));
        Assert.True(CountryClosed.Covers("grok.com", zones));
    }

    [Fact]
    public void An_open_page_without_a_pin_becomes_a_country_refusal()
    {
        var (kind, data) = BlockCheck.ClosedByCountry(BlockKind.None, new ProbeOutcome { Ok = true }, pinFirst: true);

        Assert.Equal(BlockKind.GeoBlock, kind);
        Assert.Contains("по списку", data.Detail);
    }

    [Theory]
    [InlineData(false, BlockKind.None)]
    [InlineData(true, BlockKind.TlsDpi)]
    [InlineData(true, BlockKind.Stall)]
    public void Pinned_or_otherwise_broken_names_keep_their_verdict(bool pinFirst, BlockKind kind) =>
        Assert.Equal(kind, BlockCheck.ClosedByCountry(kind, new ProbeOutcome { Ok = true }, pinFirst).Kind);

    [Fact]
    public void Without_a_pin_the_advice_starts_with_the_pin_and_ends_with_vpn()
    {
        var report = new TargetReport
        {
            Host = "gemini.google.com",
            Tcp = new ProbeOutcome { Ok = true },
            Tls = new ProbeOutcome { Ok = true },
            Http = new ProbeOutcome { Ok = true },
            Data = new ProbeOutcome { Ok = true },
            Kind = BlockKind.GeoBlock,
            PinFirst = true,
        };

        var remedy = report.Remedy();
        Assert.StartsWith("пин", remedy);
        Assert.True(remedy.IndexOf("пин", StringComparison.Ordinal) < remedy.IndexOf("VPN", StringComparison.Ordinal));
        Assert.DoesNotContain("пин", (report with { PinFirst = false }).Remedy());
    }

    [Fact]
    public void Site_fixer_names_the_pin_first_and_keeps_vpn_as_the_step()
    {
        var report = new TargetReport
        {
            Host = "gemini.google.com",
            Tcp = new ProbeOutcome { Ok = true },
            Tls = new ProbeOutcome { Ok = true },
            Http = new ProbeOutcome { Ok = true },
            Data = new ProbeOutcome { Ok = true },
            Kind = BlockKind.GeoBlock,
            PinFirst = true,
        };

        var plan = NetZapret.Supervisor.RoutePlan.For(report, NetZapret.Core.Rules.RoutingMode.Desync, desync: true, tunnel: true);

        Assert.StartsWith("Сначала пин", plan.Advice);
        Assert.Equal([NetZapret.Supervisor.RouteTry.Vpn], plan.Steps.Select(s => s.Kind));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config", "rules.yaml")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("корень проекта не найден");
    }
}
