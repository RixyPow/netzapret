using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Один маршрут или папка файлом (03.11): с одной машины на другую — с путями,
/// рецептами, пулами и своими списками.
/// </summary>
public sealed class RouteBundlesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nz-bundle-{Guid.NewGuid():N}");

    /// <summary>Машина отправителя.</summary>
    private readonly Machine _from;

    /// <summary>Машина получателя.</summary>
    private readonly Machine _to;

    public RouteBundlesTests()
    {
        _from = new Machine(Path.Combine(_dir, "from"));
        _to = new Machine(Path.Combine(_dir, "to"));
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

    private sealed class Machine
    {
        public Machine(string root)
        {
            Root = root;
            Directory.CreateDirectory(Path.Combine(root, "config"));
        }

        public string Root { get; }

        public string RulesPath => Path.Combine(Root, "config", "rules.user.yaml");

        public string SettingsPath => Path.Combine(Root, "config", "netzapret.json");

        public string Backups => Path.Combine(Root, "runtime");

        public UserRulesFile Rules => UserRulesFile.Load(RulesPath);

        public AppSettings Settings => AppSettings.Load(SettingsPath);

        public string Full(string value) => Path.Combine(Root, value.Replace('/', Path.DirectorySeparatorChar));

        public void Edit(Action<UserRulesFile> change)
        {
            var rules = Rules;
            change(rules);
            rules.Save();
        }

        public void Set(Func<AppSettings, AppSettings> change) => change(Settings).Save(SettingsPath);

        /// <summary>Свой список с правилом — как его заводит окно.</summary>
        public string OwnList(string name, RoutingMode mode, params string[] names)
        {
            var value = OwnLists.Create(name, names, Root);
            Edit(r => r.Set(MatchKind.HostList, value, mode));
            return value;
        }

        public IReadOnlyList<string> Names(string value) => OwnLists.Names(value, Root);

        public UserRuleEntry? Rule(MatchKind match, string value) =>
            Rules.Entries.FirstOrDefault(e => e.Match == match && e.Matches(value));

        public RouteImportPlan Plan(string path) => RouteBundles.Plan(RouteBundles.Read(path), Rules, Settings, Root);

        public RouteImportResult Load(string path) =>
            RouteBundles.Apply(Plan(path), RulesPath, SettingsPath, Root, Backups);
    }

    private string Export(
        Machine machine,
        string? folder,
        params (MatchKind Match, string Value)[] routes)
    {
        var path = Path.Combine(_dir, $"bundle-{Guid.NewGuid():N}.yaml");
        RouteBundles.Write(RouteBundles.Export(routes, folder, machine.Rules, machine.Settings, machine.Root), path);
        return path;
    }

    private const string Voice = "config/lists/discord-media.txt";
    private const string DiscordText = "config/lists/discord.txt";
    private const string DiscordImages = "config/lists/discord-images.txt";

    [Fact]
    public void ANewOwnListArrivesWithItsNamesPathAndRecipe()
    {
        var svtv = _from.OwnList("svtv", RoutingMode.Desync, "svtv.org", "cdn.svtv.org", "45.82.160.0/24");
        _from.Edit(r => r.Set(MatchKind.HostList, svtv, RoutingMode.Desync, recipe: "seqovl-4pda"));

        var file = Export(_from, null, (MatchKind.HostList, svtv));
        var plan = _to.Plan(file);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(RouteImportAction.Add, step.Action);
        Assert.Equal("svtv", step.Name);

        var result = _to.Load(file);

        Assert.Equal(1, result.Added);
        Assert.Equal(["svtv.org", "cdn.svtv.org", "45.82.160.0/24"], _to.Names(svtv));

        var rule = _to.Rule(MatchKind.HostList, svtv);
        Assert.NotNull(rule);
        Assert.Equal(RoutingMode.Desync, rule.Mode);
        Assert.Equal("seqovl-4pda", rule.Recipe);

        // Маршрут сам по себе: в папки и порядок одиночный не лезет.
        Assert.Empty(_to.Settings.RouteFolders);
        Assert.Empty(_to.Settings.RouteOrder);

        // Загрузили второй раз — менять нечего.
        Assert.True(_to.Plan(file).Nothing);
    }

    [Fact]
    public void TheSameRouteIsReplacedWithItsListAndTheOldOneIsKept()
    {
        var svtv = _from.OwnList("svtv", RoutingMode.Proxy, "svtv.org", "svtv.tv");
        _to.OwnList("svtv", RoutingMode.Direct, "svtv.org", "old.svtv.org");

        var file = Export(_from, null, (MatchKind.HostList, svtv));
        var step = Assert.Single(_to.Plan(file).Steps);

        Assert.Equal(RouteImportAction.Replace, step.Action);
        Assert.Contains("напрямую", step.Describe());
        Assert.Contains("VPN", step.Describe());

        var result = _to.Load(file);

        Assert.Equal(1, result.Replaced);
        Assert.Equal(RoutingMode.Proxy, _to.Rule(MatchKind.HostList, svtv)!.Mode);
        Assert.Equal(["svtv.org", "svtv.tv"], _to.Names(svtv));
        Assert.Single(_to.Rules.Entries);

        // Прежние правила и список — копией: передумал — вернул.
        Assert.NotNull(result.Backup);
        Assert.True(File.Exists(Path.Combine(result.Backup, "rules.user.yaml")));
        Assert.Contains("old.svtv.org", File.ReadAllText(Path.Combine(result.Backup, "config", "lists", "own", "svtv.txt")));
    }

    /// <summary>Совпало одно название — это чужой список, и присланный ложится рядом, а не поверх.</summary>
    [Fact]
    public void AListWithTheSameNameButOtherNamesIsNotOverwritten()
    {
        var games = _from.OwnList("games", RoutingMode.Proxy, "roblox.com");
        _to.OwnList("games", RoutingMode.Direct, "steampowered.com");

        var file = Export(_from, null, (MatchKind.HostList, games));
        var step = Assert.Single(_to.Plan(file).Steps);

        Assert.Equal(RouteImportAction.Add, step.Action);
        Assert.Equal(OwnLists.PathFor("games-2"), step.Route.Value);
        Assert.Contains("games-2", step.Note);

        _to.Load(file);

        Assert.Equal(["steampowered.com"], _to.Names(games));
        Assert.Equal(RoutingMode.Direct, _to.Rule(MatchKind.HostList, games)!.Mode);
        Assert.Equal(["roblox.com"], _to.Names(OwnLists.PathFor("games-2")));
        Assert.Equal(RoutingMode.Proxy, _to.Rule(MatchKind.HostList, OwnLists.PathFor("games-2"))!.Mode);

        // Тот же файл ещё раз — «games-2» узнаётся по общим именам, «games-3» не заводится.
        var again = Assert.Single(_to.Plan(file).Steps);
        Assert.Equal(OwnLists.PathFor("games-2"), again.Route.Value);
        Assert.Equal(RouteImportAction.Same, again.Action);
    }

    [Fact]
    public void AFolderCarriesEveryRouteWithItsOwnPathAndOrder()
    {
        var svtv = _from.OwnList("svtv", RoutingMode.Desync, "svtv.org");

        _from.Edit(r =>
        {
            r.Set(MatchKind.Domain, "*.example.com", RoutingMode.Proxy, server: ServerPools.RuleServer("aaaa1111"));
            r.Set(MatchKind.HostList, Voice, RoutingMode.Proxy);
            r.Set(MatchKind.Ip, "1.2.3.0/24", RoutingMode.Direct);
        });

        _from.Set(s => s with
        {
            Pools = [new ServerPool("aaaa1111", "Россия", ["srv-1"])],
            MadeFolders = ["Мои"],
            FolderLabels = new Dictionary<string, string> { ["Мои"] = "Мои сайты" },
        });

        var file = Export(_from, "Мои",
            (MatchKind.Ip, "1.2.3.0/24"),
            (MatchKind.HostList, Voice),
            (MatchKind.Domain, "*.example.com"),
            (MatchKind.HostList, svtv));

        // У получателя пул с тем же именем, но своим ключом.
        _to.Set(s => s with { Pools = [new ServerPool("bbbb2222", "россия", ["srv-9"])] });

        var plan = _to.Plan(file);

        Assert.True(plan.NewFolder);
        Assert.Equal(3, plan.Steps.Count(s => s.Action == RouteImportAction.Add));
        Assert.All(plan.Steps, s => Assert.Null(s.Note));

        // «Голос» у получателя есть и так — по умолчанию; его путь заменяется.
        Assert.Equal("Discord · Голос: по умолчанию → VPN", plan.Steps[1].Describe());

        _to.Load(file);

        Assert.Equal(ServerPools.RuleServer("bbbb2222"), _to.Rule(MatchKind.Domain, "*.example.com")!.Server);
        Assert.Equal(RoutingMode.Proxy, _to.Rule(MatchKind.HostList, Voice)!.Mode);
        Assert.Equal(RoutingMode.Direct, _to.Rule(MatchKind.Ip, "1.2.3.0/24")!.Mode);
        Assert.Equal(RoutingMode.Desync, _to.Rule(MatchKind.HostList, svtv)!.Mode);
        Assert.Equal(["svtv.org"], _to.Names(svtv));

        var settings = _to.Settings;
        Assert.Contains("Мои", settings.MadeFolders);
        Assert.Equal("Мои сайты", settings.FolderLabels["Мои"]);

        // Каждый — в папке, «Голос» Discord переложен из своей.
        var keys = new[]
        {
            RouteFolders.KeyOf(MatchKind.Ip, "1.2.3.0/24"),
            RouteFolders.KeyOf(MatchKind.HostList, Voice),
            RouteFolders.KeyOf(MatchKind.Domain, "*.example.com"),
            RouteFolders.KeyOf(MatchKind.HostList, svtv),
        };

        Assert.All(keys, k => Assert.Equal("Мои", settings.RouteFolders[k]));

        // Порядок в папке — как у отправителя.
        Assert.Equal([RouteFolders.Place("Мои"), .. keys], settings.RouteOrder);
    }

    [Fact]
    public void AMissingPoolLeadsIntoTheDefaultPoolAndSaysSo()
    {
        _from.Edit(r => r.Set(MatchKind.Domain, "*.example.com", RoutingMode.Proxy, server: ServerPools.RuleServer("aaaa1111")));
        _from.Set(s => s with { Pools = [new ServerPool("aaaa1111", "Россия", ["srv-1"])] });

        var file = Export(_from, null, (MatchKind.Domain, "*.example.com"));

        Assert.Contains("pool: \"Россия\"", File.ReadAllText(file));
        Assert.DoesNotContain("aaaa1111", File.ReadAllText(file));

        var step = Assert.Single(_to.Plan(file).Steps);

        Assert.Equal(RouteImportAction.Add, step.Action);
        Assert.Contains("Россия", step.Note);
        Assert.Null(step.Server);

        _to.Load(file);

        var rule = _to.Rule(MatchKind.Domain, "*.example.com")!;
        Assert.Equal(RoutingMode.Proxy, rule.Mode);
        Assert.Null(rule.Server);
    }

    /// <summary>Папка каталога едет вся: и выбранное, и оставленное по умолчанию.</summary>
    [Fact]
    public void ACatalogFolderBringsItsDefaultsBack()
    {
        _from.Edit(r => r.Set(MatchKind.HostList, Voice, RoutingMode.Proxy));
        _to.Edit(r => r.Set(MatchKind.HostList, DiscordText, RoutingMode.Desync, recipe: "multisplit"));

        var file = Export(_from, "Discord", (MatchKind.HostList, Voice), (MatchKind.HostList, DiscordText));
        var plan = _to.Plan(file);

        Assert.False(plan.NewFolder);
        Assert.Equal(RouteImportAction.Replace, plan.Steps[0].Action);

        var text = plan.Steps[1];
        Assert.Equal(RouteImportAction.Replace, text.Action);
        Assert.Equal("Discord · Сайт и переписка: десинк, рецепт «multisplit» → по умолчанию", text.Describe());

        // Прочие части Discord у получателя остаются в папке как были.
        Assert.True(plan.Staying > 0);

        _to.Load(file);

        Assert.Null(_to.Rule(MatchKind.HostList, DiscordText));
        Assert.Equal(RoutingMode.Proxy, _to.Rule(MatchKind.HostList, Voice)!.Mode);

        // Части Discord лежат дома — перекладки не пишется.
        Assert.Empty(_to.Settings.RouteFolders);
    }

    [Fact]
    public void AProgramFolderArrivesWithListsAutofillAndAtTheTop()
    {
        var settingsFrom = _from.SettingsPath;
        Assert.True(ProgramRoutes.Add("REPO.exe", settingsFrom, _from.RulesPath, _from.Root));
        ProgramRoutes.SetAutoFill("REPO.exe", false, settingsFrom);

        var domains = ProgramRoutes.DomainsPath("REPO.exe");
        var addresses = ProgramRoutes.AddressesPath("REPO.exe");

        File.AppendAllText(_from.Full(domains), "photonengine.io\r\nexitgames.com\r\n");
        File.AppendAllText(_from.Full(addresses), "82.117.225.0/24\r\n");
        _from.Edit(r =>
        {
            r.Set(MatchKind.HostList, domains, RoutingMode.Proxy);
            r.Set(MatchKind.HostList, addresses, RoutingMode.Desync);
        });

        var file = Export(_from, "REPO.exe", (MatchKind.HostList, domains), (MatchKind.HostList, addresses));

        _to.Edit(r => r.Set(MatchKind.Domain, "*.example.com", RoutingMode.Direct));

        var plan = _to.Plan(file);
        Assert.True(plan.NewProgram);
        Assert.Equal("REPO.exe", plan.Program);

        _to.Load(file);

        var route = Assert.Single(_to.Settings.ProgramRoutes);
        Assert.Equal("REPO.exe", route.Program);
        Assert.False(route.AutoFill);

        Assert.Equal(["photonengine.io", "exitgames.com"], _to.Names(domains));
        Assert.Equal(["82.117.225.0/24"], _to.Names(addresses));

        var entries = _to.Rules.Entries;
        Assert.True(entries[0].Matches(domains));
        Assert.Equal(RoutingMode.Proxy, entries[0].Mode);
        Assert.True(entries[1].Matches(addresses));
        Assert.Equal(RoutingMode.Desync, entries[1].Mode);
        Assert.Contains("Маршрут программы REPO.exe", File.ReadAllText(_to.Full(domains)));
    }

    /// <summary>
    /// Ссылка с ключом, путь машины, UUID, тег сервера — ничего из этого
    /// в пакет не попадает, даже если лежит в своём списке.
    /// </summary>
    [Fact]
    public void TheFileCarriesNoSecrets()
    {
        var mine = _from.OwnList("mine", RoutingMode.Proxy,
            "example.com",
            "vless://123e4567-e89b-12d3-a456-426614174000@1.2.3.4:443?security=reality",
            @"C:\Users\someone\secret.txt",
            "123e4567-e89b-12d3-a456-426614174000");

        // Сервер тегом, а не пулом — у получателя его нет, и тег не едет.
        _from.Edit(r => r.Set(MatchKind.HostList, mine, RoutingMode.Proxy, server: "🇳🇱 Нидерланды TRUST"));

        var bundle = RouteBundles.Export([(MatchKind.HostList, mine)], null, _from.Rules, _from.Settings, _from.Root);
        var path = Path.Combine(_dir, "mine.yaml");
        RouteBundles.Write(bundle, path);
        var text = File.ReadAllText(path);

        Assert.Equal(3, bundle.Left);
        Assert.Contains("example.com", text);
        Assert.DoesNotContain("vless", text);
        Assert.DoesNotContain("123e4567", text);
        Assert.DoesNotContain("Users", text);
        Assert.DoesNotContain("TRUST", text);
        Assert.DoesNotContain(_from.Root, text);
    }

    /// <summary>
    /// «Загрузить» версий до пакетов читает <c>rules</c> — в пакете их нет,
    /// и сотня маршрутов получателя не заменится одним присланным.
    /// </summary>
    [Fact]
    public void TheWholeFileLoadSeesNoRulesInABundle()
    {
        _from.Edit(r => r.Set(MatchKind.Domain, "*.example.com", RoutingMode.Proxy));
        var file = Export(_from, null, (MatchKind.Domain, "*.example.com"));

        Assert.Empty(RulesShare.Read(file));
        Assert.True(RouteBundles.Looks(file));
        Assert.False(RouteBundles.Looks(_from.RulesPath));
    }

    /// <summary>
    /// В папке узкий список виден после широкого, а в файле правил стоит раньше —
    /// и после загрузки он раньше: движок берёт первое совпавшее.
    /// </summary>
    [Fact]
    public void TheSendersRuleOrderSurvivesTheFolderOrder()
    {
        var narrow = _from.OwnList("narrow", RoutingMode.Proxy, "cdn.example.com");
        var wide = _from.OwnList("wide", RoutingMode.Direct, "example.com");

        var file = Export(_from, "Мои", (MatchKind.HostList, wide), (MatchKind.HostList, narrow));

        _to.Load(file);

        var entries = _to.Rules.Entries;
        Assert.True(entries[0].Matches(narrow));
        Assert.True(entries[1].Matches(wide));

        // А в папке — как видел отправитель.
        Assert.Equal(
            [RouteFolders.Place("Мои"), RouteFolders.KeyOf(MatchKind.HostList, wide), RouteFolders.KeyOf(MatchKind.HostList, narrow)],
            _to.Settings.RouteOrder);
    }

    /// <summary>Часть внутри широкого списка встаёт раньше его правила и у получателя.</summary>
    [Fact]
    public void ANarrowCatalogPartLandsBeforeItsWideList()
    {
        _from.Edit(r => r.Set(MatchKind.HostList, DiscordImages, RoutingMode.Proxy, before: DiscordText));
        _to.Edit(r => r.Set(MatchKind.HostList, DiscordText, RoutingMode.Desync));

        _to.Load(Export(_from, null, (MatchKind.HostList, DiscordImages)));

        var entries = _to.Rules.Entries;
        Assert.True(entries[0].Matches(DiscordImages));
        Assert.True(entries[1].Matches(DiscordText));
    }

    /// <summary>Файл чужой: пути вне своих списков и чужие названия не пишутся никуда.</summary>
    [Fact]
    public void PathsOutsideTheListsAreNotWritten()
    {
        var path = Path.Combine(_dir, "evil.yaml");

        File.WriteAllText(path, """
            export: netzapret-route
            version: 1
            routes:
              - match: hostlist
                value: "config/lists/own/../../../evil.txt"
                mode: proxy
                list:
                  - "example.com"
              - match: hostlist
                value: "C:/Users/someone/list.txt"
                mode: proxy
              - match: hostlist
                value: "config/lists/no-such-service.txt"
                mode: proxy
              - match: process
                value: "Fallout76.exe"
                mode: proxy
              - match: hostlist
                value: "config/lists/own/programs/repo.exe.domains.txt"
                mode: proxy
                list:
                  - "example.com"
            """);

        var plan = _to.Plan(path);

        Assert.All(plan.Steps, s => Assert.Equal(RouteImportAction.Skip, s.Action));

        _to.Load(path);

        Assert.Empty(_to.Rules.Entries);
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(_to.Root, "config", "lists")));
    }

    [Fact]
    public void ANewerFileIsRefusedNotHalfRead()
    {
        var path = Path.Combine(_dir, "newer.yaml");
        File.WriteAllText(path, "export: netzapret-route\nversion: 99\nroutes: []\n");

        Assert.Contains("более новой версией", Assert.Throws<InvalidDataException>(() => RouteBundles.Read(path)).Message);

        var rules = Path.Combine(_dir, "rules.yaml");
        File.WriteAllText(rules, "rules: []\n");

        Assert.Throws<InvalidDataException>(() => RouteBundles.Read(rules));
    }

    [Fact]
    public void BlockKeepsThePlaceOfTheFirstKeyOrGoesFirst()
    {
        Assert.Equal(["a", "x", "y", "b"], RouteBundles.Block(["a", "y", "b", "x"], ["x", "y"]));
        Assert.Equal(["x", "y", "a", "b"], RouteBundles.Block(["a", "b"], ["x", "y"]));
        Assert.Equal(["x", "y"], RouteBundles.Block([], ["x", "y"]));
    }
}
