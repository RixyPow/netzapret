using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Пины двух родов и готовые решения с пином.
/// </summary>
/// <remarks>
/// Замер 06.10, A/B/A на работающем winws2: пин на прокси XBOX под рецептом
/// своей секции — 0 из 10 (со щитом 20 из 20), пин на адрес самого сервиса
/// из каталога «Напрямую» — 6 из 6 под рецептом и 0 из 22 со щитом.
/// Отсюда пометка <see cref="HostsEditor.DesyncMark"/>: щит берёт пин
/// посредника и не берёт пин на адрес сервиса.
/// </remarks>
public sealed class PinSolutionsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"netzapret-pinsol-{Guid.NewGuid():N}");
    private readonly string _hosts;
    private readonly string _solutions;

    public PinSolutionsTests()
    {
        Directory.CreateDirectory(_directory);
        _hosts = Path.Combine(_directory, "hosts");
        _solutions = Path.Combine(_directory, "pin-solutions.yaml");

        File.WriteAllText(_hosts, "# hosts\r\n127.0.0.1 localhost\r\n");

        File.WriteAllText(_solutions, """
            solutions:
              - id: instagram
                name: "Instagram"
                note: "Адреса самой Meta."
                desync: true
                pins:
                  - addresses: ["2a03:2880:f330:25:face:b00c:0:4420", 163.70.151.174]
                    names: [instagram.com, www.instagram.com]
                  - addresses: [157.240.224.63]
                    names: [scontent.cdninstagram.com]
              - id: github-content
                name: "GitHub: загрузки и картинки"
                desync: true
                needs: ipv6
                pins:
                  - addresses: ["2606:50c0:8000::154"]
                    names: [avatars.githubusercontent.com]
              - id: supercell
                name: "Supercell"
                desync: false
                pins:
                  - addresses: [45.95.233.23]
                    names: [game.mocogame.com]
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private PinSolution Solution(string id) => PinSolutions.Load(_solutions).Single(s => s.Id == id);

    [Fact]
    public void DesyncPinIsMarkedAndLeftOutOfTheShield()
    {
        HostsEditor.PinWithDesync(new Dictionary<string, IReadOnlyList<string>>
        {
            ["www.instagram.com"] = ["163.70.151.174"],
        }, _hosts);

        HostsEditor.Pin(new Dictionary<string, string> { ["chatgpt.com"] = "87.228.47.203" }, _hosts);

        Assert.Contains("163.70.151.174 www.instagram.com # nz:desync", File.ReadAllLines(_hosts));
        Assert.Equal(["www.instagram.com"], HostsEditor.DesyncPins(_hosts));

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules: []
            """);

        var excluded = HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts);

        // Пин посредника щит держит, пин на адрес сервиса — нет.
        Assert.Contains("chatgpt.com", excluded);
        Assert.DoesNotContain("www.instagram.com", excluded);
    }

    [Fact]
    public void ProxyPinOverAServiceAddressDropsTheMark()
    {
        HostsEditor.PinWithDesync(new Dictionary<string, IReadOnlyList<string>>
        {
            ["instagram.com"] = ["163.70.151.174"],
            ["www.instagram.com"] = ["163.70.151.174"],
        }, _hosts);

        // Человек перебил одно имя посредником — его пометка уходит,
        // у соседнего остаётся.
        HostsEditor.Pin(new Dictionary<string, string> { ["instagram.com"] = "87.228.47.199" }, _hosts);

        Assert.Equal(["www.instagram.com"], HostsEditor.DesyncPins(_hosts));
    }

    [Fact]
    public void ForeignMarkIsNotTrusted()
    {
        // Пометка вне нашего блока ничего не значит: чужую строку мы не ставили.
        File.AppendAllText(_hosts, "163.70.151.174 instagram.com # nz:desync\r\n");

        Assert.Empty(HostsEditor.DesyncPins(_hosts));
    }

    [Fact]
    public void WithoutIpV6OnlyIpV4AddressesAreWritten()
    {
        var entries = PinSolutions.Entries(Solution("instagram"), haveIpV6: false);

        Assert.Equal(["163.70.151.174"], entries["www.instagram.com"]);
        Assert.Equal(2, PinSolutions.Entries(Solution("instagram"), haveIpV6: true)["www.instagram.com"].Count);
    }

    [Fact]
    public void IpV6OnlySolutionIsUnavailableWithoutIpV6()
    {
        Assert.Equal("Нужен IPv6 — сейчас его нет", PinSolutions.Unavailable(Solution("github-content"), haveIpV6: false));
        Assert.Null(PinSolutions.Unavailable(Solution("github-content"), haveIpV6: true));
        Assert.Throws<InvalidOperationException>(() => PinSolutions.Enable(Solution("github-content"), false, _hosts));
    }

    [Fact]
    public void EnableAndDisableRoundTrip()
    {
        var instagram = Solution("instagram");

        PinSolutions.Enable(instagram, haveIpV6: false, _hosts);

        var state = PinSolutions.StateOf(instagram, _hosts);
        Assert.True(state.On);
        Assert.False(state.Partly);
        Assert.Equal(3, state.Total);
        Assert.Equal(3, HostsEditor.DesyncPins(_hosts).Count);

        PinSolutions.Disable(instagram, _hosts);

        Assert.False(PinSolutions.StateOf(instagram, _hosts).On);
        Assert.Empty(HostsEditor.PinsAll(_hosts));
    }

    [Fact]
    public void DisableLeavesNamesThePersonRepinned()
    {
        var instagram = Solution("instagram");

        PinSolutions.Enable(instagram, haveIpV6: false, _hosts);
        HostsEditor.Pin(new Dictionary<string, string> { ["instagram.com"] = "87.228.47.199" }, _hosts);

        var state = PinSolutions.StateOf(instagram, _hosts);
        Assert.True(state.Partly);
        Assert.Equal(1, state.Elsewhere);

        PinSolutions.Disable(instagram, _hosts);

        Assert.Equal(["instagram.com"], HostsEditor.PinsAll(_hosts).Keys);
    }

    [Fact]
    public void IntermediarySolutionStaysUnderTheShield()
    {
        PinSolutions.Enable(Solution("supercell"), haveIpV6: false, _hosts);

        Assert.Empty(HostsEditor.DesyncPins(_hosts));

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules: []
            """);

        Assert.Contains("game.mocogame.com", HostsFile.CollectDesyncExclusions(engine.RuleSet, _hosts));
    }

    [Fact]
    public void DirectRouteIsReportedAsBlocker()
    {
        var instagram = Solution("instagram");
        PinSolutions.Enable(instagram, haveIpV6: false, _hosts);

        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: domain
                value: "*.instagram.com"
                mode: direct
              - match: domain
                value: "*.cdninstagram.com"
                mode: proxy
            """);

        // «Напрямую» десинк снимет; «через VPN» без туннеля — тоже, с туннелем — нет.
        var withoutTunnel = PinSolutions.RouteBlockers(instagram, engine.RuleSet, tunnelUp: false, _hosts);
        var withTunnel = PinSolutions.RouteBlockers(instagram, engine.RuleSet, tunnelUp: true, _hosts);

        Assert.Equal(3, withoutTunnel.Count);
        Assert.Equal(["instagram.com", "www.instagram.com"], withTunnel.Select(b => b.Name).Order());
        Assert.All(withTunnel, b => Assert.Equal(DesyncBypass.Direct, b.Why));
    }

    [Fact]
    public async Task PinRefreshLeavesServiceAddressesAlone()
    {
        // Только пины с пометкой — обновлять нечего, и в сеть он не идёт.
        PinSolutions.Enable(Solution("instagram"), haveIpV6: false, _hosts);

        var result = await PinRefresh.RunAsync(CancellationToken.None, _hosts);

        Assert.Empty(result.Changes);
        Assert.Equal(0, result.Checked);
        Assert.Equal(3, HostsEditor.DesyncPins(_hosts).Count);
    }

    /// <summary>Поставляемый файл разбирается целиком: он собран машиной из каталога Zapret GUI.</summary>
    [Fact]
    public void ShippedSolutionsParse()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
        {
            var path = Path.Combine(d.FullName, "config", "pin-solutions.yaml");

            if (!File.Exists(path))
                continue;

            var all = PinSolutions.Load(path);

            Assert.Equal(12, all.Count);
            Assert.Equal(["instagram", "x", "youtube", "discord", "discord-voice"], all.Take(5).Select(s => s.Id));

            // Посредник в решениях один — Supercell; остальным десинк нужен.
            Assert.Equal(["supercell"], all.Where(s => !s.Desync).Select(s => s.Id));
            Assert.Equal(["whatsapp", "github-content"], all.Where(s => s.NeedsIpV6).Select(s => s.Id));
            Assert.Equal(348, all.Single(s => s.Id == "discord-voice").Names.Count);
            return;
        }

        Assert.Fail("config/pin-solutions.yaml не найден");
    }
}
