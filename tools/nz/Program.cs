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
    "voice" or "голос" => Voice(),
    "doctor" or "диагностика" => DoctorCommand(),
    "watch" or "наблюдение" => await Watch(args.Skip(1).ToList()),
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

        // Закреплён ли сервер, знают только настройки: сторож ставит серверы
        // прямо в селектор и при автоподборе (TunnelStatus.Standing). Замену
        // выбранному серверу называет надзор.
        var remark = state.Services.First(s => s.Name == "sing-box").Remark;

        var word = NetZapret.Proxy.TunnelStatus.StandingWord(server, automatic, settings.PreferredServer, remark);

        Console.WriteLine();
        Console.WriteLine(server is null
            ? "выход:   движок не ответил"
            : $"выход:   {server} ({word})");
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
        var exclusions = NetZapret.Proxy.HostsFile.DescribeDesyncExclusions(engine.RuleSet, tunnelUp: tunnelUp);
        var bypass = NetZapret.Proxy.HostsFile.BypassFor(
            exclusions,
            host,
            NetZapret.Proxy.HostsFile.CollectShieldHoles(engine.RuleSet, exclusions));

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
            EngineAnswersDns(),
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
// вкладке DNS: пишется DnsVia (авто, напрямую, туннель), применяется при следующем запуске
// движков. Перезапустить их nz не может — прав администратора он не просит.
int DnsMode(string? choice)
{
    var settings = AppSettings.Load(AppSettings.DefaultPath);

    if (choice is { Length: > 0 })
    {
        var route = DnsRoutes.Parse(choice.Replace('-', ' '));

        if (route is null)
        {
            Console.Error.WriteLine("nz dns-mode авто | напрямую | туннель");
            return 2;
        }

        if (settings.DnsVia != route)
        {
            settings = settings with { DnsVia = route.Value };
            settings.Save(AppSettings.DefaultPath);
            Console.WriteLine("записано; применится при следующем запуске движков");
        }
        else
        {
            Console.WriteLine("и так стоит");
        }
    }

    Console.WriteLine($"в настройках: {settings.DnsServer}, {DnsRoutes.Word(settings.DnsVia)}");

    // Что делает работающий движок — может отставать от настроек до перезапуска.
    var config = ReadEngineConfig();

    if (!EngineAnswersDns() || config is null)
    {
        Console.WriteLine("движок:       не поднят — имена разрешает Windows сама");
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

// Отвечает ли на DNS наш движок — туннель или движок только ради DNS (03.10).
bool EngineAnswersDns() =>
    SupervisorState.Load(SupervisorState.DefaultPath)?.EngineAnswersDns() == true;

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

// Голос Discord: адреса звука из журнала Discord против списка голоса —
// что сторож окна (DiscordVoiceWatch) дописал бы. Ничего не пишет: чтобы
// проверить без звонков и попросить запустить того, у кого голос не идёт.
// Тот же текст уходит в отчёт (voice.txt).
int Voice()
{
    var (text, found) = NetZapret.Core.Services.DiscordVoiceLearn.Report();

    Console.Write(text);

    return found ? 0 : 1;
}

// Наблюдение — тот же сеанс, что раздел «Наблюдение» окна (ConnectionWatch),
// и тот же журнал runtime\watch.log. Владелец 10.10: «добавь в nz наблюдение».
//
// Единственная команда nz, которой нужны права администратора: сессия ETW
// ядра без них не создаётся. Просить их nz не станет (см. начало файла) —
// запускать из терминала администратора.
async Task<int> Watch(List<string> options)
{
    int? seconds = null;
    string? process = null;
    bool routed = false, force = false, journal = true;

    for (int i = 0; i < options.Count; i++)
    {
        switch (options[i])
        {
            case "--seconds" when i + 1 < options.Count && int.TryParse(options[i + 1], out var s) && s > 0:
                seconds = s;
                i++;
                break;
            case "--process" when i + 1 < options.Count:
                process = options[++i];
                break;
            case "--routed":
                routed = true;
                break;
            case "--force":
                force = true;
                break;
            case "--no-log":
                journal = false;
                break;
            default:
                Console.Error.WriteLine($"не знаю «{options[i]}». nz watch [--seconds N] [--process имя.exe] [--routed] [--no-log] [--force]");
                return 2;
        }
    }

    // Имя сессии одно на машину, и новая останавливает прежнюю: без вопроса
    // nz молча оборвала бы наблюдение в окне.
    if (!force && NetZapret.Etw.EtwConnectionSource.SessionExists())
    {
        Console.Error.WriteLine("сессия наблюдения уже есть: наблюдает окно или осталась от прерванного запуска.");
        Console.Error.WriteLine("nz watch --force заберёт её себе (наблюдение в окне остановится).");
        return 1;
    }

    ConnectionWatch watch;

    try
    {
        watch = ConnectionWatch.Start(AppSettings.Load(AppSettings.DefaultPath), journal ? ConnectionWatch.DefaultJournal : null);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("наблюдение не началось: " + ex.GetBaseException().Message);
        Console.Error.WriteLine("сессии ETW нужны права администратора — запустите nz из терминала администратора.");
        return 1;
    }

    using var stop = seconds is { } limit ? new CancellationTokenSource(TimeSpan.FromSeconds(limit)) : new CancellationTokenSource();

    // Ctrl+C — остановка, а не убийство: сессия ETW переживает процесс,
    // и брошенная останется в системе до перезагрузки.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stop.Cancel();
    };

    Console.WriteLine($"смотрю: режим «{watch.Mode}», правил {watch.RuleCount}"
        + (journal ? $", журнал {ConnectionWatch.DefaultJournal}" : ", без журнала")
        + (seconds is null ? "; Ctrl+C — остановить" : $"; {seconds} с"));
    Console.WriteLine("«куда» — что сказали бы правила, а не что сделал движок");
    Console.WriteLine();

    int shown = 0;

    await using (watch)
    {
        await foreach (var entry in watch.ReadAsync(stop.Token))
        {
            if (routed && !entry.Routed)
                continue;

            if (process is not null && !string.Equals(entry.Process, process, StringComparison.OrdinalIgnoreCase))
                continue;

            shown++;
            Console.WriteLine($"{entry.Time.ToLocalTime():HH:mm:ss.fff}  {entry.ModeWord,-8}  {entry.Protocol}  {entry.Process,-24}  {entry.Endpoint,-40}  {entry.RuleShown}");
        }
    }

    Console.WriteLine();

    // Кончилось без Ctrl+C и без срока — сессию погасили снаружи: окно,
    // начиная наблюдение, забирает её себе (имя одно на машину).
    if (!stop.IsCancellationRequested)
        Console.WriteLine("наблюдение прервано снаружи: сессию забрало окно («Наблюдение» → «Начать») или остановила система");

    Console.WriteLine($"соединений {watch.Total}, показано {shown}, под правило {watch.Matched}, имён узнано {watch.NamesKnown}"
        + (watch.Dropped > 0 ? $", потеряно при переполнении {watch.Dropped}" : string.Empty));

    return 0;
}

// Те же проверки, что раздел «Диагностика» окна и doctor.txt в отчёте.
// Права не проверяются: nz идёт без администратора намеренно.
int DoctorCommand()
{
    var sections = Doctor.Run(AppSettings.Load(AppSettings.DefaultPath), elevation: false);

    Console.Write(Doctor.Describe(sections));

    return sections.SelectMany(s => s.Lines).Any(l => l.Level == DoctorLevel.Bad) ? 1 : 0;
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

// Тот же отчёт, что кнопка «Собрать» в «Диагностике»: сборка и вычистка — в библиотеке.
int Report()
{
    var version = typeof(SupportReport).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
        .FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "—";

    var result = SupportReport.Create(version + " (nz)", machine: true);

    Console.WriteLine($"записано: {result.Path}");
    Console.WriteLine($"внутри: {string.Join(", ", result.Files)}");

    return 0;
}

int Help()
{
    Console.WriteLine("nz — разбор неисправностей NetZapret.");
    Console.WriteLine();
    Console.WriteLine("  nz status    что сейчас поднято");
    Console.WriteLine("  nz doctor    те же проверки, что раздел «Диагностика» окна");
    Console.WriteLine("  nz where <имя|программа.exe|адрес>");
    Console.WriteLine("               куда пойдёт и через что разрешится имя: по правилам,");
    Console.WriteLine("               выключателям, hosts и конфигу работающего движка");
    Console.WriteLine("  nz routes    противоречия в своих маршрутах и пинах");
    Console.WriteLine("  nz fix <сайт>  не открывается: пробует как есть, напрямую, через VPN");
    Console.WriteLine("               и записывает сработавший маршрут");
    Console.WriteLine("  nz dns       обзор DNS-провайдеров: что отвечает и что подменяется");
    Console.WriteLine("  nz voice     голос Discord: адреса звука из его журнала против списка голоса —");
    Console.WriteLine("               что сторож окна дописал бы; ничего не пишет");
    Console.WriteLine("  nz watch [--seconds N] [--process имя.exe] [--routed] [--no-log] [--force]");
    Console.WriteLine("               наблюдение: соединения и правило к каждому, как раздел окна;");
    Console.WriteLine("               пишет runtime\\watch.log; нужен терминал администратора");
    Console.WriteLine("  nz dns-mode [авто|напрямую|туннель]");
    Console.WriteLine("               как движок спрашивает имена; с аргументом — переключить");
    Console.WriteLine("  nz catalog   снимок рабочих записей каталога Zapret");
    Console.WriteLine("               в config\\catalog.zapret.yaml; идёт несколько минут");
    Console.WriteLine("  nz report    отчёт для разбора архивом в reports\\: журналы, настройки, сеть,");
    Console.WriteLine("               диагностика, голос Discord, hosts, сторож серверов, наблюдение —");
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
