using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Почерки поломок (14.10): каждый узнаётся по своему признаку и не путается
/// с соседями; при массовом отказе остальные не ищутся.
/// </summary>
public sealed class SignaturesTests
{
    private static readonly ProbeOutcome Good = new() { Ok = true };

    private static TargetReport Report(
        string host,
        BlockKind kind,
        int? received = null,
        string? answered = null,
        string[]? addresses = null,
        string[]? silent = null,
        bool tunnelled = false,
        DnsSpoofResult? spoof = null) => new()
        {
            Host = host,
            Tcp = new ProbeOutcome { Ok = kind is not (BlockKind.Full or BlockKind.HttpsPort), Silent = silent ?? [] },
            Tls = Good,
            Http = Good,
            Data = new ProbeOutcome { Ok = kind == BlockKind.None, Received = received },
            Kind = kind,
            Answered = answered,
            Addresses = addresses ?? (answered is null ? [] : [answered]),
            Tunnelled = tunnelled,
            Spoof = spoof ?? DnsSpoofResult.Unknown,
        };

    private static Dictionary<string, List<IPAddress>> Hosts(params (string Name, string Address)[] pins) =>
        pins.ToDictionary(p => p.Name, p => new List<IPAddress> { IPAddress.Parse(p.Address) }, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TheFreezeIsRecognisedByItsBytes()
    {
        var found = Signatures.Find(
        [
            Report("steamcommunity.com", BlockKind.Stall, received: 14_381),
            Report("skinsrestorer.net", BlockKind.Stall, received: 13_505),
            Report("itch.io", BlockKind.Stall, received: 26_011),
            Report("example.com", BlockKind.None, received: 14_000),
        ]);

        var freeze = Assert.Single(found, f => f.Signature == Signatures.Freeze);

        Assert.Equal(["steamcommunity.com", "skinsrestorer.net"], freeze.Hosts);
    }

    [Fact]
    public void ADeadPinNeedsTheProbeToGoToThePinnedAddress()
    {
        var hosts = Hosts(("chat.openai.com", "72.56.93.144"), ("www.deepl.com", "72.56.93.144"), ("canva.com", "72.56.93.144"));

        var found = Signatures.Find(
        [
            Report("chat.openai.com", BlockKind.Full, addresses: ["72.56.93.144"]),
            Report("www.deepl.com", BlockKind.TlsDpi, answered: "72.56.93.144"),

            // Прибито, но открывается — пин живой.
            Report("canva.com", BlockKind.None, answered: "72.56.93.144"),

            // Не прибито — о пине судить не по чему.
            Report("x.ai", BlockKind.Full, addresses: ["104.18.1.1"]),
        ], hosts);

        var pin = Assert.Single(found, f => f.Signature == Signatures.DeadPin);

        Assert.Equal("Пин в hosts не работает: 72.56.93.144", pin.Headline);
        Assert.Equal(["chat.openai.com", "www.deepl.com"], pin.Hosts);
    }

    /// <summary>Чужой сертификат на прибитом адресе — тот же почерк, даже если вердикт «доступен».</summary>
    [Fact]
    public void AForeignCertificateOnAPinIsADeadPin()
    {
        var spoof = new DnsSpoofResult { Verdict = DnsVerdict.ForeignCertificate, Detail = "сертификат instaposts.halahala.ru" };

        var found = Signatures.Find(
            [Report("chat.openai.com", BlockKind.None, answered: "72.56.93.144", spoof: spoof)],
            Hosts(("CHAT.OPENAI.COM", "72.56.93.144")));

        var pin = Assert.Single(found);

        Assert.Equal(Signatures.DeadPin, pin.Signature);
        Assert.Contains("instaposts.halahala.ru", pin.Lines[0]);
    }

    [Fact]
    public void ASharedNodeNeedsThreeNamesAndSkipsDeadPins()
    {
        var three = Signatures.Find(
        [
            Report("itch.io", BlockKind.TlsDpi, answered: "8.47.69.6"),
            Report("x.ai", BlockKind.Stall, received: 30_000, answered: "8.47.69.6"),
            Report("aternos.org", BlockKind.Full, addresses: ["8.47.69.6"]),
            Report("osu.ppy.sh", BlockKind.None, answered: "8.47.69.6"),
        ]);

        var node = Assert.Single(three, f => f.Signature == Signatures.SharedNode);

        Assert.Equal("Закрыт общий узел: 8.47.69.6", node.Headline);
        Assert.Equal(3, node.Hosts.Count);

        // Два — ещё совпадение; прибитое уходит в почерк пина, а не узла.
        var two = Signatures.Find(
            [
                Report("itch.io", BlockKind.TlsDpi, answered: "8.47.69.6"),
                Report("x.ai", BlockKind.TlsDpi, answered: "8.47.69.6"),
                Report("aternos.org", BlockKind.TlsDpi, answered: "8.47.69.6"),
            ],
            Hosts(("aternos.org", "8.47.69.6")));

        Assert.DoesNotContain(two, f => f.Signature == Signatures.SharedNode);
        Assert.Contains(two, f => f.Signature == Signatures.DeadPin);
    }

    /// <summary>
    /// Больше половины не ответили — находка одна, остальные молчат
    /// (29.09: автозамена приняла такую минуту за смерть 68 адресов).
    /// </summary>
    [Fact]
    public void AMassFailureSilencesTheRest()
    {
        var reports = Enumerable.Range(0, 7).Select(i => Report($"site{i}.com", BlockKind.Full, addresses: ["8.47.69.6"]))
            .Concat(Enumerable.Range(0, 4).Select(i => Report($"ok{i}.com", BlockKind.None)))
            .Append(Report("steamcommunity.com", BlockKind.Stall, received: 14_381))
            .ToList();

        var found = Signatures.Find(reports, Hosts(("site0.com", "8.47.69.6")));

        var mass = Assert.Single(found);

        Assert.Equal(Signatures.MassFailure, mass.Signature);
        Assert.Equal("Больше половины имён не ответили вовсе: 7 из 12", mass.Headline);

        // Меньше десяти имён — доля ничего не значит.
        var few = Enumerable.Range(0, 6).Select(i => Report($"site{i}.com", BlockKind.Full, addresses: [$"1.1.1.{i}"]))
            .Concat(Enumerable.Range(0, 3).Select(i => Report($"ok{i}.com", BlockKind.None)))
            .ToList();

        Assert.DoesNotContain(Signatures.Find(few), f => f.Signature == Signatures.MassFailure);
    }

    [Fact]
    public void PartlySilentNamesAreCalm()
    {
        var found = Signatures.Find(
            [Report("google.com", BlockKind.None, answered: "142.250.74.46", silent: ["142.251.142.206", "173.194.222.100"])]);

        var partly = Assert.Single(found);

        Assert.Equal(Signatures.PartlySilent, partly.Signature);
        Assert.True(partly.Calm);
        Assert.Equal("google.com — молчали 142.251.142.206, 173.194.222.100, ответил 142.250.74.46", partly.Lines[0]);
    }

    /// <summary>Через туннель адрес подменный: ни узел, ни пин, ни байты о сайте не говорят.</summary>
    [Fact]
    public void TunnelledNamesAreLeftOut()
    {
        var found = Signatures.Find(
        [
            Report("a.com", BlockKind.Stall, received: 14_000, answered: "198.18.0.5", tunnelled: true),
            Report("b.com", BlockKind.TlsDpi, answered: "198.18.0.5", tunnelled: true),
            Report("c.com", BlockKind.TlsDpi, answered: "198.18.0.5", tunnelled: true),
            Report("d.com", BlockKind.TlsDpi, answered: "198.18.0.5", tunnelled: true),
        ]);

        Assert.Empty(found);
    }

    /// <summary>
    /// <summary>
    /// Закрытое по имени, но принятое с общим именем ECH — сведения, не поломка:
    /// в браузере с ECH откроется. Без принятого приветствия — не почерк.
    /// </summary>
    [Fact]
    public void AnEchHiddenNameIsCalmAndNeedsTheAcceptedHello()
    {
        var found = Signatures.Find(
        [
            Report("lostfilm.tv", BlockKind.TlsDpi, answered: "188.114.96.1") with { EchName = "cloudflare-ech.com", EchHelloAccepted = true },
            Report("rutracker.org", BlockKind.TlsDpi, answered: "104.21.32.39") with { EchName = "cloudflare-ech.com" },
            Report("example.com", BlockKind.TlsDpi, answered: "8.6.112.0"),
        ]);

        var ech = Assert.Single(found, f => f.Signature == Signatures.EchHidden);

        Assert.True(ech.Calm);
        Assert.Equal(["lostfilm.tv"], ech.Hosts);
        Assert.Contains("cloudflare-ech.com", ech.Lines[0]);
    }

    /// <summary>
    /// Почерк без случая, по которому он узнан, — догадка: каждый в каталоге
    /// обязан нести смысл, совет и происхождение.
    /// </summary>
    [Fact]
    public void EverySignatureSaysWhereItComesFrom()
    {
        Assert.Equal(Signatures.All.Count, Signatures.All.Select(s => s.Id).Distinct().Count());

        foreach (var signature in Signatures.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(signature.Title), signature.Id);
            Assert.False(string.IsNullOrWhiteSpace(signature.Meaning), signature.Id);
            Assert.False(string.IsNullOrWhiteSpace(signature.Remedy), signature.Id);
            Assert.False(string.IsNullOrWhiteSpace(signature.Origin), signature.Id);
        }
    }

    [Fact]
    public void TheFindingTextCarriesMeaningRemedyAndOrigin()
    {
        var finding = Assert.Single(Signatures.Find([Report("steamcommunity.com", BlockKind.Stall, received: 14_381)]));
        var text = finding.Describe();

        Assert.StartsWith("steamcommunity.com — 14", text);
        Assert.Contains(Signatures.Freeze.Meaning, text);
        Assert.Contains("Что делать: " + Signatures.Freeze.Remedy, text);
        Assert.Contains("Почерк известен: " + Signatures.Freeze.Origin, text);
    }
}
