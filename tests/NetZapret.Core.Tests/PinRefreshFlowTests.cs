using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Ход автозамены пинов по образцу Zapret GUI (05.11): профиль пина, переезд
/// посредника, две неудачи подряд, контроль сети. Сеть подменена — тесты идут
/// на копии hosts во временной папке.
/// </summary>
public sealed class PinRefreshFlowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nz-pinflow-" + Guid.NewGuid().ToString("N"));
    private readonly string _hosts;
    private readonly string _suspects;

    public PinRefreshFlowTests()
    {
        Directory.CreateDirectory(_dir);
        _hosts = Path.Combine(_dir, "hosts");
        _suspects = Path.Combine(_dir, "pin-suspects.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Подменённая сеть: вердикты по адресам, ответы серверов по ключу.</summary>
    private sealed class FakeNet
    {
        public Dictionary<string, PinVerdict> Verdicts { get; } = new(StringComparer.Ordinal);

        /// <summary>Вердикт адреса с одним именем: «адрес имя».</summary>
        public Dictionary<string, PinVerdict> NameVerdicts { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, ServerAnswers> Replies { get; } = new(StringComparer.Ordinal);

        public bool Online { get; set; } = true;

        public List<string> Probed { get; } = [];

        public List<(string Key, IReadOnlyCollection<string> Names)> Asked { get; } = [];

        public int OnlineAsked { get; private set; }

        public PinNet Net => new()
        {
            Probe = (address, name, _) =>
            {
                Probed.Add(address);
                return Task.FromResult(NameVerdicts.TryGetValue(address + " " + name, out var byName)
                    ? byName
                    : Verdicts.GetValueOrDefault(address, PinVerdict.Dead));
            },
            Ask = (server, names, _) =>
            {
                Asked.Add((server.Key, names));
                return Task.FromResult(Replies.GetValueOrDefault(server.Key));
            },
            Online = _ =>
            {
                OnlineAsked++;
                return Task.FromResult(Online);
            },
        };
    }

    /// <summary>
    /// Ответ сервера: контрольные claude.ai и chatgpt.com получают его посредников
    /// (так их узнаёт <c>IntermediaryDns.Proxies</c>), прочие имена — как сказано.
    /// </summary>
    private static ServerAnswers Reply(string[] relays, params (string Name, string[] Addresses)[] names)
    {
        var map = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude.ai"] = relays.Select(IPAddress.Parse).ToList(),
            ["chatgpt.com"] = relays.Select(IPAddress.Parse).ToList(),
        };

        foreach (var (name, addresses) in names)
            map[name] = addresses.Select(IPAddress.Parse).ToList();

        return new(map, []);
    }

    private void Hosts(params string[] pins) =>
        File.WriteAllLines(_hosts,
        [
            "# hosts",
            HostsEditor.BlockBegin,
            "# Claude — Comss DNS",
            "# Записи ведёт NetZapret. Правьте их в окне, «Файл hosts»: при следующей",
            "# записи всё, что дописано сюда руками, будет потеряно.",
            .. pins,
            HostsEditor.BlockEnd,
            "",
        ]);

    private Task<PinRefreshResult> Run(FakeNet net, bool byHand = false) =>
        PinRefresh.CheckAsync(net.Net, _hosts, _suspects, byHand, CancellationToken.None);

    private string[] Lines() => File.ReadAllLines(_hosts).Select(l => l.Trim()).ToArray();

    [Fact]
    public async Task An_old_pin_gets_the_profile_of_the_intermediary_that_gives_its_address()
    {
        // chatgpt.com без профиля стоит на прокси Comss; grok.com мёртв, и кнопка
        // его меняет — с этой записью блока профиль chatgpt.com и пишется.
        Hosts("206.223.251.18 chatgpt.com", "45.13.237.213 grok.com # nz:via=geohide-eu");

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Verdicts["45.13.237.214"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["206.223.251.18"], ("chatgpt.com", ["206.223.251.18"]));
        net.Replies["geohide-eu"] = Reply(["45.13.237.214"], ("grok.com", ["45.13.237.214"]));

        var result = await Run(net, byHand: true);

        Assert.Null(result.Error);
        Assert.Contains("206.223.251.18 chatgpt.com # nz:via=comss", Lines());
        Assert.Contains("45.13.237.214 grok.com # nz:via=geohide-eu", Lines());
        Assert.Equal(1, result.Assigned);

        // Имя без профиля спрашивается у всех; имя с профилем — только у своего.
        Assert.Contains(net.Asked, a => a.Key == "astracat" && a.Names.Contains("chatgpt.com"));
        Assert.DoesNotContain(net.Asked, a => a.Key == "comss" && a.Names.Contains("grok.com"));
    }

    [Fact]
    public async Task Assignment_alone_does_not_rewrite_hosts()
    {
        Hosts("206.223.251.18 chatgpt.com");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["206.223.251.18"], ("chatgpt.com", ["206.223.251.18"]));

        var result = await Run(net);

        Assert.Empty(result.Changes);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task The_intermediary_that_moved_takes_the_pin_while_the_old_one_still_answers()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "206.223.251.18 claude.com # nz:via=comss");

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["157.22.181.209"], ("claude.com", ["157.22.181.209"]));

        var result = await Run(net);

        Assert.Null(result.Error);
        Assert.Single(result.Moved);
        Assert.Contains("Comss DNS", result.Moved[0]);
        Assert.Contains("157.22.181.209 claude.ai # nz:via=comss", Lines());
        Assert.Contains("157.22.181.209 claude.com # nz:via=comss", Lines());
        Assert.DoesNotContain(Lines(), l => l.StartsWith("206.223.251.18", StringComparison.Ordinal));

        // Новый посредник проверен один раз на оба имени, контроль сети не нужен —
        // старый жив.
        Assert.Equal(1, net.Probed.Count(a => a == "157.22.181.209"));
        Assert.Equal(0, net.OnlineAsked);
    }

    [Fact]
    public async Task A_new_intermediary_that_does_not_answer_is_not_taken()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["157.22.181.209"]);

        var result = await Run(net);

        Assert.Empty(result.Moved);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task A_silent_server_changes_nothing()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;

        var result = await Run(net, byHand: true);

        Assert.Contains("Comss DNS", result.Silent);
        Assert.Empty(result.Changes);
        Assert.Empty(result.Moved);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task One_miss_is_only_a_suspicion_and_the_second_in_a_row_replaces()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com # nz:via=geohide-eu");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["206.223.251.18", "157.22.181.209"], ("claude.ai", ["206.223.251.18", "157.22.181.209"]));

        var first = await Run(net);

        Assert.Empty(first.Changes);
        Assert.Equal(1, first.Suspects);
        Assert.Equal(before, File.ReadAllText(_hosts));
        Assert.Contains("206.223.251.18", PinRefresh.LoadSuspects(_suspects));

        var second = await Run(net);

        Assert.Single(second.Changes);
        Assert.Contains("157.22.181.209 claude.ai # nz:via=comss", Lines());
        Assert.DoesNotContain(Lines(), l => l.StartsWith("206.223.251.18", StringComparison.Ordinal));
        Assert.Equal(1, net.OnlineAsked);
    }

    [Fact]
    public async Task An_address_that_answered_in_between_is_no_longer_a_suspect()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com");

        var net = new FakeNet();
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["206.223.251.18", "157.22.181.209"], ("claude.ai", ["206.223.251.18", "157.22.181.209"]));

        await Run(net);

        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        await Run(net);

        // Ответил — подозрение снято; следующий промах снова только первый.
        net.Verdicts.Remove("206.223.251.18");
        var third = await Run(net);

        Assert.Empty(third.Changes);
        Assert.Contains("206.223.251.18 claude.ai # nz:via=comss", Lines());
    }

    [Fact]
    public async Task The_button_replaces_a_dead_address_at_once()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com");

        var net = new FakeNet();
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["206.223.251.18", "157.22.181.209"], ("claude.ai", ["206.223.251.18", "157.22.181.209"]));

        var result = await Run(net, byHand: true);

        Assert.Single(result.Changes);
        Assert.Contains("157.22.181.209 claude.ai # nz:via=comss", Lines());
    }

    [Fact]
    public async Task With_the_network_down_nothing_changes()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet { Online = false };
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["206.223.251.18", "157.22.181.209"], ("claude.ai", ["206.223.251.18", "157.22.181.209"]));

        var result = await Run(net, byHand: true);

        Assert.NotNull(result.Error);
        Assert.Contains("нет сети", result.Error);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task Most_addresses_silent_at_once_is_a_network_failure()
    {
        Hosts("1.0.0.1 a.example", "1.0.0.2 b.example", "1.0.0.3 c.example", "1.0.0.4 d.example");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["1.0.0.4"] = PinVerdict.Works;

        var result = await Run(net, byHand: true);

        Assert.Contains("сбой сети", result.Error);
        Assert.Equal(before, File.ReadAllText(_hosts));
        Assert.Equal(0, net.OnlineAsked);
    }

    [Fact]
    public async Task A_name_never_stays_without_an_address()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;

        var result = await Run(net, byHand: true);

        Assert.Equal(["claude.ai"], result.Dead);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task A_live_recorded_address_goes_first()
    {
        // Chrome, по замеру Zapret GUI 13.10, берёт только первую строку имени:
        // живой второй адрес встаёт первым, мёртвый остаётся за ним.
        Hosts("206.223.251.18 claude.ai", "157.22.181.209 claude.ai", "45.13.237.213 grok.com");

        var net = new FakeNet();
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;

        var result = await Run(net, byHand: true);

        Assert.Single(result.Reordered);

        var claude = Lines().Where(l => l.EndsWith(" claude.ai", StringComparison.Ordinal)).ToList();
        Assert.Equal(["157.22.181.209 claude.ai", "206.223.251.18 claude.ai"], claude);
    }

    [Fact]
    public async Task Zapret_dns_relay_check_names_its_intermediary()
    {
        // Посредник, которого контрольные имена не выдали: о нём говорит только
        // ответ на relay.check.dns.zapret.moe.
        Hosts("83.217.211.149 claude.ai # nz:via=zapret");

        var net = new FakeNet();
        net.Verdicts["83.217.211.149"] = PinVerdict.Works;
        net.Verdicts["5.129.221.64"] = PinVerdict.Works;
        net.Replies["zapret"] = new ServerAnswers(
            new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude.ai"] = [IPAddress.Parse("5.129.221.64")],
            },
            [IPAddress.Parse("5.129.221.64")]);

        var result = await Run(net);

        Assert.Single(result.Moved);
        Assert.Contains("5.129.221.64 claude.ai # nz:via=zapret", Lines());
        Assert.Equal("relay.check.dns.zapret.moe", IntermediaryDns.ByKey("zapret")!.RelayName);
    }

    [Fact]
    public async Task A_service_address_with_the_desync_mark_is_not_touched()
    {
        Hosts("163.70.151.174 www.instagram.com # nz:desync");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        var result = await Run(net, byHand: true);

        Assert.Equal(0, result.Checked);
        Assert.Empty(net.Probed);
        Assert.Empty(net.Asked);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public void Three_names_make_a_relay_only_among_names_of_its_profile()
    {
        // Comss 03.11 отдал три именам Google AI настоящий Google: эти имена без
        // профиля, и посредником Google не становится. Своим трём именам —
        // становится.
        static IReadOnlyList<IPAddress> A(params string[] a) => a.Select(IPAddress.Parse).ToList();

        var answers = new ServerAnswers(new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini.google.com"] = A("142.251.157.2"),
            ["aistudio.google.com"] = A("142.251.157.2"),
            ["generativelanguage.googleapis.com"] = A("142.251.157.2"),
            ["a.example"] = A("10.20.30.40", "0.0.0.0"),
            ["b.example"] = A("10.20.30.40", "0.0.0.0"),
            ["c.example"] = A("10.20.30.40", "0.0.0.0"),
        }, []);

        var relays = IntermediaryDns.Relays(answers, ["a.example", "b.example", "c.example"]);

        Assert.Equal(["10.20.30.40"], relays);
    }

    [Fact]
    public void Both_marks_survive_a_rewrite_by_another_service()
    {
        Hosts(
            "206.223.251.18 claude.ai # nz:via=comss",
            "163.70.151.174 www.instagram.com # nz:desync nz:via=geohide-eu",
            "1.2.3.4 other.example");

        HostsEditor.Pin(new Dictionary<string, string> { ["grok.com"] = "45.13.237.214" }, _hosts, note: "Grok — автоподбор",
            via: new Dictionary<string, string> { ["grok.com"] = "geohide-eu" });

        var lines = Lines();

        Assert.Contains("206.223.251.18 claude.ai # nz:via=comss", lines);
        Assert.Contains("163.70.151.174 www.instagram.com # nz:desync nz:via=geohide-eu", lines);
        Assert.Contains("45.13.237.214 grok.com # nz:via=geohide-eu", lines);
        Assert.Contains("1.2.3.4 other.example", lines);

        Assert.Contains("www.instagram.com", HostsEditor.DesyncPins(_hosts));
        Assert.Equal("comss", HostsEditor.PinsVia(_hosts)["claude.ai"]);

        var instagram = HostsTable.Read(_hosts).Single(r => r.Name == "www.instagram.com");
        Assert.Equal(HostsPinKind.Service, instagram.Kind);
    }

    [Fact]
    public void A_hand_pin_over_an_intermediary_drops_the_profile()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss");

        HostsEditor.Repin(new Dictionary<string, IReadOnlyList<string>> { ["claude.ai"] = ["9.9.9.9"] }, _hosts, note: "вручную");

        Assert.Contains("9.9.9.9 claude.ai", Lines());
        Assert.Empty(HostsEditor.PinsVia(_hosts));
    }

    [Fact]
    public void A_profile_mark_outside_our_block_is_not_trusted()
    {
        File.WriteAllLines(_hosts,
        [
            "206.223.251.18 claude.ai # nz:via=comss",
            HostsEditor.BlockBegin,
            "1.2.3.4 grok.com",
            HostsEditor.BlockEnd,
        ]);

        Assert.Empty(HostsEditor.PinsVia(_hosts));
    }

    [Fact]
    public async Task A_profiled_name_falls_back_to_another_intermediary_when_its_own_is_gone()
    {
        // Профиль GeoHide EU, его сервер молчит, первый адрес мёртв. Чужие серверы
        // об этом имени в первом круге не спрашивались — спрашиваются теперь.
        Hosts("45.13.237.213 claude.ai # nz:via=geohide-eu", "206.223.251.18 chatgpt.com # nz:via=comss");

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["206.223.251.18", "157.22.181.209"]);

        var result = await Run(net, byHand: true);

        Assert.Single(result.Changes);
        Assert.Contains(net.Asked, a => a.Key == "comss" && a.Names.Contains("claude.ai"));
        Assert.Contains("157.22.181.209 claude.ai # nz:via=comss", Lines());
        Assert.Contains("206.223.251.18 chatgpt.com # nz:via=comss", Lines());
    }

    [Fact]
    public async Task A_dead_head_moves_at_once_when_the_server_names_a_new_intermediary()
    {
        // Плановая проверка, первый промах: смерть ещё не доказана, но сервер
        // профиля уже отдаёт другой посредник — переезд проверен пробой.
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com");

        var net = new FakeNet();
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["157.22.181.209"]);

        var result = await Run(net);

        Assert.Single(result.Moved);
        Assert.Contains("157.22.181.209 claude.ai # nz:via=comss", Lines());
        Assert.Equal(0, net.OnlineAsked);
    }

    [Fact]
    public async Task A_live_pin_does_not_move_to_an_address_known_only_by_three_names()
    {
        // Comss бросил проксировать Google и отдал трём своим именам настоящий
        // Google. Живой пин посредника на него не уходит.
        Hosts(
            "206.223.251.18 gemini.google.com # nz:via=comss",
            "206.223.251.18 aistudio.google.com # nz:via=comss",
            "206.223.251.18 ai.google.dev # nz:via=comss");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Verdicts["142.251.157.2"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["157.22.181.209"],
            ("gemini.google.com", ["142.251.157.2"]),
            ("aistudio.google.com", ["142.251.157.2"]),
            ("ai.google.dev", ["142.251.157.2"]));

        var result = await Run(net);

        Assert.Empty(result.Moved);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task A_live_pin_stays_while_the_server_still_gives_its_address_to_other_names()
    {
        // Сервер отдал claude.ai другой узел, но старый — chatgpt.com и grok.com:
        // узлы перемешаны, а не сменены.
        Hosts("206.223.251.18 claude.ai # nz:via=comss");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = new ServerAnswers(new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude.ai"] = [IPAddress.Parse("157.22.181.209")],
            ["chatgpt.com"] = [IPAddress.Parse("157.22.181.209"), IPAddress.Parse("206.223.251.18")],
            ["grok.com"] = [IPAddress.Parse("206.223.251.18")],
        }, []);

        var result = await Run(net);

        Assert.Empty(result.Moved);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task A_live_pin_moves_only_if_the_second_question_says_the_same()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss");
        var before = File.ReadAllText(_hosts);

        var net = new FakeNet();
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        var moved = Reply(["157.22.181.209"]);
        var stayed = Reply(["206.223.251.18"]);
        int calls = 0;

        var fake = net.Net;
        var pinNet = new PinNet
        {
            Probe = fake.Probe,
            Online = fake.Online,
            Ask = (server, _, _) => Task.FromResult<ServerAnswers?>(server.Key != "comss" ? null : calls++ == 0 ? moved : stayed),
        };

        var result = await PinRefresh.CheckAsync(pinNet, _hosts, _suspects, false, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Empty(result.Moved);
        Assert.Equal(before, File.ReadAllText(_hosts));
    }

    [Fact]
    public async Task A_reordered_pin_keeps_its_profile()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "157.22.181.209 claude.ai # nz:via=comss", "45.13.237.213 grok.com");

        var net = new FakeNet();
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;

        var result = await Run(net, byHand: true);

        Assert.Single(result.Reordered);

        var claude = Lines().Where(l => l.Contains(" claude.ai", StringComparison.Ordinal)).ToList();
        Assert.Equal(["157.22.181.209 claude.ai # nz:via=comss", "206.223.251.18 claude.ai # nz:via=comss"], claude);
    }

    [Fact]
    public async Task A_refused_new_address_is_not_taken_but_another_name_does_not_decide_for_this_one()
    {
        // Новый посредник: api.anthropic.com на корне отвечает 403, claude.ai —
        // сайтом. Отказ первого имени не решает за второе.
        Hosts("206.223.251.18 api.anthropic.com # nz:via=comss", "206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com");

        var net = new FakeNet();
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Refused;
        net.NameVerdicts["157.22.181.209 claude.ai"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["157.22.181.209"], ("api.anthropic.com", ["157.22.181.209"]));

        var result = await Run(net, byHand: true);

        Assert.Contains("157.22.181.209 claude.ai # nz:via=comss", Lines());
        Assert.Contains("206.223.251.18 api.anthropic.com # nz:via=comss", Lines());
        Assert.Contains("api.anthropic.com", result.Dead);
    }

    [Fact]
    public async Task With_the_network_down_a_checked_move_is_still_written_and_misses_are_kept()
    {
        Hosts("206.223.251.18 claude.ai # nz:via=comss", "45.13.237.213 grok.com", "45.13.237.215 x.ai");
        File.WriteAllText(_suspects, "[\"45.13.237.215\"]");

        var net = new FakeNet { Online = false };
        net.Verdicts["206.223.251.18"] = PinVerdict.Works;
        net.Verdicts["45.13.237.213"] = PinVerdict.Works;
        net.Verdicts["157.22.181.209"] = PinVerdict.Works;
        net.Replies["comss"] = Reply(["157.22.181.209"]);

        var result = await Run(net);

        Assert.Contains("нет сети", result.Error);
        Assert.Single(result.Moved);
        Assert.Contains("157.22.181.209 claude.ai # nz:via=comss", Lines());
        Assert.Contains("45.13.237.215 x.ai", Lines());
        Assert.Contains("45.13.237.215", PinRefresh.LoadSuspects(_suspects));
    }

    [Fact]
    public async Task A_network_failure_clears_the_suspicion_of_an_address_that_answered()
    {
        Hosts("1.0.0.1 a.example", "1.0.0.2 b.example", "1.0.0.3 c.example", "1.0.0.4 d.example");
        File.WriteAllText(_suspects, "[\"1.0.0.1\",\"1.0.0.4\"]");

        var net = new FakeNet();
        net.Verdicts["1.0.0.4"] = PinVerdict.Works;

        var result = await Run(net);

        Assert.Contains("сбой сети", result.Error);

        // 1.0.0.4 ответил — подозрение снято; 1.0.0.2 и 1.0.0.3 молчат впервые,
        // но при сбое это не промах.
        Assert.Equal(["1.0.0.1"], PinRefresh.LoadSuspects(_suspects).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_profile_is_assigned_without_changing_addresses_only_if_they_are_the_same()
    {
        Hosts("206.223.251.18 chatgpt.com", "9.9.9.9 claude.ai", "45.13.237.213 grok.com");

        HostsEditor.Repin(
            new Dictionary<string, IReadOnlyList<string>> { ["grok.com"] = ["45.13.237.214"] },
            _hosts,
            via: new Dictionary<string, string> { ["grok.com"] = "geohide-eu", ["chatgpt.com"] = "comss", ["claude.ai"] = "comss" },
            assignedAt: new Dictionary<string, IReadOnlyList<string>>
            {
                ["chatgpt.com"] = ["206.223.251.18"],

                // claude.ai за время проверки прибит руками к другому адресу.
                ["claude.ai"] = ["206.223.251.18"],
            });

        var lines = Lines();

        Assert.Contains("206.223.251.18 chatgpt.com # nz:via=comss", lines);
        Assert.Contains("9.9.9.9 claude.ai", lines);
        Assert.Contains("45.13.237.214 grok.com # nz:via=geohide-eu", lines);
    }

    [Fact]
    public void The_intermediary_survives_collapsing_the_same_address_from_two_sources()
    {
        var picked = PinPicker.Distinct(
        [
            new PinCandidate("206.223.251.18", PinSource.Catalog, "Comss DNS, сейчас", "comss"),
            new PinCandidate("206.223.251.18", PinSource.Pool, "свой каталог"),
            new PinCandidate("9.9.9.9", PinSource.Pool, "свой каталог"),
        ]);

        Assert.Equal("comss", picked.Single(c => c.Address == "206.223.251.18").Via);
        Assert.Null(picked.Single(c => c.Address == "9.9.9.9").Via);
    }
}
