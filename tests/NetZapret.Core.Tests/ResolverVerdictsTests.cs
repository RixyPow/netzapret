using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Пометки резолверов по обзору и их память между запусками (<see cref="ResolverVerdicts"/>).
/// </summary>
public sealed class ResolverVerdictsTests : IDisposable
{
    private static readonly DateTime At = new(2026, 11, 5, 11, 0, 0);

    private static readonly DnsProvider Google = new("Google", ["8.8.4.4"], "8.8.4.4", "dns.google");
    private static readonly DnsProvider Comss = new("Comss.one", ["83.220.169.155"], "83.220.169.155", "dns.comss.one");
    private static readonly DnsProvider Yandex = new("Yandex", ["77.88.8.8"], "77.88.8.8", "common.dot.dns.yandex.net");
    private static readonly DnsProvider DohOnly = new("Только DoH", [], "104.21.15.208", "dns.zapret.moe");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nz-resolver-verdicts-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_folder, "runtime", "resolver-verdicts.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static DnsSurveyRow Honest(DnsProvider provider) => new()
    {
        Provider = provider,
        DohMs = 30,
        UdpMs = 5,
        UdpAnswered = provider.Udp.Count,
        Spoofed = 0,
        SpoofChecked = provider.Udp.Count == 0 ? 0 : 5,
    };

    [Fact]
    public void DoH_that_does_not_pass_is_blocked_whatever_UDP_says()
    {
        var row = Honest(Comss) with { DohMs = null, DohFailure = "TIMEOUT" };

        Assert.Equal(new ResolverVerdict(ResolverVerdictKind.Blocked, "TIMEOUT", At), ResolverVerdicts.Judge(row, At));
    }

    [Fact]
    public void Spoofed_names_mark_UDP_as_spoofed()
    {
        var spoofed = Honest(Google) with { Spoofed = 5, Intercepted = true };

        Assert.Equal(new ResolverVerdict(ResolverVerdictKind.UdpSpoofed, "5/5", At), ResolverVerdicts.Judge(spoofed, At));
    }

    [Fact]
    public void A_foreign_egress_with_honest_answers_is_not_spoofing()
    {
        // Quad9 ходит наружу через партнёров: выход в чужой сети, подмены 0 из 5 (обзор 05.11).
        var quad9 = Honest(new DnsProvider("Quad9", ["9.9.9.9"], "9.9.9.9", "dns.quad9.net")) with { Intercepted = true };

        Assert.Equal(ResolverVerdictKind.Untouched, ResolverVerdicts.Judge(quad9, At)?.Kind);
    }

    [Fact]
    public void Untouched_only_when_DoH_passed_and_UDP_answered_honestly()
    {
        Assert.Equal(ResolverVerdictKind.Untouched, ResolverVerdicts.Judge(Honest(Yandex), At)?.Kind);

        // Открытого DNS у резолвера нет — проверять нечего, хватает DoH.
        Assert.Equal(ResolverVerdictKind.Untouched, ResolverVerdicts.Judge(Honest(DohOnly), At)?.Kind);
    }

    [Fact]
    public void Silent_UDP_or_unmeasured_DoH_says_nothing()
    {
        var silentUdp = Honest(Yandex) with { UdpMs = null, UdpAnswered = 0, SpoofChecked = 0, UdpFailure = "TIMEOUT" };
        var notMeasured = new DnsSurveyRow { Provider = Yandex };

        Assert.Null(ResolverVerdicts.Judge(silentUdp, At));
        Assert.Null(ResolverVerdicts.Judge(notMeasured, At));
    }

    [Fact]
    public void Verdicts_survive_a_restart_with_their_time()
    {
        ResolverVerdicts.Remember([Honest(Comss) with { DohMs = null, DohFailure = "TLS DROP" }, Honest(Google) with { Spoofed = 3 }], At, File);

        var read = ResolverVerdicts.Load(File);

        Assert.Equal(new ResolverVerdict(ResolverVerdictKind.Blocked, "TLS DROP", At), read["83.220.169.155"]);
        Assert.Equal(new ResolverVerdict(ResolverVerdictKind.UdpSpoofed, "3/5", At), read["8.8.4.4"]);
    }

    [Fact]
    public void A_partial_survey_keeps_the_others_and_clears_what_it_could_not_judge()
    {
        ResolverVerdicts.Remember([Honest(Comss) with { DohMs = null, DohFailure = "TIMEOUT" }, Honest(Google) with { Spoofed = 5 }], At, File);

        // Позже проверили только Google: UDP замолчал — пометка снята; Comss не трогали.
        var later = At.AddHours(1);
        ResolverVerdicts.Remember([Honest(Google) with { UdpMs = null, UdpAnswered = 0, SpoofChecked = 0 }], later, File);

        var read = ResolverVerdicts.Load(File);

        Assert.False(read.ContainsKey("8.8.4.4"));
        Assert.Equal(At, read["83.220.169.155"].At);
    }

    [Fact]
    public void A_missing_or_broken_file_reads_as_nothing_remembered()
    {
        Assert.Empty(ResolverVerdicts.Load(File));

        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        System.IO.File.WriteAllText(File, "{ не json");

        Assert.Empty(ResolverVerdicts.Load(File));
    }
}
