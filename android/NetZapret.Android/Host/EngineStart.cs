using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetZapret.Mobile;
#if NZ_INTEGRATED
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Zapret;
#endif

namespace NetZapret.Android.Host;

/// <summary>Что хозяин знает о запуске до сборки конфига.</summary>
/// <param name="Port">Порт ByeDPI на эту сессию VPN (случайный, 30000–60999).</param>
/// <param name="Username">Имя SOCKS5 для ByeDPI — то же, что получит исходящий sing-box.</param>
/// <param name="Password">Пароль SOCKS5 на эту сессию; не пишется ни в журнал, ни в файлы.</param>
/// <param name="Root">Root есть и человек разрешил им пользоваться (<see cref="MobileSettings.UseRoot"/>).</param>
/// <param name="Mobile">Настройки десинка телефона (config/mobile.json, пишет интерфейс).</param>
internal sealed record EngineStartRequest(
    string FilesDirectory,
    string NativeLibraryDirectory,
    string PackageName,
    int Uid,
    int Pid,
    bool Root,
    int Port,
    string Username,
    string Password,
    MobileSettings Mobile);

/// <summary>ByeDPI для NzBox: аргументы без имени файла, порт, «имя:пароль» и рабочая папка.</summary>
internal sealed record ByeDpiLaunch(IReadOnlyList<string> Arguments, int Port, string Auth, string WorkDirectory);

/// <summary>nfqws2 через su: команды подъёма и остановки (сценарии лежат в runtime/).</summary>
/// <param name="Digest">
/// SHA-256 сценариев этого запуска. Команды su от запуска к запуску одни и те
/// же (путь к сценарию), а служба при повторном запросе перезапускает nfqws2,
/// только если сменилась root-часть запроса (NzVpnService.reload): без отпечатка
/// новые аргументы — свои рецепты, другой пресет — до работающего nfqws2
/// не доходили бы до полной остановки.
/// </param>
internal sealed record RootLaunch(IReadOnlyList<string> Start, IReadOnlyList<string> Stop, string Digest);

/// <summary>Итог подготовки: конфиг записан, десинк описан — или причина, почему нет.</summary>
internal sealed record EnginePrepared(
    string? Error,
    string? ConfigPath,
    ByeDpiLaunch? ByeDpi,
    RootLaunch? Root,
    IReadOnlyList<string> Warnings)
{
    public static EnginePrepared Failed(string error) => new(error, null, null, null, []);
}

/// <summary>
/// Подготовка запуска: конфиг sing-box (EngineConfigBuilder, WP1), командная
/// строка ByeDPI или nfqws2 с правилами iptables (WP2) и запрос для NzBox
/// (DESIGN.md §4).
/// </summary>
/// <remarks>
/// <para>
/// Логика — в библиотеках src/ (CLAUDE.md, «Код»): здесь только то, что знает
/// один телефон, — папки, номер приложения, root, порт и пароль сессии.
/// </para>
/// <para>
/// Пока WP1 и WP2 пишутся в своих рабочих копиях, их типов в этом дереве нет:
/// код с ними — под NZ_INTEGRATED (символ ставит .csproj, когда файлы
/// появятся), а без них запуск честно отвечает, что сборка конфига ещё не
/// подключена.
/// </para>
/// </remarks>
internal static class EngineStart
{
    /// <summary>Имя SOCKS5 у ByeDPI — не секрет, секрет — пароль.</summary>
    public const string ByeDpiUser = "nz";

    /// <summary>Случайный свободный порт 30000–60999 и пароль на сессию VPN.</summary>
    /// <remarks>
    /// ByeDPI не умеет порт 0, поэтому порт выбираем сами и проверяем занятость
    /// пробной привязкой (byedpi.md §2.4). Случайный, а не 1080: методичка
    /// Минцифры велит проверять известные порты прокси (byedpi.md §5.2) —
    /// маскировка слабая, но пароль закрывает остальное.
    /// </remarks>
    public static (int Port, string Password) NewSession()
    {
        int port = 0;

        for (int attempt = 0; attempt < 20 && port == 0; attempt++)
        {
            int candidate = RandomNumberGenerator.GetInt32(30000, 61000);

            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
                probe.Start();
                probe.Stop();
                port = candidate;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // Занят — следующий.
            }
        }

        if (port == 0)
            port = RandomNumberGenerator.GetInt32(30000, 61000);

        // 24 случайных байта в base64url: в RFC 1929 пароль до 255 байт,
        // а «:» и «/» в нём мешали бы разбору NZ_BYEDPI_AUTH=имя:пароль.
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        return (port, password);
    }

    public static async Task<EnginePrepared> PrepareAsync(EngineStartRequest request, CancellationToken cancellationToken)
    {
#if NZ_INTEGRATED
        return await PrepareIntegratedAsync(request, cancellationToken);
#else
        await Task.CompletedTask;
        return EnginePrepared.Failed(
            "Сборка конфига ещё не подключена: в этой сборке нет EngineConfigBuilder (WP1) "
            + "и командных строк десинка (WP2) — движок запускать не с чем.");
#endif
    }

    /// <summary>Запрос NzBox.start (DESIGN.md §4). В нём пароль ByeDPI — только в память, не на диск.</summary>
    public static string RequestJson(string files, EnginePrepared prepared, string notificationTitle)
    {
        using var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("configPath", prepared.ConfigPath);
            json.WriteString("workingPath", Path.Combine(files, "runtime", "libbox"));

            if (prepared.ByeDpi is { } byedpi)
            {
                json.WriteStartObject("byedpi");
                json.WriteStartArray("args");

                foreach (var argument in byedpi.Arguments)
                    json.WriteStringValue(argument);

                json.WriteEndArray();
                json.WriteString("auth", byedpi.Auth);
                json.WriteNumber("port", byedpi.Port);
                json.WriteString("workDir", byedpi.WorkDirectory);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("byedpi");
            }

            if (prepared.Root is { } root)
            {
                json.WriteStartObject("root");
                WriteArray(json, "start", root.Start);
                WriteArray(json, "stop", root.Stop);

                // Java его не читает — он входит в слепок root-части (NzRequest.Root.key).
                json.WriteString("digest", root.Digest);
                json.WriteEndObject();
            }
            else
            {
                json.WriteNull("root");
            }

            json.WriteString("notificationTitle", notificationTitle);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteArray(Utf8JsonWriter json, string name, IReadOnlyList<string> values)
    {
        json.WriteStartArray(name);

        foreach (var value in values)
            json.WriteStringValue(value);

        json.WriteEndArray();
    }

    /// <summary>
    /// Аргументы ByeDPI с гарантированными адресом и портом: слушать только
    /// 127.0.0.1 и только SOCKS5 (у SOCKS4 и HTTP пароля нет; по умолчанию
    /// ByeDPI слушает 0.0.0.0 — открытый прокси для всего Wi-Fi, byedpi.md §5.1).
    /// </summary>
    public static IReadOnlyList<string> WithListener(IReadOnlyList<string> arguments, int port)
    {
        var result = new List<string>();

        bool HasKey(params string[] keys) =>
            arguments.Any(a => keys.Any(k => a == k || a.StartsWith(k + "=", StringComparison.Ordinal)));

        if (!HasKey("-i", "--ip"))
            result.AddRange(["-i", "socks5://127.0.0.1"]);

        if (!HasKey("-p", "--port"))
            result.AddRange(["-p", port.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        result.AddRange(arguments);
        return result;
    }

#if NZ_INTEGRATED
    private static async Task<EnginePrepared> PrepareIntegratedAsync(EngineStartRequest request, CancellationToken cancellationToken)
    {
        var files = request.FilesDirectory;
        var settings = AppSettings.Load();
        var warnings = new List<string>();
        var zapretRoot = Path.Combine(files, "engines", "zapret");
        var hostsPath = Path.Combine(files, "config", "hosts");

        var nfqws2 = Path.Combine(request.NativeLibraryDirectory, "libnfqws2.so");
        var byedpiExe = Path.Combine(request.NativeLibraryDirectory, "libbyedpi.so");

        bool desync = settings.NeedsDesync;
        bool useRoot = desync && request.Root && File.Exists(nfqws2);

        if (desync && request.Root && !useRoot)
            warnings.Add("libnfqws2.so не распакован в папку библиотек — десинк без root.");

        // Без root — ByeDPI отдельным процессом (DESIGN.md), если человек не
        // выбрал фрагментацию sing-box. Нет файла (APK поставлен без распаковки
        // библиотек) — тоже фрагментация: хуже, но не мимо всего.
        ByeDpiLaunch? byedpi = null;
        DesyncSocks? socks = null;
        IReadOnlyList<string> passHosts = [];

        if (desync && !useRoot && request.Mobile.Desync == DesyncEngine.ByeDpi)
        {
            if (File.Exists(byedpiExe))
                (byedpi, socks, passHosts) = PrepareByeDpi(request, settings, zapretRoot, warnings);
            else
                warnings.Add("libbyedpi.so не распакован в папку библиотек — десинк будет фрагментацией TLS sing-box.");
        }

        var host = new EngineHost
        {
            Platform = SingBoxPlatform.Android,
            Ipv6 = static () => true,
            SystemResolvers = static () => [],
            LoadRules = static s =>
            {
                var (engine, root) = RuleSetExpander.LoadFor(s);
                return (engine.RuleSet, root);
            },
            ExpandAddressLists = static (entries, root) => AddressListReader.Expand(entries, root, out _),
            Subscriptions = () => Sources(settings),
            Journal = static (category, message) => AndroidJournal.Write(category, message),
            HostsPath = hostsPath,
            Android = new AndroidEngine
            {
                HostsFilePath = hostsPath,
                Root = useRoot,
                // Наш пакет — мимо своего VPN: через него ходят ByeDPI и пробы «напрямую».
                ExcludePackages = [request.PackageName],
                Socks = socks,
                DesyncPassHosts = passHosts,
            },
        };

        var plan = EngineConfigBuilder.Plan(settings, host);

        // С root и без туннеля план библиотеки не поднимает движок вовсе:
        // «десинк внутри движка» он видит только без root, а DNS-движок — только
        // при «DNS через туннель» или DNS роутера. При DNS «авто» (по умолчанию)
        // и «Только десинк» или «Гибриде» без подписки запуск отвечал «Запускать
        // нечего», и nfqws2 не поднимался никогда (найдено чтением 27.10). Без
        // движка на телефоне нельзя: NzBox без конфига не стартует, а пины
        // hosts и DNS живут в sing-box (DESIGN.md: «Только десинк» — тот же TUN
        // без исходящих подписки).
        //
        // Поэтому собираем конфиг «без выходов», как у телефона без root, но
        // с десинком мимо движка: без ByeDPI, без нарезки и без отказа QUIC —
        // маршрут «десинк» тогда direct (SingBoxConfigCompiler.DesyncWayOf), и
        // его пакеты ловит nfqws2, как winws2 на Windows. Цена — перехват TUN
        // полный, а не выборочный: так библиотека собирает конфиг без root.
        // Место правке — EngineConfigBuilder.Plan (без туннеля при десинке на
        // телефоне — движок всегда); когда она там будет, план придёт с
        // FragmentOnly, и эта ветка не понадобится.
        if (useRoot && !plan.Tunnel && !plan.DnsOnly && !plan.FragmentOnly)
        {
            host = host with
            {
                Android = host.Android! with
                {
                    Root = false,
                    Socks = null,
                    DesyncPassHosts = [],
                    DesyncFragment = TlsFragment.Off,
                    DesyncFragmentDefault = false,
                    DesyncSocksDefault = false,
                    DesyncRejectQuic = false,
                },
            };

            plan = EngineConfigBuilder.Plan(settings, host);
        }

        EngineBuild build;

        if (plan.Tunnel || plan.FragmentOnly)
            build = await EngineConfigBuilder.BuildTunnelAsync(settings, host, cancellationToken);
        else if (plan.DnsOnly)
            build = EngineConfigBuilder.BuildDnsOnly(settings, host);
        else
            return EnginePrepared.Failed("Запускать нечего: ни VPN, ни десинк не включены.");

        if (!build.Ok)
            return EnginePrepared.Failed(build.Message);

        if (build.Warning is not null)
            warnings.Add(build.Warning);

        var configPath = Path.GetFullPath(Path.Combine(files, settings.ProxyConfigPath));

        RootLaunch? root = null;

        if (useRoot)
        {
            // После конфига: адреса серверов для исключения из перехвата
            // берутся из него. Списки щита и своих рецептов пишет сам запуск
            // nfqws2 (DesyncLists) — сборка конфига их не пишет.
            var (launch, error) = await PrepareRootAsync(
                request, settings, zapretRoot, configPath, hostsPath, plan.Tunnel, warnings, cancellationToken);

            if (error is not null)
                return EnginePrepared.Failed(error);

            root = launch;
        }

        foreach (var warning in warnings)
            AndroidJournal.Write("Запуск", warning);

        return new EnginePrepared(null, configPath, byedpi, root, warnings);
    }

    /// <summary>
    /// Подписки из книги — как TunnelConfig.BuildAsync на Windows, с запасной
    /// ссылкой из настроек, когда книга пуста (её ставит консоль).
    /// </summary>
    private static SubscriptionSources Sources(AppSettings settings)
    {
        var book = SubscriptionSources.Read();

        if (book.Pool.Count > 0 || string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            return book;

        return book with
        {
            Pool = [new PoolSource("Основная", settings.SubscriptionUrl)],
            PanelUrls = [settings.SubscriptionUrl, .. book.PanelUrls],
        };
    }

    private static (ByeDpiLaunch Launch, DesyncSocks Socks, IReadOnlyList<string> PassHosts) PrepareByeDpi(
        EngineStartRequest request, AppSettings settings, string zapretRoot, List<string> warnings)
    {
        var workDirectory = Path.Combine(request.FilesDirectory, "runtime", "byedpi");
        Directory.CreateDirectory(workDirectory);

        // Списки групп прошлого запуска: секции пресета могли поменяться.
        foreach (var stale in Directory.EnumerateFiles(workDirectory))
            File.Delete(stale);

        var presetPath = settings.PresetName is { } name ? ZapretPaths.FindPreset(name) : null;

        // Имена стратегий у интерфейса и у библиотеки одни (MobileSettings.DesyncStrategy).
        var strategy = request.Mobile.Strategy switch
        {
            DesyncStrategy.Default => ByeDpiStrategy.Default,
            DesyncStrategy.Custom => ByeDpiStrategy.Custom,
            _ => ByeDpiStrategy.Preset,
        };

        if (strategy == ByeDpiStrategy.Preset && presetPath is null)
        {
            warnings.Add($"Пресет «{settings.DescribePreset()}» не найден — ByeDPI со строкой по умолчанию.");
            strategy = ByeDpiStrategy.Default;
        }

        ByeDpiRequest Request(ByeDpiStrategy chosen) => new()
        {
            PresetPath = presetPath ?? "",
            ZapretRoot = zapretRoot,
            WorkDirectory = workDirectory,
            Port = request.Port,
            Strategy = chosen,
            CustomArguments = request.Mobile.CustomArguments,
        };

        ByeDpiPlan plan;

        try
        {
            plan = ByeDpiCommandLine.Build(Request(strategy));
        }
        catch (InvalidOperationException error) when (strategy != ByeDpiStrategy.Default)
        {
            // Пресет не уложился в группы ByeDPI и после уступок — лучше
            // строка по умолчанию, чем «десинк» мимо всего.
            warnings.Add($"ByeDPI: {error.Message} — взята строка по умолчанию.");
            plan = ByeDpiCommandLine.Build(Request(ByeDpiStrategy.Default));
        }

        foreach (var (relative, text) in plan.Files)
        {
            var path = Path.Combine(workDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, new UTF8Encoding(false));
        }

        warnings.AddRange(plan.Warnings.Select(w => "ByeDPI: " + w));

        var auth = $"{request.Username}:{request.Password}";

        return (
            new ByeDpiLaunch(WithListener(plan.Arguments, request.Port), request.Port, auth, workDirectory),
            new DesyncSocks(request.Port, request.Username, request.Password),
            plan.PassHosts);
    }

    /// <summary>
    /// nfqws2 с root (nfqws2.md §3–5): аргументы, правила iptables и сценарии
    /// подъёма и остановки — от библиотеки (Nfqws2CommandLine.Scripts), здесь
    /// только то, что знает телефон: uid, номер процесса, папки и ядро.
    /// </summary>
    /// <param name="hostsPath">Файл пинов телефона — щит берёт прибитые в нём имена.</param>
    /// <param name="tunnelUp">Поднимается ли туннель (план запуска) — от этого причина у имён «через VPN».</param>
    private static async Task<(RootLaunch? Launch, string? Error)> PrepareRootAsync(
        EngineStartRequest request, AppSettings settings, string zapretRoot, string configPath,
        string hostsPath, bool tunnelUp, List<string> warnings, CancellationToken cancellationToken)
    {
        var runtime = Path.Combine(request.FilesDirectory, "runtime");

        var presetPath = settings.PresetName is { } name ? ZapretPaths.FindPreset(name) : null;

        if (presetPath is null)
            return (null, $"Пресет «{settings.DescribePreset()}» не найден — nfqws2 запускать не с чем.");

        var preset = new PresetReader().Load(presetPath);

        var host = new Nfqws2Host
        {
            ZapretRoot = zapretRoot,
            Executable = Path.Combine(request.NativeLibraryDirectory, "libnfqws2.so"),
            Uid = request.Uid,
            PidFile = Path.Combine(runtime, "nfqws2.pid"),
            LogFile = Path.Combine(runtime, "nfqws2.log"),
        };

        // Щит, дыры в нём, свои рецепты и UDP Riot — заново на каждый запуск,
        // как окно Windows перед winws2 (TunnelConfig.WriteDesyncLists). Прежде
        // здесь их только подбирали с диска, а писать их на телефоне было
        // некому — nfqws2 поднимался без щита (DesyncLists, 27.10).
        var lists = DesyncLists.Write(settings, preset, request.FilesDirectory, zapretRoot, hostsPath, tunnelUp, warnings);

        var launch = Nfqws2CommandLine.Build(preset, host, lists.Exclude, lists.Own, settings.GameFilter, settings.Capture, lists.Keep);
        WinwsCommandLine.PrepareWritable(launch.Arguments, zapretRoot);

        var missing = WinwsCommandLine.FindMissingFiles(preset, zapretRoot);

        if (missing.Count > 0)
            warnings.Add($"Пресет ссылается на {missing.Count} отсутствующих файлов, например {missing[0]}.");

        foreach (var dropped in launch.Capture.Dropped)
            warnings.Add($"nfqws2: {dropped}");

        // Что умеет ядро — от root; без ответа правил не собрать.
        var probe = await RootShell.RunAsync(
            NfqueueFirewall.ProbeScript(host.QueueNumber, host.DesyncMark), TimeSpan.FromSeconds(20), cancellationToken);

        if (probe is null)
            return (null, "root не ответил на проверку ядра — nfqws2 не запущен.");

        var kernel = NfqueueFirewall.ParseProbe(probe.Output.Split('\n'));

        if (!(kernel.Mangle4 && kernel.Nfqueue4) && !(kernel.Mangle6 && kernel.Nfqueue6))
            return (null, "В ядре нет NFQUEUE или таблицы mangle — nfqws2 без них не работает. Выключите десинк с root.");

        var exclusions = new FirewallExclusions
        {
            Servers = TunnelEndpoints.Read(configPath, ServerPools.CaptureFirst(Warp.PreferredExit(settings), settings.EnginePools)),
            FakeRange = TunnelEndpoints.FakeRange(configPath),
            UdpOff = lists.UdpOff,
            Lans = LocalNetworks.VirtualLans,
        };

        var scripts = Nfqws2CommandLine.Scripts(host, launch, exclusions, kernel, runtime, request.Pid, request.PackageName);

        // Правила прошлого запуска, которых в этом нет (ядро без IPv6), — убрать.
        foreach (var rules in new[] { Nfqws2Scripts.Rules4Name, Nfqws2Scripts.Rules6Name })
        {
            var stale = Path.Combine(runtime, rules);

            if (!scripts.Files.ContainsKey(rules) && File.Exists(stale))
                File.Delete(stale);
        }

        foreach (var (file, text) in scripts.Files)
            File.WriteAllText(Path.Combine(runtime, file), text, new UTF8Encoding(false));

        warnings.AddRange(scripts.Warnings.Select(w => "nfqws2: " + w));

        // Журнал и файл номера процесса создаёт приложение, а не root: файл,
        // созданный root, получил бы метку без категорий приложения, и мы бы
        // его потом не переписали (nfqws2.md §5.3).
        if (File.Exists(host.LogFile))
            File.Move(host.LogFile, Path.Combine(runtime, "nfqws2.1.log"), overwrite: true);

        File.WriteAllText(host.LogFile, "");
        File.WriteAllText(host.PidFile, "");

        var digest = new StringBuilder();

        foreach (var (file, text) in scripts.Files.OrderBy(each => each.Key, StringComparer.Ordinal))
            digest.Append(file).Append('\n').Append(text).Append('\n');

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(digest.ToString()))).ToLowerInvariant();

        return (new RootLaunch(scripts.Start, scripts.Stop, hash), null);
    }
#endif
}
