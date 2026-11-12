using NetZapret.Core.Rules;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Особые маршруты: пометка в rules.user.yaml и пины, которые она ставит в hosts.
/// </summary>
/// <remarks>
/// Владелец 12–13.11: пин сервиса, пин посредника и TG Proxy — особыми режимами
/// окна пути, с пометкой в файле маршрутов; пины при этом стоят в hosts, а файл
/// лишь дописывает и меняет их там.
/// </remarks>
public sealed class RoutePinsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"netzapret-routepins-{Guid.NewGuid():N}");
    private readonly string _hosts;
    private readonly string _rules;
    private readonly IReadOnlyList<PinSolution> _solutions;

    public RoutePinsTests()
    {
        Directory.CreateDirectory(_directory);
        _hosts = Path.Combine(_directory, "hosts");
        _rules = Path.Combine(_directory, "rules.user.yaml");

        File.WriteAllText(_hosts, "# hosts\r\n127.0.0.1 localhost\r\n");

        var solutions = Path.Combine(_directory, "pin-solutions.yaml");

        File.WriteAllText(solutions, """
            solutions:
              - id: instagram
                name: "Instagram"
                desync: true
                pins:
                  - addresses: ["2a03:2880:f330:25:face:b00c:0:4420", 163.70.151.174]
                    names: [instagram.com, www.instagram.com]
            """);

        _solutions = PinSolutions.Load(solutions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Hosts(params (string Name, string Address)[] pairs) =>
        pairs.ToDictionary(p => p.Name, p => (IReadOnlyList<string>)[p.Address], StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SpecialRouteSurvivesSaveAndLoad()
    {
        var file = UserRulesFile.Load(_rules);

        file.SetSpecial(MatchKind.HostList, "config/lists/instagram.txt", RouteSpecial.ServicePin, recipe: "multidisorder", pin: "instagram");
        file.SetSpecial(MatchKind.HostList, "config/lists/claude.txt", RouteSpecial.RelayPin, pin: "Zapret DNS",
            hosts: Hosts(("claude.ai", "5.129.221.64"), ("www.claude.ai", "5.129.221.64")));
        file.SetSpecial(MatchKind.IpSet, "config/lists/ipset-telegram.txt", RouteSpecial.TgProxy);
        file.Save();

        var text = File.ReadAllText(_rules);
        Assert.Contains("special: service-pin", text);
        Assert.Contains("special: relay-pin", text);
        Assert.Contains("special: tg-proxy", text);
        Assert.Contains("\"claude.ai\": [\"5.129.221.64\"]", text);

        var back = UserRulesFile.Load(_rules).Entries;

        var instagram = back.Single(e => e.Value.EndsWith("instagram.txt"));
        Assert.Equal(RouteSpecial.ServicePin, instagram.Special);
        Assert.Equal(RoutingMode.Desync, instagram.Mode);
        Assert.Equal("multidisorder", instagram.Recipe);
        Assert.Equal("instagram", instagram.Pin);

        var claude = back.Single(e => e.Value.EndsWith("claude.txt"));
        Assert.Equal(RouteSpecial.RelayPin, claude.Special);
        Assert.Equal(RoutingMode.Direct, claude.Mode);
        Assert.Equal(["5.129.221.64"], claude.PinHosts["www.claude.ai"]);
        Assert.Equal("пин посредника Zapret DNS", claude.DescribeMode());

        var telegram = back.Single(e => e.Match == MatchKind.IpSet);
        Assert.Equal(RouteSpecial.TgProxy, telegram.Special);
        Assert.Equal(RoutingMode.Desync, telegram.Mode);
    }

    /// <summary>Пометку ставит только особый маршрут; смена рецепта её не снимает, смена режима — снимает.</summary>
    [Fact]
    public void PlainSetKeepsSpecialOnlyWithSameMode()
    {
        var file = UserRulesFile.Load(_rules);
        file.SetSpecial(MatchKind.HostList, "config/lists/instagram.txt", RouteSpecial.ServicePin, pin: "instagram");

        file.Set(MatchKind.HostList, "config/lists/instagram.txt", RoutingMode.Desync, recipe: "syndata");
        Assert.Equal(RouteSpecial.ServicePin, file.Entries[0].Special);
        Assert.Equal("syndata", file.Entries[0].Recipe);

        file.Set(MatchKind.HostList, "config/lists/instagram.txt", RoutingMode.Proxy);
        Assert.Equal(RouteSpecial.None, file.Entries[0].Special);
        Assert.Null(file.Entries[0].Pin);
    }

    /// <summary>Незнакомое слово — от программы новее — не роняет файл, а пропускается.</summary>
    [Fact]
    public void UnknownSpecialDoesNotBreakTheFile()
    {
        File.WriteAllText(_rules, """
            rules:
              - match: hostlist
                value: "config/lists/discord.txt"
                mode: desync
                special: something-new
            """);

        var entry = Assert.Single(UserRulesFile.Load(_rules).Entries);
        Assert.Equal(RouteSpecial.None, entry.Special);
        Assert.Equal(RoutingMode.Desync, entry.Mode);
    }

    [Fact]
    public void ServicePinGoesToHostsWithDesyncMark()
    {
        var entry = Entry(RouteSpecial.ServicePin, "instagram");

        var result = RoutePins.Apply(entry, haveIpV6: false, _hosts, _solutions);

        Assert.NotNull(result);
        Assert.Equal(["163.70.151.174"], HostsEditor.PinsAll(_hosts)["instagram.com"]);
        Assert.Contains("www.instagram.com", HostsEditor.DesyncPins(_hosts));

        var state = RoutePins.StateOf(entry, _hosts, _solutions);
        Assert.True(state.Whole);
    }

    [Fact]
    public void RelayPinCarriesIntermediaryProfile()
    {
        var entry = Entry(RouteSpecial.RelayPin, "Zapret DNS", Hosts(("claude.ai", "5.129.221.64")));

        RoutePins.Apply(entry, haveIpV6: false, _hosts, _solutions);

        Assert.Equal("5.129.221.64", HostsEditor.Pins(_hosts)["claude.ai"]);
        Assert.Equal("zapret", HostsEditor.PinsVia(_hosts)["claude.ai"]);
        Assert.DoesNotContain("claude.ai", HostsEditor.DesyncPins(_hosts));
    }

    /// <summary>Снимается только своё: имя, перебитое человеком своим адресом, остаётся.</summary>
    [Fact]
    public void RemoveTakesOnlyNamesOnRecordedAddresses()
    {
        var entry = Entry(RouteSpecial.RelayPin, "Zapret DNS",
            Hosts(("claude.ai", "5.129.221.64"), ("www.claude.ai", "5.129.221.64")));

        RoutePins.Apply(entry, haveIpV6: false, _hosts, _solutions);
        HostsEditor.Pin(new Dictionary<string, string> { ["www.claude.ai"] = "1.2.3.4" }, _hosts);

        var removed = RoutePins.Remove(entry, _hosts, _solutions);

        Assert.Equal(["claude.ai"], removed);
        Assert.Equal("1.2.3.4", HostsEditor.Pins(_hosts)["www.claude.ai"]);
        Assert.False(HostsEditor.Pins(_hosts).ContainsKey("claude.ai"));
    }

    /// <summary>Расхождение записи и hosts видно: так его покажет отчёт.</summary>
    [Fact]
    public void StateShowsMovedAndMissingNames()
    {
        var entry = Entry(RouteSpecial.RelayPin, "Comss DNS",
            Hosts(("chatgpt.com", "80.74.30.162"), ("api.openai.com", "80.74.30.162"), ("openai.com", "80.74.30.162")));

        File.AppendAllText(_hosts, "80.74.30.162 chatgpt.com\r\n9.9.9.9 api.openai.com\r\n");

        var state = RoutePins.StateOf(entry, _hosts, _solutions);

        Assert.Equal(1, state.Pinned);
        Assert.Equal(3, state.Total);
        Assert.Equal(["9.9.9.9"], state.Moved["api.openai.com"]);
        Assert.Equal(["openai.com"], state.Missing);
    }

    /// <summary>Автозамена перевела имя — запись в файле маршрутов идёт следом.</summary>
    [Fact]
    public void RepinMovesRecordedAddresses()
    {
        var file = UserRulesFile.Load(_rules);
        file.SetSpecial(MatchKind.HostList, "config/lists/claude.txt", RouteSpecial.RelayPin, pin: "Zapret DNS",
            hosts: Hosts(("claude.ai", "5.129.221.64"), ("www.claude.ai", "5.129.221.64")));
        file.Save();

        int changed = UserRulesFile.RepinSaved(Hosts(("claude.ai", "5.129.221.70"), ("unrelated.com", "1.1.1.1")), _rules);

        Assert.Equal(1, changed);

        var entry = UserRulesFile.Load(_rules).Entries.Single();
        Assert.Equal(["5.129.221.70"], entry.PinHosts["claude.ai"]);
        Assert.Equal(["5.129.221.64"], entry.PinHosts["www.claude.ai"]);

        Assert.Equal(0, UserRulesFile.RepinSaved(Hosts(("claude.ai", "5.129.221.70")), _rules));
    }

    /// <summary>Загрузка чужого файла ставит пины его особых маршрутов.</summary>
    [Fact]
    public void ImportedBookPutsPinsIntoHosts()
    {
        var source = Path.Combine(_directory, "theirs.yaml");

        File.WriteAllText(source, """
            rules:
              - match: hostlist
                value: "config/lists/chatgpt.txt"
                mode: direct
                special: relay-pin
                pin: "WLRUS DNS"
                pin_hosts:
                  "chatgpt.com": ["191.217.212.16"]
              - match: hostlist
                value: "config/lists/discord.txt"
                mode: desync
            """);

        var incoming = RulesShare.Read(source);
        var lines = RoutePins.ApplyAll(incoming, haveIpV6: false, _hosts);

        var line = Assert.Single(lines);
        Assert.Contains("поставлен", line);
        Assert.Equal("191.217.212.16", HostsEditor.Pins(_hosts)["chatgpt.com"]);
    }

    [Fact]
    public void ReportNamesPinAndDrift()
    {
        var entry = Entry(RouteSpecial.RelayPin, "Comss DNS",
            Hosts(("chatgpt.com", "80.74.30.162"), ("api.openai.com", "80.74.30.162")));

        File.AppendAllText(_hosts, "80.74.30.162 chatgpt.com\r\n9.9.9.9 api.openai.com\r\n");

        var text = RoutePins.Report([entry, Entry(RouteSpecial.TgProxy, "")], _hosts);

        Assert.Contains("пин посредника Comss DNS", text);
        Assert.Contains("в hosts на записанных адресах: 1 из 2", text);
        Assert.Contains("api.openai.com: в hosts 9.9.9.9, записано 80.74.30.162", text);
        Assert.Contains("TG Proxy", text);
    }

    [Fact]
    public void CompareSeesSpecialChange()
    {
        var plain = new UserRuleEntry { Match = MatchKind.HostList, Value = "config/lists/claude.txt", Mode = RoutingMode.Direct };
        var pinned = plain with { Special = RouteSpecial.RelayPin, Pin = "Zapret DNS" };

        Assert.Single(RulesShare.Compare([plain], [pinned]).Changed);
    }

    private static UserRuleEntry Entry(
        RouteSpecial special,
        string pin,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? hosts = null) => new()
    {
        Match = MatchKind.HostList,
        Value = "config/lists/x.txt",
        Mode = RouteSpecials.ModeOf(special),
        Special = special,
        Pin = pin,
        PinHosts = hosts ?? UserRuleEntry.NoHosts,
    };
}
