using System.Net.Sockets;
using NetZapret.Proxy;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Что нужно новому виду проверки блокировок (редизайн 27.10): ступень статуса,
/// короткий вердикт, сеть адреса и пробы мимо туннеля.
/// </summary>
public sealed class BlockCheckLookTests
{
    private static readonly ProbeOutcome Ok = new() { Ok = true, Elapsed = TimeSpan.FromMilliseconds(40) };

    private static readonly ProbeOutcome No = new() { Ok = false, Started = true };

    private static TargetReport Report(BlockKind kind, ProbeOutcome? quic = null, bool ech = false) => new()
    {
        Host = "example.com",
        Tcp = Ok,
        Tls = kind == BlockKind.None ? Ok : No,
        Http = No,
        Data = kind == BlockKind.None ? Ok : No,
        Kind = kind,
        Quic = quic,
        EchHelloAccepted = ech,
    };

    /// <summary>
    /// Ступень — по тому, откроется ли сайт в браузере, а не по виду блокировки.
    /// </summary>
    [Theory]
    [InlineData(BlockKind.None, Reach.Open)]
    [InlineData(BlockKind.Stall, Reach.Limited)]
    [InlineData(BlockKind.Forbidden, Reach.Limited)]
    [InlineData(BlockKind.TlsDpi, Reach.Closed)]
    [InlineData(BlockKind.GeoBlock, Reach.Closed)]
    [InlineData(BlockKind.TlsByAddress, Reach.Closed)]
    [InlineData(BlockKind.TunnelFailed, Reach.Closed)]
    [InlineData(BlockKind.NoAddress, Reach.Unknown)]
    [InlineData(BlockKind.Handshake, Reach.Unknown)]
    public void Reach_follows_whether_a_browser_gets_the_page(BlockKind kind, Reach reach) =>
        Assert.Equal(reach, Report(kind).Reach);

    /// <summary>
    /// Открытый по TCP сайт с молчащим QUIC — с ограничениями: браузер ждёт QUIC
    /// и подвисает (YouTube, 15.10). Закрытое имя, которое откроет браузер с ECH, — тоже.
    /// </summary>
    [Fact]
    public void Silent_quic_and_ech_make_it_limited_not_closed()
    {
        var quiet = Report(BlockKind.None, quic: No);
        var ech = Report(BlockKind.TlsDpi, ech: true);

        Assert.Equal(Reach.Limited, quiet.Reach);
        Assert.Equal("QUIC молчит", quiet.Brief());

        Assert.Equal(Reach.Open, Report(BlockKind.None, quic: Ok).Reach);
        Assert.Null(Report(BlockKind.None, quic: Ok).Brief());

        Assert.Equal(Reach.Limited, ech.Reach);
        Assert.Equal("откроется с ECH", ech.Brief());
    }

    /// <summary>Короткий вердикт есть у каждого вида и влезает в колонку.</summary>
    [Fact]
    public void Every_kind_has_a_brief_short_enough_for_the_column()
    {
        foreach (var kind in Enum.GetValues<BlockKind>().Where(k => k != BlockKind.None))
        {
            var brief = Report(kind).Brief();

            Assert.False(string.IsNullOrWhiteSpace(brief), kind.ToString());
            Assert.True(brief!.Length <= 18, $"{kind}: «{brief}» длиннее 18 знаков");
        }
    }

    /// <summary>Первый совпавший источник побеждает; вложенные сети сливаются.</summary>
    [Fact]
    public void Owner_is_the_first_list_that_holds_the_address()
    {
        var owners = AddressOwners.From(
            ("Telegram", ["149.154.160.0/20", "2001:b28:f23d::/48"]),
            ("Облако", ["149.154.0.0/16", "10.0.0.0/8", "10.1.0.0/16", "10.2.3.4"]));

        Assert.Equal("Telegram", owners.Of("149.154.167.220"));
        Assert.Equal("Облако", owners.Of("149.154.1.1"));
        Assert.Equal("Облако", owners.Of("10.1.2.3"));
        Assert.Equal("Облако", owners.Of("10.255.255.255"));
        Assert.Equal("Telegram", owners.Of("2001:b28:f23d::1"));
        Assert.Null(owners.Of("11.0.0.0"));
        Assert.Null(owners.Of("не адрес"));
        Assert.Null(owners.Of(null));
    }

    /// <summary>
    /// Настоящие списки копии Zapret GUI называют сеть верно, а списки, подписанные
    /// не тем, что в них лежит, не берутся: 3.64.0.0/12 из ipset-cloudflare1 — Amazon.
    /// </summary>
    [Fact]
    public void Real_lists_name_the_network_right()
    {
        var owners = AddressOwners.Load(Path.Combine(RepoRoot(), "vendor", "zapret-gui"));

        Assert.Equal("Meta", owners.Of("157.240.205.174"));
        Assert.Equal("Cloudflare", owners.Of("104.16.132.229"));
        Assert.Equal("Google", owners.Of("142.250.74.46"));
        Assert.Equal("Telegram", owners.Of("149.154.167.220"));
        Assert.Equal("Hetzner", owners.Of("95.217.1.1"));
        Assert.NotEqual("Cloudflare", owners.Of("3.64.0.1"));
        Assert.Null(owners.Of("192.168.1.1"));
    }

    /// <summary>
    /// Под <see cref="ProbePorts.Through"/> сокет пробы привязан к адаптеру
    /// (IP_UNICAST_IF), вне его — нет.
    /// </summary>
    [Fact]
    public void Probe_sockets_are_bound_to_the_adapter_only_inside_the_scope()
    {
        using (var plain = ProbePorts.Client(AddressFamily.InterNetwork))
            Assert.Equal(0, (int)plain.Client.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)!);

        // Адаптер — настоящий: к несуществующему Windows сокет не привязывает.
        // Без сети (CI без маршрута по умолчанию) проверять нечего.
        if (PhysicalNetwork.Interface() is not { } adapter)
            return;

        using (ProbePorts.Through(adapter))
        using (var bound = ProbePorts.Client(AddressFamily.InterNetwork))
        {
            Assert.Equal(adapter, ProbePorts.BoundAdapter);
            // Задаётся в сетевом порядке байт, читается — в обычном (замер 27.10: 24).
            Assert.Equal(adapter, (int)bound.Client.GetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31)!);
        }

        Assert.Null(ProbePorts.BoundAdapter);

        using (ProbePorts.Through(null))
            Assert.Null(ProbePorts.BoundAdapter);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("нет корня репозитория");
    }
}
