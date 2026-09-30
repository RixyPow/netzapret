using System.Text;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Supervisor;

// Лёгкий инструмент для разбора неисправностей.
//
// Заведён 21.09 по замечанию владельца: «хочу, чтобы ты оставил себе все
// нужные инструменты, которые работают по тому же принципу, что и окно».
// Замечание точное, и диагноз в нём верный.
//
// Беда прежней консоли была не в том, что она существует, а в том, что
// у неё своя копия логики: BlockCheckCommand держит девятьсот строк
// собственных решений, MenuCommand — свою сборку конфига. Две копии
// одного расходятся, и 16.09 за вечер нашлись четыре расхождения,
// все в пользу консоли.
//
// Здесь своей логики нет ни строки, и это не аккуратность, а устройство:
// каждая команда сводится к вызову в ту же библиотеку, которой пользуется
// окно. Расходятся копии — а копии здесь нет.
//
// Мерка простая: понадобилось решение — решению место в библиотеке,
// где им воспользуется и окно. Появится здесь «if» о том, как чинить
// имя, — значит ошиблись местом.
//
// Прав администратора не просит намеренно. Окно их требует — ему ставить
// драйвер перехвата и поднимать TUN, — и потому «спросить состояние»
// через окно каждый раз дёргает UAC. Чтение файла состояния прав
// не требует вовсе, а диагностика, спрашивающая разрешения, не нужна
// никому.

Console.OutputEncoding = new UTF8Encoding(false);

// Корень установки — тем же поиском, что у окна: пути в настройках
// считаются от него, и nz из build\ иначе читала бы пустые настройки.
InstallRoot.MoveTo();

var command = args.FirstOrDefault()?.ToLowerInvariant();

return command switch
{
    "status" or "состояние" => Status(),
    "where" or "куда" => Where(string.Join(' ', args.Skip(1))),
    "dns" => await Dns(),
    "dns-mode" or "днс" => DnsMode(args.ElementAtOrDefault(1)),
    "routes" or "маршруты" => Routes(),
    "catalog" or "каталог" => await Catalog(),
    "report" or "отчёт" => Report(),
    "fix" or "починить" => await Fix(string.Join(' ', args.Skip(1))),
    null or "help" or "--help" or "-h" => Help(),
    _ => Unknown(command),
};

// Что сейчас поднято — теми же словами, какими судит окно.
//
// Иначе два ответа на вопрос «работает ли» разошлись бы, а именно
// на него отвечают первым делом, когда что-то сломалось.
int Status()
{
    var state = SupervisorState.Load(SupervisorState.DefaultPath);
    var settings = AppSettings.Load(AppSettings.DefaultPath);

    Console.WriteLine($"режим:   {settings.Engines.Describe()}");
    Console.WriteLine($"пресет:  {settings.PresetName ?? "не выбран"}");

    if (state is null)
    {
        Console.WriteLine("состояния нет — движки не поднимались");
        return 1;
    }

    Console.WriteLine($"надзор:  {(state.IsSupervisorAlive() ? "жив" : "не дожил")}");
    Console.WriteLine();

    foreach (var service in state.Services)
    {
        // Те же слова, что в окне и трее (EngineHealth.Status).
        var line = $"  {service.Name,-10} {EngineHealth.Status(service)}";

        if (service.RestartCount > 0)
            line += $", перезапусков {service.RestartCount}";

        Console.WriteLine(line);
    }

    // Через какой сервер туннель ходит прямо сейчас — у самого движка,
    // а не из настроек: они говорят «авто», а движок мог держаться другого.
    if (state.Services.Any(s => s.Name == "sing-box"))
    {
        var (server, automatic) = NetZapret.Proxy.TunnelStatus.CurrentExitAsync(CancellationToken.None)
            .GetAwaiter().GetResult();

        // Замену выбранному серверу называет надзор — со стороны она выглядит
        // как закреплённый сервер.
        var remark = state.Services.First(s => s.Name == "sing-box").Remark;

        Console.WriteLine();
        Console.WriteLine(server is null
            ? "выход:   движок не ответил"
            : $"выход:   {server} ({(automatic ? "автоподбор" : remark ?? "закреплён")})");
    }

    Console.WriteLine();
    Console.WriteLine(EngineHealth.Running(state)
        ? "итог: движки подняты"
        : "итог: " + EngineHealth.Complaint(state));

    return EngineHealth.Running(state) ? 0 : 1;
}

// Куда пойдёт имя, программа или адрес — сейчас, а не по правилам.
//
// Правило говорит «через VPN», а при выключенном туннеле это напрямую;
// «десинк» при прибитом в hosts имени не трогается вовсе. Поэтому три
// факта врозь: что сказано в правилах, что из этого делают выключатели,
// и выведено ли имя из-под десинка. Сводить их здесь в один ответ
// значило бы завести своё решение — а решения у nz нет.
int Where(string target)
{
    target = target.Trim();

    if (target.Length == 0)
    {
        Console.Error.WriteLine("что проверить? nz where twitch.tv");
        return 2;
    }

    var settings = AppSettings.Load(AppSettings.DefaultPath);
    var (engine, _) = NetZapret.Zapret.RuleSetExpander.LoadFor(settings);
    var connection = NetZapret.Core.Connections.ConnectionEvent.Describe(target);
    var decision = engine.Evaluate(connection);
    var engines = settings.Engines;
    bool tunnelUp = settings.NeedsProxy;

    Console.WriteLine($"{target}");
    Console.WriteLine();
    Console.WriteLine($"  в правилах:       {Word(decision.Mode)}"
        + (decision.Rule is null ? "  (по умолчанию)" : $"  ({decision.Rule})"));
    Console.WriteLine($"  при выключателях: {Word(engines.Effective(decision.Mode, tunnelUp))}"
        + $"  ({engines.Describe()})");

    if (connection.Hostname is { } host && settings.NeedsDesync)
    {
        var bypass = NetZapret.Proxy.HostsFile.BypassFor(
            NetZapret.Proxy.HostsFile.DescribeDesyncExclusions(engine.RuleSet, tunnelUp: tunnelUp),
            host);

        if (bypass != NetZapret.Proxy.DesyncBypass.None)
            Console.WriteLine($"  десинк:            {NetZapret.Proxy.HostsFile.DescribeBypass(bypass)}");
    }

    // Через что разрешается имя — сейчас, по hosts и конфигу работающего
    // движка (DnsPath). 30.09 имена мимо VPN разрешались через туннель,
    // и при заминке сервера пропадали у всей машины.
    if (connection.Hostname is { } name)
    {
        var steps = NetZapret.Proxy.DnsPath.Explain(
            name,
            NetZapret.Proxy.HostsFile.Read(),
            ReadEngineConfig(),
            TunnelRunning(),
            NetZapret.Proxy.SystemResolvers.Discover());

        for (int i = 0; i < steps.Count; i++)
            Console.WriteLine((i == 0 ? "  DNS:              " : "                    ") + steps[i]);
    }

    return 0;

    static string Word(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "VPN",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };
}

// «Сайт не открывается»: пути по очереди, сработавший — в правила.
// Решение целиком в SiteFixer; здесь только вывод.
async Task<int> Fix(string target)
{
    var result = await NetZapret.Supervisor.SiteFixer.FixAsync(
        target, new Progress<string>(Console.WriteLine), CancellationToken.None);

    foreach (var step in result.Steps)
        Console.WriteLine($"  {(step.Works ? "да " : "нет")}  {step.Path}: {step.Detail}");

    Console.WriteLine();
    Console.WriteLine(result.Summary);

    return result.Applied is null && result.Steps.All(s => !s.Works) ? 1 : 0;
}

// Как движок спрашивает имена, и переключение — то же, что список на
// вкладке DNS: пишется DnsThroughTunnel, применяется при следующем запуске
// движков. Перезапустить их nz не может — прав администратора он не просит.
int DnsMode(string? choice)
{
    var settings = AppSettings.Load(AppSettings.DefaultPath);

    if (choice is { Length: > 0 })
    {
        bool? through = choice.ToLowerInvariant() switch
        {
            "напрямую" or "direct" => false,
            "туннель" or "через-туннель" or "tunnel" => true,
            _ => null,
        };

        if (through is null)
        {
            Console.Error.WriteLine("nz dns-mode напрямую | туннель");
            return 2;
        }

        if (settings.DnsThroughTunnel != through)
        {
            settings = settings with { DnsThroughTunnel = through.Value };
            settings.Save(AppSettings.DefaultPath);
            Console.WriteLine("записано; применится при следующем запуске движков");
        }
        else
        {
            Console.WriteLine("и так стоит");
        }
    }

    Console.WriteLine($"в настройках: {settings.DnsServer}, {(settings.DnsThroughTunnel ? "через туннель" : "напрямую")}");

    // Что делает работающий движок — может отставать от настроек до перезапуска.
    var config = ReadEngineConfig();

    if (!TunnelRunning() || config is null)
    {
        Console.WriteLine("движок:       туннель не поднят — имена разрешает Windows сама");
        return 0;
    }

    var final = (string?)config["dns"]?["final"] ?? "?";
    Console.WriteLine($"движок:       {NetZapret.Proxy.DnsPath.Describe(config["dns"], final, string.Empty)}");

    return 0;
}

System.Text.Json.Nodes.JsonNode? ReadEngineConfig()
{
    try
    {
        return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(NetZapret.Proxy.EngineKeys.DefaultConfigPath));
    }
    catch (Exception)
    {
        return null;
    }
}

bool TunnelRunning()
{
    var state = SupervisorState.Load(SupervisorState.DefaultPath);
    return state is not null && state.IsSupervisorAlive() && state.Services.Any(s => s.Name == "sing-box");
}

// Обзор резолверов: задержки, кто отвечает на самом деле, подмена.
// Сокеты привязаны к физическому адаптеру — меряется сеть провайдера,
// а не наш туннель. Своего решения здесь нет: всё в DnsSurvey.
async Task<int> Dns()
{
    Console.WriteLine($"{"провайдер",-14} {"DoH",9} {"DoT",9} {"UDP",11}  {"реальный UDP-резолвер",-40} подмена");

    var rows = await NetZapret.Proxy.DnsSurvey.SurveyAllAsync();

    foreach (var row in rows)
    {
        static string Ms(double? ms, string failure) =>
            ms is { } v ? $"{v:0.0}мс" : failure.Length > 0 ? failure : "—";

        var udp = Ms(row.UdpMs, row.UdpFailure);

        if (row.UdpMs is not null && row.UdpAnswered < row.Provider.Udp.Count)
            udp += $" {row.UdpAnswered}/{row.Provider.Udp.Count}";

        var real = row.RealResolver is null
            ? "—"
            : $"{row.RealResolver}→{row.RealNetwork ?? "?"}" + (row.Intercepted ? " (чужая сеть)" : string.Empty);

        var spoof = row.SpoofChecked == 0 ? "—" : $"{row.Spoofed}/{row.SpoofChecked}";

        Console.WriteLine($"{row.Provider.Name,-14} {Ms(row.DohMs, row.DohFailure),9} {Ms(row.DotMs, row.DotFailure),9} {udp,11}  {real,-40} {spoof}");
    }

    return 0;
}

// Противоречия в своих маршрутах и пинах.
//
// Нужна затем, что 21.09 разбор «почему инста не грузится» занял час,
// и ответ всё это время лежал в том, что имя прибито в hosts и потому
// выведено из-под десинка. Тем же RouteClashes.FromRules, что и карточка
// на вкладке «Маршруты». До 30.09 читала файл книги маршрутов, которого
// программа сама не заводила, и отвечала «книги нет».
int Routes()
{
    var rules = UserRulesFile.Load().Entries;
    var pins = NetZapret.Proxy.HostsEditor.Pins().Keys.ToList();
    var clashes = RouteClashes.FromRules(rules, pins);

    Console.WriteLine($"своих маршрутов: {rules.Count(r => r.Enabled)}, пинов: {pins.Count}");

    if (clashes.Count == 0)
    {
        Console.WriteLine("противоречий нет");
        return 0;
    }

    Console.WriteLine();
    Console.WriteLine($"ПРОТИВОРЕЧИЙ: {clashes.Count}");

    foreach (var clash in clashes)
    {
        Console.WriteLine();
        Console.WriteLine($"  {clash.Name}");
        Console.WriteLine($"    {clash.Outcome}");
    }

    return 1;
}

// Снимок рабочих записей каталога Zapret в config\catalog.zapret.yaml.
// Проверяет каждую запись живым запросом — минуты, а не секунды.
async Task<int> Catalog()
{
    var zapret = NetZapret.Zapret.ZapretCatalog.Discover();

    if (zapret is null)
    {
        Console.Error.WriteLine("каталог Zapret не найден — снимать не с чего");
        return 1;
    }

    var records = zapret.AllRecords();
    Console.WriteLine($"записей в каталоге Zapret: {records.Count}; проверяю каждую…");

    var result = await NetZapret.Proxy.CatalogSnapshot.BuildAsync(
        records, new Progress<string>(Console.WriteLine), CancellationToken.None);

    OwnCatalog.WriteSnapshot(
        OwnCatalog.SnapshotPath,
        result.Services,
        result.Intermediaries,
        NetZapret.Proxy.CatalogSnapshot.Header(result, DateTime.Now));

    Console.WriteLine();
    Console.WriteLine($"имён {result.Hosts}, рабочих {result.Working}; записей в снимке {result.Services.Count}, "
        + $"посредников {result.Intermediaries.Count}");
    Console.WriteLine($"записано: {Path.GetFullPath(OwnCatalog.SnapshotPath)}");

    return result.Working > 0 ? 0 : 1;
}

// Тот же отчёт, что кнопка «Собрать» в «Ещё»: сборка и вычистка — в библиотеке.
int Report()
{
    var version = typeof(SupportReport).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "—";

    var result = SupportReport.Create(version + " (nz)", network: true);

    Console.WriteLine($"записано: {result.Path}");
    Console.WriteLine($"внутри: {string.Join(", ", result.Files)}");

    return 0;
}

int Help()
{
    Console.WriteLine("nz — разбор неисправностей NetZapret.");
    Console.WriteLine();
    Console.WriteLine("  nz status    что сейчас поднято");
    Console.WriteLine("  nz where <имя|программа.exe|адрес>");
    Console.WriteLine("               куда пойдёт и через что разрешится имя: по правилам,");
    Console.WriteLine("               выключателям, hosts и конфигу работающего движка");
    Console.WriteLine("  nz routes    противоречия в своих маршрутах и пинах");
    Console.WriteLine("  nz fix <сайт>  не открывается: пробует как есть, напрямую, через VPN");
    Console.WriteLine("               и записывает сработавший маршрут");
    Console.WriteLine("  nz dns       обзор DNS-провайдеров: что отвечает и что подменяется");
    Console.WriteLine("  nz dns-mode [напрямую|туннель]");
    Console.WriteLine("               как движок спрашивает имена; с аргументом — переключить");
    Console.WriteLine("  nz catalog   снимок рабочих записей каталога Zapret");
    Console.WriteLine("               в config\\catalog.zapret.yaml; идёт несколько минут");
    Console.WriteLine("  nz report    отчёт для разбора: журналы и настройки архивом в reports\\,");
    Console.WriteLine("               без ссылок подписок и ключей");
    Console.WriteLine();
    Console.WriteLine("Поднять и погасить движки можно самой программой:");
    Console.WriteLine("  NetZapret.exe --start");
    Console.WriteLine("  NetZapret.exe --stop");
    Console.WriteLine("Им нужны права администратора — ставится драйвер перехвата.");

    return 0;
}

int Unknown(string? said)
{
    Console.Error.WriteLine($"не знаю команды «{said}». Список: nz help");

    return 2;
}
