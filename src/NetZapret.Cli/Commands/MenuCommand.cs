using System.Diagnostics;
using System.Security.Principal;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Core.Updates;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Интерактивное меню.
/// </summary>
/// <remarks>
/// Сделано внутри программы, а не батником: cmd.exe читает файлы в кодировке
/// OEM и на кириллице разваливается — мы это уже поймали на обёртке. Здесь же
/// доступны и Unicode, и вся логика напрямую, без разбора вывода собственных
/// же команд.
/// </remarks>
internal static class MenuCommand
{
    public static async Task<int> RunAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var settingsPath = cmd.Value("settings", AppSettings.DefaultPath);
        var settings = AppSettings.Load(settingsPath);

        UpdateInstaller.CleanUp();

        // Спрашивается один раз за сеанс и в стороне от главного дела:
        // выяснение занимает секунды, а держать из-за него меню незачем.
        var update = settings.CheckForUpdates
            ? Task.Run(() => UpdateCheck.LatestAsync(cancellationToken), cancellationToken)
            : Task.FromResult<ReleaseInfo?>(null);

        while (!cancellationToken.IsCancellationRequested)
        {
            Draw(settings);
            ShowUpdateHint(update);

            Console.Write("Выбор: ");
            var choice = Console.ReadLine();

            // Конец ввода — не то же самое, что нажатый ноль. Пока это было
            // одним и тем же, меню при закрытом stdin спрашивало «что делать
            // с движками», не получало ответа, возвращалось к себе же —
            // и так по кругу: сто двадцать килобайт перерисовок за шесть
            // секунд и полностью занятое ядро. Спрашивать некого, выходим.
            if (choice is null)
                return 0;

            choice = choice.Trim();

            if (choice is "0" or "q" or "Q")
            {
                if (AskOnExit())
                    return 0;

                continue;
            }

            settings = await HandleAsync(choice, settings, settingsPath, cancellationToken);
        }

        return 0;
    }

    /// <summary>
    /// Спрашивает при выходе, что делать с работающими движками.
    /// </summary>
    /// <returns><c>true</c> — выходить, <c>false</c> — вернуться в меню.</returns>
    /// <remarks>
    /// <para>
    /// Раньше выход просто закрывал меню, а движки продолжали работать —
    /// и это правильное поведение, но незаметное. Со стороны программа
    /// выглядела закрытой, при том что весь трафик шёл через неё; чтобы
    /// её остановить, надо было догадаться зайти обратно.
    /// </para>
    /// <para>
    /// Вопрос задаётся, только когда есть что останавливать: спрашивать
    /// при остановленных движках значит приучать жать Enter не глядя.
    /// </para>
    /// </remarks>
    private static bool AskOnExit()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
            return true;

        Console.WriteLine();
        Console.WriteLine($"  Движки работают: {string.Join(", ", state.Services.Select(s => s.Name))}");
        Console.WriteLine();
        Console.WriteLine("  1. Закрыть меню, оставить работать");
        Console.WriteLine("  2. Остановить всё и выйти");
        Console.WriteLine("  0. Вернуться в меню");
        Console.Write("Выбор: ");

        var answer = Console.ReadLine();

        // Ответа не будет — уходим, оставив движки работать. Возврат false
        // здесь означал бы «вернуться в меню», а меню тут же спросило бы
        // снова: этот выбор и был вторым звеном зацикливания.
        if (answer is null)
            return true;

        switch (answer.Trim())
        {
            case "1":
                Console.WriteLine();
                Console.WriteLine("Движки продолжают работать. Вернуться к управлению — запустите NetZapret снова.");
                return true;

            case "2":
                StopEngines(state);
                return true;

            default:
                return false;
        }
    }

    private static void StopEngines(SupervisorState state)
    {
        Console.WriteLine();
        Console.WriteLine("Останавливаю…");

        try
        {
            using var process = Process.GetProcessById(state.SupervisorProcessId);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (Exception)
        {
            // Мог завершиться сам, пока мы спрашивали.
        }

        SupervisorState.Clear(SupervisorState.DefaultPath);
        Console.WriteLine("Остановлено.");
    }

    /// <summary>
    /// Очищает экран, если это возможно.
    /// </summary>
    /// <remarks>
    /// При перенаправленном выводе консольного буфера нет, и Clear бросает
    /// IOException «неверный дескриптор». Меню не должно от этого падать:
    /// его запускают и через конвейер, например при отладке.
    /// </remarks>
    private static void ClearScreen()
    {
        if (Console.IsOutputRedirected)
            return;

        try
        {
            Console.Clear();
        }
        catch (IOException)
        {
            // Консоль без буфера — просто продолжаем без очистки.
        }
    }

    /// <summary>
    /// Версия для заголовка меню.
    /// </summary>
    /// <remarks>
    /// Три числа вместо четырёх: последнее всегда ноль и в разговоре о версии
    /// только мешает. Нужна на виду, потому что вопрос «а какая у тебя стоит»
    /// возникает при первом же разборе неисправности.
    /// </remarks>
    private static string Version
    {
        get
        {
            var v = typeof(MenuCommand).Assembly.GetName().Version;
            return v is null ? string.Empty : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    private static void Draw(AppSettings settings)
    {
        ClearScreen();
        Console.WriteLine($"NetZapret {Version}");
        Console.WriteLine(new string('=', 62));

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = running ? ConsoleColor.Green : ConsoleColor.DarkGray;
        Console.WriteLine(running
            ? $"  Состояние: работает ({string.Join(", ", state!.Services.Select(s => s.Name))})"
            : "  Состояние: остановлено");
        Console.ForegroundColor = previous;

        if (!IsElevated())
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Права: обычные — запуск и автозапуск потребуют администратора");
            Console.ForegroundColor = previous;
        }

        Console.WriteLine();
        Console.WriteLine($"  1. Режим работы .......... {settings.DescribeMode()}");
        Console.WriteLine($"  2. Пресет Zapret ......... {settings.DescribePreset()}");
        Console.WriteLine($"  3. VPN ................... {DescribeVpn(settings)}");
        // DNS и файл hosts сведены в один пункт. Оба про одно — каким адресом
        // окажется имя, — и порознь путали: пин ставится в «Сервисах»,
        // а смотрится был в отдельном пункте через две строки.
        Console.WriteLine($"  4. Имена и адреса ........ {DescribeDns(settings)}, hosts: {DescribeHosts()}");
        Console.WriteLine($"  5. Сервисы и маршруты .... {DescribeRoutes()}");
        Console.WriteLine();
        // Отдельного «собрать конфиг» здесь нет намеренно: он собирается при
        // каждом запуске, а сам по себе ничего не применяет — действует только
        // перезапуск. Пункт предлагал мнимое действие и создавал впечатление
        // обязательного шага, который на деле выполняется сам.
        Console.WriteLine(running ? "  6. Остановить" : "  6. Запустить");
        Console.WriteLine("  7. Проверка блокировок ... что закрыто и чем это лечится");
        Console.WriteLine("  8. Ещё ................... автозапуск, обновление, диагностика");
        Console.WriteLine();
        Console.WriteLine("  0. Выход");
        Console.WriteLine();
    }

    private static string DescribeSubscription(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            return "не задана";

        return Uri.TryCreate(settings.SubscriptionUrl, UriKind.Absolute, out var url)
            ? url.Host
            : "задана";
    }

    private static async Task<AppSettings> HandleAsync(
        string choice,
        AppSettings settings,
        string settingsPath,
        CancellationToken cancellationToken)
    {
        switch (choice)
        {
            case "1":
                settings = ChooseMode(settings);
                break;
            case "2":
                settings = ChoosePreset(settings);
                break;
            case "3":
                settings = await VpnMenuAsync(settings, cancellationToken);
                break;
            case "4":
                settings = await DnsMenuAsync(settings, cancellationToken);
                break;
            case "5":
                await EditServicesAsync(settings, cancellationToken);
                return settings;
            case "6":
                await ToggleRunAsync(settings, cancellationToken);
                return settings;
            case "7":
                await RunBlockCheckAsync(settings, cancellationToken);
                return settings;
            case "8":
                return await MoreMenuAsync(settings, settingsPath, cancellationToken);
            default:
                return settings;
        }

        settings.Save(settingsPath);
        return settings;
    }

    private static AppSettings ChooseMode(AppSettings settings)
    {
        var chosen = Pick(
            "Режим работы",
            [
                ("Выключено — весь трафик напрямую, движки не запускаются", OperatingMode.Off),
                ("Только десинк — VPN не поднимается", OperatingMode.DesyncOnly),
                ("Выборочно — десинк по умолчанию, VPN по правилам", OperatingMode.Selective),
                ("Всё через VPN, российские напрямую", OperatingMode.ProxyAll),
                ("Всё через VPN без исключений — российские сломаются", OperatingMode.ProxyStrict),
            ],
            settings.Mode);

        return chosen is null ? settings : settings with { Mode = chosen.Value };
    }

    private static AppSettings ChoosePreset(AppSettings settings)
    {
        var paths = ZapretPaths.Discover();

        if (paths is null)
        {
            Message("Установка Zapret не найдена.", ConsoleColor.Yellow);
            return settings;
        }

        // Читается заново при каждом заходе: пресет могли положить в папку
        // минуту назад, и требовать ради этого перезапуска незачем.
        var presets = new PresetReader().Read(ZapretPaths.PresetFiles);

        // Заголовок в файле пресета — не обязательно уникальный: свой
        // «Universal V5 my.txt» объявляет себя тем же «Universal V5», что
        // и заводской, и в списке они выглядели двумя одинаковыми строками,
        // между которыми не выбрать. Различаем именем файла, но только там,
        // где заголовки и вправду совпали, — иначе в списке станет шумно.
        var collisions = presets.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = new List<(string, string?)> { ("Не запускать десинк", null) };

        items.AddRange(presets.Select(p =>
        {
            var file = Path.GetFileNameWithoutExtension(p.FilePath);
            var label = collisions.Contains(p.Name) ? $"{p.Name} [{file}]" : p.Name;

            // Значение — имя файла: по нему пресет и находится однозначно,
            // тогда как заголовок теперь заведомо может быть общим.
            return ($"{label}  ({p.ActiveSections.Count()} секций)", (string?)file);
        }));

        var folder = ZapretPaths.PresetDirectory;

        if (folder is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  Папка пресетов: {folder}");
            Console.WriteLine("  Положенный туда файл появится здесь сразу — достаточно");
            Console.WriteLine("  выйти в меню и зайти обратно.");
            Console.WriteLine("  п — открыть папку.");
        }

        var chosen = PickNullable("Пресет Zapret", items, settings.PresetName);

        // Открытие папки — не выбор пресета: возвращаемся к списку, чтобы
        // человек мог положить файл и тут же его увидеть.
        if (chosen.Cancelled && folder is not null && chosen.Raw is { } raw && Is(raw, "п", "p"))
        {
            Report(Maintenance.OpenFolder(folder));
            return ChoosePreset(settings);
        }

        return chosen.Cancelled ? settings : settings with { PresetName = chosen.Value };
    }

    private static async Task<AppSettings> ChooseServerAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
        {
            Message("Сначала задайте подписку — пункт «VPN».", ConsoleColor.Yellow);
            return settings;
        }

        Console.WriteLine();
        Console.WriteLine("Загружаю подписку…");

        IReadOnlyList<ProxyServer> servers;

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(settings.SubscriptionUrl), cancellationToken);
            servers = info.Servers.Where(s => s.IsSupportedBySingBox).ToList();
        }
        catch (Exception ex)
        {
            Message($"Не удалось загрузить: {ex.GetBaseException().Message}", ConsoleColor.Red);
            return settings;
        }

        var cache = ServerHealthCache.Load();
        cache.KeepOnly(servers.Select(s => s.Tag));

        // Проверяем только когда нечего показать. Иначе список появляется
        // сразу, с пометкой о давности, а перепроверка остаётся выбором.
        bool complete = servers.All(s => cache.Find(s.Tag) is not null);

        if (!complete)
            await ProbeForSelectionAsync(servers, cache, cancellationToken);

        while (true)
        {
            var choice = PickServer(settings, servers, cache);

            if (choice.Recheck)
            {
                await ProbeForSelectionAsync(servers, cache, cancellationToken);
                continue;
            }

            return choice.Cancelled ? settings : settings with { PreferredServer = choice.Tag };
        }
    }

    /// <summary>
    /// Проверяет серверы, печатая каждый результат по мере готовности.
    /// </summary>
    /// <remarks>
    /// Построчно, а не полосой точек в конце. Точки не говорили, чей это
    /// ответ, а список появлялся разом в самом конце — и всё ожидание
    /// выглядело зависанием, при том что первые серверы отвечали за доли
    /// секунды и их уже можно было показать.
    /// </remarks>
    private static async Task ProbeForSelectionAsync(
        IReadOnlyList<ProxyServer> servers,
        ServerHealthCache cache,
        CancellationToken cancellationToken)
    {
        var singBox = EngineLocator.FindSingBox();

        if (singBox is null)
        {
            Message("sing-box.exe не найден — проверить нечем.", ConsoleColor.Yellow);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Проверяю серверы ({servers.Count}), мёртвые отвечают дольше живых…");
        Console.WriteLine();

        var previous = Console.ForegroundColor;
        int done = 0;

        try
        {
            await new ProxyProbe(singBox).RunManyAsync(
                servers,
                new ProbeOptions
                {
                    // Внешний адрес здесь не показывают, а его поиск на каждом
                    // живом сервере перебирает три перекрытые службы подряд.
                    LookupExternalIp = false,

                    // Короче общего: выбор сервера — это ожидание перед
                    // экраном, и полминуты на мёртвую Европу тут не окупаются.
                    TotalTimeout = TimeSpan.FromSeconds(8),

                    // Больше общего: все проверки независимы, а ждать их
                    // тремя очередями вместо двух незачем.
                    Parallelism = 7,
                },
                result =>
                {
                    done++;

                    cache.Set(new ServerHealth
                    {
                        Tag = result.ServerTag,
                        Success = result.Success,
                        LatencyMs = result.Latency?.TotalMilliseconds,
                        CheckedAt = DateTimeOffset.Now,
                    });

                    Console.ForegroundColor = result.Success ? ConsoleColor.Green : ConsoleColor.Red;

                    var state = result.Latency is { } latency
                        ? $"{latency.TotalMilliseconds,5:0} мс"
                        : "не работает";

                    Console.WriteLine($"  [{done,2}/{servers.Count}] {state,-12} {Truncate(result.ServerTag, 34)}");
                    Console.ForegroundColor = previous;
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = previous;
            Console.WriteLine();
            Console.WriteLine($"Проверка не удалась: {ex.GetBaseException().Message}");
        }

        // Сохраняем сразу: следующий заход в список покажет эти цифры
        // без повторного ожидания.
        cache.Save();
    }

    private readonly record struct ServerChoice(bool Cancelled, bool Recheck, string? Tag);

    private static ServerChoice PickServer(
        AppSettings settings,
        IReadOnlyList<ProxyServer> servers,
        ServerHealthCache cache)
    {
        // Живые впереди, среди них — по отклику. Мёртвые в конец: они нужны
        // в списке (выбор может быть осознанным), но не первыми.
        var ordered = servers
            .Select(s => (Server: s, Health: cache.Find(s.Tag)))
            .OrderBy(x => x.Health?.Success == true ? 0 : 1)
            .ThenBy(x => x.Health?.LatencyMs ?? double.MaxValue)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("Сервер VPN");
        Console.WriteLine(new string('-', 62));

        var previous = Console.ForegroundColor;

        Console.WriteLine(
            $"   1. Авто — быстрейший из живых{(settings.PreferredServer is null ? "   <- сейчас" : string.Empty)}");

        for (int i = 0; i < ordered.Count; i++)
        {
            var (server, health) = ordered[i];
            bool active = string.Equals(server.Tag, settings.PreferredServer, StringComparison.OrdinalIgnoreCase);

            Console.ForegroundColor = health switch
            {
                { Success: true } => ConsoleColor.Green,
                { Success: false } => ConsoleColor.Red,
                _ => previous,
            };

            var state = health switch
            {
                { Success: true, LatencyMs: { } ms } => $"{ms,5:0} мс",
                { Success: true } => "работает",
                { Success: false } => "не работает",
                _ => "не проверен",
            };

            Console.WriteLine(
                $"  {i + 2,2}. {Truncate(server.Tag, 32),-32} {state,-12}" +
                $"{(active ? "   <- сейчас" : string.Empty)}");

            Console.ForegroundColor = previous;
        }

        Console.WriteLine();

        // Давность показывается всегда: цифры из кэша выглядят как свежий
        // замер, и без пометки по ним судили бы о том, что уже изменилось.
        if (cache.OldestAge is { } age)
            Console.WriteLine($"  Проверено: {DescribeAge(age)}");

        Console.WriteLine("   п. Проверить заново");
        Console.WriteLine("   0. Отмена");
        Console.Write("Выбор: ");

        var input = Console.ReadLine()?.Trim();

        if (string.Equals(input, "п", StringComparison.OrdinalIgnoreCase)
            || string.Equals(input, "p", StringComparison.OrdinalIgnoreCase))
        {
            return new ServerChoice(false, Recheck: true, null);
        }

        if (!int.TryParse(input, out var index) || index < 1 || index > ordered.Count + 1)
            return new ServerChoice(Cancelled: true, false, null);

        // Первый пункт — автоподбор, дальше идут серверы.
        return new ServerChoice(false, false, index == 1 ? null : ordered[index - 2].Server.Tag);
    }

    private static string DescribeAge(TimeSpan age) => age switch
    {
        { TotalSeconds: < 90 } => "только что",
        { TotalMinutes: < 60 } => $"{age.TotalMinutes:0} мин назад",
        { TotalHours: < 24 } => $"{age.TotalHours:0} ч назад",
        _ => $"{age.TotalDays:0} дн назад — стоит перепроверить",
    };

    private static string DescribeDns(AppSettings settings)
    {
        var known = DnsProbe.Known.FirstOrDefault(r =>
            string.Equals(r.Address, settings.DnsServer, StringComparison.Ordinal));

        var name = known is null ? settings.DnsServer : $"{known.Name} ({known.Address})";

        return settings.DnsThroughTunnel ? $"{name}, через туннель" : name;
    }

    /// <summary>
    /// Выбор апстрим-резолвера с проверкой.
    /// </summary>
    /// <remarks>
    /// Список не сортируется по отклику и «быстрейший» не подставляется сам:
    /// резолвер решает, какие ответы получит человек. Один отсекает рекламу,
    /// другой вредоносные домены, третий не фильтрует ничего — это выбор,
    /// а не оптимизация, и делать его молча нельзя.
    /// </remarks>
    private static async Task<AppSettings> DnsMenuAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"  Апстрим DNS: {DescribeDns(settings)}");
            Console.WriteLine();
            Console.WriteLine("  1. Выбрать резолвер");
            Console.WriteLine("  2. Проверить резолверы");
            Console.WriteLine(settings.DnsThroughTunnel
                ? "  3. Разрешать имена напрямую"
                : "  3. Разрешать имена через туннель");
            Console.WriteLine($"  4. Файл hosts ............ {DescribeHosts()}");
            Console.WriteLine("  0. Назад");

            // Каталог отсюда убран. Он делал то же, что теперь делает пункт
            // «Закрепить пин в hosts», но через наш резолвер — и делал молча
            // ничего, если не выбрать набор адресов, чего меню не требовало.
            // Два пути к одной цели, из которых один тихо не работает, хуже
            // одного: человек чинит не тем и не знает об этом.
            if (settings.AddressServices.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"  Прежняя настройка «Каталог Zapret» ({settings.AddressServices.Count} сервисов)");
                Console.WriteLine("  больше не применяется. Её место — «Сервисы и маршруты» →");
                Console.WriteLine("  нужный сервис → «Закрепить пин в hosts».");
                Console.WriteLine("  9 — забыть её.");
            }
            Console.Write("Выбор: ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    return await ChooseResolverAsync(settings, cancellationToken);

                case "2":
                    await CheckResolversAsync(settings, cancellationToken);
                    break;

                case "4":
                    await HostsMenuAsync(cancellationToken);
                    break;

                case "9" when settings.AddressServices.Count > 0:
                    settings = settings with { AddressServices = [], AddressProfile = null };
                    settings.Save(AppSettings.DefaultPath);
                    Message("Забыто. Адреса теперь ставятся пином в hosts.", ConsoleColor.Green);
                    break;

                case "3":
                    var next = settings with { DnsThroughTunnel = !settings.DnsThroughTunnel };

                    Message(next.DnsThroughTunnel
                        ? "Имена будут разрешаться внутри туннеля — оператор их не увидит. " +
                          "Пока туннель не поднят, не откроется ничего."
                        : "Имена будут разрешаться напрямую.", ConsoleColor.Green);

                    return next;

                default:
                    return settings;
            }
        }
    }

    private static async Task CheckResolversAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Console.WriteLine();

        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
        {
            // Мы сами закрываем DoH к системным резолверам внутри туннеля,
            // и наш собственный запрос попадает под то же правило.
            Message(
                "Туннель поднят — замер занизит доступность: мы сами закрываем DoH " +
                "внутри туннеля. Для чистой картины остановите движки.",
                ConsoleColor.Yellow);
        }

        Console.WriteLine("Проверяю резолверы…");
        Console.WriteLine();

        var previous = Console.ForegroundColor;

        await DnsProbe.CheckAllAsync(
            DnsProbe.Known,
            TimeSpan.FromSeconds(5),
            r =>
            {
                Console.ForegroundColor = r.Works ? ConsoleColor.Green : ConsoleColor.Red;

                var latency = r.Works && r.Latency is { } l ? $"{l.TotalMilliseconds,5:0} мс" : "не отвечает";

                Console.WriteLine($"  {r.Resolver.Name,-14} {r.Resolver.Address,-18} {latency}");
                Console.ForegroundColor = previous;
            },
            cancellationToken);

        Pause();
    }

    private static async Task<AppSettings> ChooseResolverAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Проверяю…");

        var results = await DnsProbe.CheckAllAsync(
            DnsProbe.Known, TimeSpan.FromSeconds(5), null, cancellationToken);

        var byAddress = results.ToDictionary(r => r.Resolver.Address, StringComparer.Ordinal);
        var previous = Console.ForegroundColor;

        Console.WriteLine();

        for (int i = 0; i < DnsProbe.Known.Count; i++)
        {
            var resolver = DnsProbe.Known[i];
            var result = byAddress.GetValueOrDefault(resolver.Address);

            Console.ForegroundColor = result switch
            {
                { Works: true } => ConsoleColor.Green,
                { Works: false } => ConsoleColor.Red,
                _ => previous,
            };

            var latency = result is { Works: true, Latency: { } l } ? $"{l.TotalMilliseconds,5:0} мс" : "не отвечает";
            var active = string.Equals(resolver.Address, settings.DnsServer, StringComparison.Ordinal)
                ? "   <- сейчас"
                : string.Empty;

            Console.WriteLine($"  {i + 1}. {resolver.Name,-12} {resolver.Address,-18} {latency}{active}");
            Console.ForegroundColor = previous;

            if (resolver.Note is { } note)
                Console.WriteLine($"     {note}");
        }

        Console.WriteLine("  0. Отмена");
        Console.Write("Выбор: ");

        var input = Console.ReadLine()?.Trim();

        if (!int.TryParse(input, out var index) || index < 1 || index > DnsProbe.Known.Count)
            return settings;

        var chosen = DnsProbe.Known[index - 1];

        Message($"Апстрим DNS: {chosen.Name} ({chosen.Address}). Применится при следующем запуске.",
            ConsoleColor.Green);

        return settings with { DnsServer = chosen.Address };
    }

    private static string DescribeVpn(AppSettings settings) =>
        $"{DescribeSubscription(settings)}, {settings.DescribeServer()}";

    /// <summary>
    /// Всё, что относится к VPN: подписка, выбор сервера, проверка.
    /// </summary>
    /// <remarks>
    /// Три пункта верхнего уровня на одну тему заставляли вспоминать, где
    /// что лежит, хотя порядок действий всегда один: вставить ссылку,
    /// посмотреть, что живо, выбрать сервер. Ни один из трёх не имеет
    /// смысла без остальных.
    /// </remarks>
    private static async Task<AppSettings> VpnMenuAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine($"  Подписка: {DescribeSubscription(settings)}");
            Console.WriteLine($"  Сервер:   {settings.DescribeServer()}");
            Console.WriteLine();
            Console.WriteLine($"  Автовыбор: {(settings.ForeignExitsOnly ? "только зарубежные" : "любой, включая отечественные")}");
            Console.WriteLine();
            Console.WriteLine("  1. Выбрать сервер");
            Console.WriteLine("  2. Проверить серверы");
            Console.WriteLine("  3. Изменить ссылку подписки");
            Console.WriteLine(settings.ForeignExitsOnly
                ? "  4. Разрешить автовыбору отечественные серверы"
                : "  4. Держать автовыбор на зарубежных серверах");
            Console.WriteLine("  0. Назад");
            Console.Write("Выбор: ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    return await ChooseServerAsync(settings, cancellationToken);
                case "2":
                    await ProbeAsync(settings, cancellationToken);
                    break;
                case "3":
                    return AskSubscription(settings);

                case "4":
                    var toggled = settings with { ForeignExitsOnly = !settings.ForeignExitsOnly };

                    Message(toggled.ForeignExitsOnly
                        ? "Автовыбор больше не возьмёт отечественный сервер. Выбрать такой "
                          + "вручную по-прежнему можно."
                        : "Автовыбор снова может взять отечественный сервер. Он ближе всех, "
                          + "поэтому и будет выбран чаще прочих — а сервисы, закрывающиеся "
                          + "от страны, через него не заработают.",
                        ConsoleColor.Green);

                    return toggled;
                default:
                    return settings;
            }
        }
    }

    private static AppSettings AskSubscription(AppSettings settings)
    {
        Console.WriteLine();
        Console.WriteLine("Вставьте ссылку на подписку (пусто — оставить как есть):");
        Console.Write("> ");

        var input = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(input))
            return settings;

        var url = SubscriptionClient.Unwrap(new Uri(input));
        return settings with { SubscriptionUrl = url.ToString() };
    }

    private static string DescribeRoutes()
    {
        var file = UserRulesFile.Load();
        int active = file.Entries.Count(e => e.Enabled);

        if (file.Entries.Count == 0)
            return "нет своих";

        return active == file.Entries.Count
            ? $"{active}"
            : $"{active} из {file.Entries.Count}";
    }

    /// <summary>
    /// Правка маршрутов: спросить про сервис, показать текущее решение, дать выбрать.
    /// </summary>
    /// <remarks>
    /// Задумано как ответ на вопрос «а куда пойдёт вот это»: человек вводит
    /// имя, видит путь с основанием и тут же может его сменить. Тип совпадения
    /// определяется по виду строки, чтобы не заставлять помнить ключи.
    /// </remarks>
    /// <summary>
    /// Список сервисов: Discord, YouTube и прочее, как о них думает человек.
    /// </summary>
    /// <remarks>
    /// Плоский список доменов остаётся ниже отдельным пунктом: домен, добавленный
    /// руками и не относящийся ни к одному сервису, не должен пропасть из виду.
    /// </remarks>
    private static async Task EditServicesAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine("Сервисы и маршруты");
            Console.WriteLine(new string('=', 62));

            RuleEngine engine;
            var zapretRoot = ZapretPaths.Discover()?.Root;
            var userRules = UserRulesFile.Load();

            try
            {
                engine = RuleSetLoader.LoadLayered(settings.RulesPath, UserRulesFile.DefaultPath);
                RuleSetExpander.Expand(engine.RuleSet, zapretRoot);
            }
            catch (Exception ex)
            {
                Message($"Правила не читаются: {ex.GetBaseException().Message}", ConsoleColor.Red);
                return;
            }

            var services = ServiceCatalog.All;
            var previous = Console.ForegroundColor;

            Console.WriteLine();

            for (int i = 0; i < services.Count; i++)
            {
                var parts = ServiceRouting.Describe(services[i], engine, zapretRoot, userRules);
                var summary = ServiceRouting.Summarize(parts);

                Console.WriteLine($"  {i + 1,2}. {services[i].Name,-22} {summary}");
            }

            Console.ForegroundColor = previous;

            Console.WriteLine();
            Console.WriteLine("  Номер — открыть сервис и направить его части.");
            Console.WriteLine("  д — свои домены и программы списком.");
            Console.WriteLine("  п — проверить, что закрыто, и сверить с этой таблицей.");
            Console.WriteLine("  Пусто — назад.");
            Console.WriteLine();
            Console.Write("> ");

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return;

            if (string.Equals(input, "д", StringComparison.OrdinalIgnoreCase)
                || string.Equals(input, "d", StringComparison.OrdinalIgnoreCase))
            {
                EditRoutes(settings);
                continue;
            }

            if (string.Equals(input, "п", StringComparison.OrdinalIgnoreCase)
                || string.Equals(input, "p", StringComparison.OrdinalIgnoreCase))
            {
                ClearScreen();

                // Быстрая: отсюда её запускают, чтобы свериться с только что
                // изменённой таблицей, а не разбираться заново. За подробным
                // разбором есть отдельный пункт меню.
                await BlockCheckCommand.ExecuteAsync(
                    settings,
                    settings.RulesPath,
                    UserRulesFile.DefaultPath,
                    zapretRoot,
                    only: null,
                    CheckDepth.Quick,
                    cancellationToken);

                Pause();
                continue;
            }

            if (int.TryParse(input, out var index) && index >= 1 && index <= services.Count)
                EditService(settings, services[index - 1], zapretRoot);
        }
    }

    /// <summary>
    /// Части одного сервиса и их направление.
    /// </summary>
    private static void EditService(AppSettings settings, ServiceDefinition service, string? zapretRoot)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine(service.Name);
            Console.WriteLine(new string('=', 62));

            RuleEngine engine;
            var userRules = UserRulesFile.Load();

            try
            {
                engine = RuleSetLoader.LoadLayered(settings.RulesPath, UserRulesFile.DefaultPath);
                RuleSetExpander.Expand(engine.RuleSet, zapretRoot);
            }
            catch (Exception ex)
            {
                Message($"Правила не читаются: {ex.GetBaseException().Message}", ConsoleColor.Red);
                return;
            }

            var parts = ServiceRouting.Describe(service, engine, zapretRoot, userRules);
            var previous = Console.ForegroundColor;

            Console.WriteLine();

            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];

                if (p.DomainCount == 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.WriteLine($"  {i + 1}. {p.Part.Name,-24} список не найден");
                    Console.ForegroundColor = previous;
                    continue;
                }

                Console.ForegroundColor = p.Mode switch
                {
                    RoutingMode.Proxy => ConsoleColor.Cyan,
                    RoutingMode.Desync => ConsoleColor.Green,
                    _ => ConsoleColor.DarkGray,
                };

                // Пометка «ваш выбор» существенна: без неё непонятно, что
                // именно можно вернуть к заводскому, а что и так заводское.
                var mark = p.Explicit ? "  <- ваш выбор" : string.Empty;

                var unit = p.Part.ByAddress ? "подс." : "дом.";

                Console.WriteLine(
                    $"  {i + 1}. {p.Part.Name,-26} {p.DescribeMode(),-10} {p.DomainCount,4} {unit}{mark}");

                Console.ForegroundColor = previous;

                if (p.Part.Note is { } note)
                    Console.WriteLine($"     {note}");
            }

            Console.WriteLine();
            Console.WriteLine("  Номер — изменить направление части.");
            Console.WriteLine("  Пусто — назад.");
            Console.WriteLine();
            Console.Write("> ");

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return;

            if (int.TryParse(input, out var index) && index >= 1 && index <= parts.Count)
            {
                if (parts[index - 1].DomainCount == 0)
                {
                    Message("Списка нет — направлять нечего.", ConsoleColor.Yellow);
                    continue;
                }

                ChoosePartMode(parts[index - 1], settings);
            }
        }
    }

    /// <summary>
    /// Куда направить часть сервиса.
    /// </summary>
    private static void ChoosePartMode(ServiceRouting.PartStatus part, AppSettings settings)
    {
        Console.WriteLine();
        Console.WriteLine($"  {part.Part.Name}: сейчас {part.DescribeMode()}");
        Console.WriteLine(part.Part.ByAddress
            ? $"  Список: {part.Part.List} ({part.DomainCount} подсетей, например {part.Example})"
            : $"  Список: {part.Part.List} ({part.DomainCount} доменов, например {part.Example})");

        // Пин показывается до выбора, потому что он и есть один из вариантов.
        // Спорит он с маршрутом не так, как кажется: адрес берётся из файла,
        // но имя вынюхивается из рукопожатия, и правило по домену срабатывает
        // поверх прибитого адреса. Победителем выходит правило.
        var pinned = PinnedNamesOf(part);

        if (pinned.Count > 0)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine();
            Console.WriteLine($"  Прибито в hosts: {string.Join(", ", pinned.Take(4).Select(p => $"{p.Key} → {p.Value}"))}");

            if (pinned.Count > 4)
                Console.WriteLine($"  …и ещё {pinned.Count - 4}");

            // Спорит с пином только «через VPN»: там прибитый адрес уводится
            // в туннель. Десинк адреса не меняет — он калечит пакеты по пути
            // к нему же, — и вместе с пином работает как ни в чём не бывало.
            if (part.Mode is RoutingMode.Proxy)
            {
                Console.WriteLine("  Но режим «VPN» уводит прибитый адрес в туннель, и пин теряет смысл.");
                Console.WriteLine("  Нужен адрес из файла — возьмите пункт 4, он поставит «напрямую».");
            }

            Console.ForegroundColor = previous;
        }

        Console.WriteLine();
        Console.WriteLine("  1. Напрямую");
        Console.WriteLine("  2. Десинк");
        Console.WriteLine("  3. VPN");
        Console.WriteLine("  4. Закрепить пин в hosts");
        Console.WriteLine("  5. Убрать свой выбор — вернуть как было");
        Console.WriteLine("  0. Оставить как есть");
        Console.Write("Выбор: ");

        var choice = Console.ReadLine()?.Trim();
        var file = UserRulesFile.Load();

        // Правило пишется того же вида, каким часть задана: адресная часть
        // доменным правилом не направляется — оно попросту не совпадёт
        // с трафиком, который ходит мимо DNS.
        var kind = part.Part.ByAddress ? MatchKind.IpSet : MatchKind.HostList;

        switch (choice)
        {
            case "1":
                file.Set(kind, part.Part.List, RoutingMode.Direct);
                break;
            case "2":
                file.Set(kind, part.Part.List, RoutingMode.Desync);
                break;
            case "3":
                file.Set(kind, part.Part.List, RoutingMode.Proxy);
                break;
            case "4":
                PinPart(part, file, kind);
                return;
            case "5":
                file.Remove(kind, part.Part.List);
                UnpinOurs(part);
                break;
            default:
                return;
        }

        file.Save();
        Message("Записано. Применится при следующем запуске.", ConsoleColor.Green);

        OfferDiscordRestart(settings, part.Part.List);
    }

    /// <summary>
    /// Закрепляет имена части в hosts и уводит её маршрут «напрямую».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Второе — не довесок, а условие работы первого. Пин задаёт адрес,
    /// но не дорогу: TUN вынюхивает имя из рукопожатия, и правило по домену
    /// срабатывает поверх прибитого адреса. Оставить пин при режиме «VPN»
    /// значит записать в файл строку, которая ни на что не влияет.
    /// </para>
    /// <para>
    /// Так и потерялся ChatGPT: прибитый адрес был жив и отвечал, а правило
    /// уводило соединение в туннель, который в тот час не работал. Проверка
    /// объявляла мёртвым адрес, живой прокси числился трупом, и чинили
    /// не то полдня.
    /// </para>
    /// </remarks>
    private static void PinPart(ServiceRouting.PartStatus part, UserRulesFile file, MatchKind kind)
    {
        if (part.Part.ByAddress)
        {
            Message("Часть задана подсетями, а hosts понимает только имена.", ConsoleColor.Yellow);
            return;
        }

        var zones = HostListReader.Read(part.Part.List, ZapretPaths.Discover()?.Root, out _)
            .Select(d => d.TrimStart('*', '.'))
            .ToList();

        // Свой каталог спрашивается первым: он для того и заведён, что нужного
        // имени в чужом обычно нет вовсе.
        var own = OwnCatalog.Load();

        foreach (var problem in own.Problems)
            Message($"Каталог NetZapret: {problem}", ConsoleColor.Yellow);

        var mine = own.For(zones);
        var catalog = ZapretCatalog.Discover();

        if (catalog is null && mine.Count == 0)
        {
            Message("Каталог адресов не найден — брать адрес неоткуда.", ConsoleColor.Yellow);
            return;
        }

        // Сервисы каталога, покрывающие хоть одно наше имя. Списки Zapret
        // и каталог ведутся порознь, и совпадение по названию сервиса
        // не гарантировано — сверяем по именам.
        var services = catalog?.NamesByService()
            .Where(pair => pair.Value.Any(name => Covers(zones, name)))
            .Select(pair => pair.Key)
            .ToList() ?? [];

        if (services.Count == 0 && mine.Count == 0)
        {
            Message("Ни в одном каталоге нет адресов для имён этой части.", ConsoleColor.Yellow);
            return;
        }

        var profile = ChooseProfile(catalog, services, zones, mine);

        if (profile is null)
            return;

        Dictionary<string, string> answers;

        if (profile == HonestResolver)
        {
            var known = catalog?.Answers(services, null).Keys.Where(n => Covers(zones, n)).ToList() ?? [];
            answers = AskHonestResolver(zones, known);
        }
        else if (profile.StartsWith(OwnPrefix, StringComparison.Ordinal))
        {
            var entry = mine[int.Parse(profile[OwnPrefix.Length..])];

            answers = entry.Resolve
                ? AskHonestResolver(entry.Names.Select(n => n.TrimStart('*', '.')).ToList(), [])
                : entry.Names
                    .Select(n => n.TrimStart('*', '.'))
                    .ToDictionary(n => n, _ => entry.Addresses[0], StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            answers = catalog!.Answers(services, profile)
                .Where(pair => Covers(zones, pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        if (answers.Count == 0)
        {
            Message("Этот набор не покрывает ни одного имени части.", ConsoleColor.Yellow);
            return;
        }

        try
        {
            var result = HostsEditor.Pin(answers, note: $"{part.Part.Name} — {DescribeSource(profile, mine)}");

            var shadowing = ClearShadowingRules(file, zones);

            file.Set(kind, part.Part.List, RoutingMode.Direct);
            file.Save();

            HostsEditor.FlushDns();

            Message(
                $"Прибито имён: {answers.Count}. Маршрут переведён на «напрямую» — " +
                "иначе пин не работает. Копия файла: " + Path.GetFileName(result.Backup ?? "—"),
                ConsoleColor.Green);

            if (shadowing.Count > 0)
            {
                Message(
                    "Убраны прежние правила, которые перекрывали новое: " +
                    string.Join(", ", shadowing) + ".",
                    ConsoleColor.Yellow);
            }

            if (result.Shadowed.Count > 0)
            {
                Message(
                    $"В файле есть и чужие записи на те же имена ({result.Shadowed.Count}). " +
                    "Наш блок стоит выше и разбирается первым, но снимете наш — вернутся они.",
                    ConsoleColor.Yellow);
            }
        }
        catch (UnauthorizedAccessException)
        {
            Message("Нет прав на запись в hosts — запустите программу от администратора.", ConsoleColor.Red);
        }
        catch (IOException ex)
        {
            Message($"Не удалось записать hosts: {ex.Message}", ConsoleColor.Red);
        }
    }

    /// <summary>
    /// Каким набором адресов прибивать.
    /// </summary>
    /// <remarks>
    /// Наборы показываются с покрытием именно этой части, а не всего каталога:
    /// «818 имён» ничего не говорит человеку, выбирающему адрес для ChatGPT,
    /// а «покрывает 4 из 4» говорит всё. Лучшего набора среди них нет — каждый
    /// живёт ровно столько, сколько живёт чужой узел за ним.
    /// </remarks>
    /// <summary>Пометка выбора из нашего каталога; за ней идёт номер записи.</summary>
    private const string OwnPrefix = "\0own:";

    /// <summary>Чем подписать наш блок в hosts.</summary>
    private static string DescribeSource(string profile, IReadOnlyList<OwnCatalogEntry> mine) =>
        profile == HonestResolver ? "честный резолвер"
        : profile.StartsWith(OwnPrefix, StringComparison.Ordinal)
            ? mine[int.Parse(profile[OwnPrefix.Length..])].Name
            : $"набор {profile}";

    /// <summary>
    /// Убирает свои правила, которые перекрыли бы новое «напрямую».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Правила проверяются по порядку, и выигрывает первое совпавшее.
    /// Закрепление пина дописывает правило на список, а оно оказывается
    /// в конце — под прежними доменными, которые человек ставил раньше.
    /// Пин при этом встаёт, а маршрут к нему нет.
    /// </para>
    /// <para>
    /// Так и вышло у пользователя: <c>*.chatgpt.com</c> и <c>*.openai.com</c>
    /// стояли первыми и вторыми со значением «через VPN», а записанное
    /// пунктом «Закрепить пин» правило «напрямую» — тринадцатым. Адрес
    /// из каталога прибивался исправно и тут же уводился в туннель,
    /// и выглядело это так, будто каталог ломает сервис.
    /// </para>
    /// <para>
    /// Убираются только свои доменные правила и только те, что покрывают
    /// имена этой части. Поставляемый набор не трогается: он ниже своего
    /// по старшинству и перекрыть новое правило не может.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<string> ClearShadowingRules(UserRulesFile file, IReadOnlyList<string> zones)
    {
        var removed = new List<string>();

        for (int i = file.Entries.Count - 1; i >= 0; i--)
        {
            var entry = file.Entries[i];

            if (entry.Match != MatchKind.Domain)
                continue;

            var zone = entry.Value.TrimStart('*', '.');

            // Совпадение в обе стороны: правило на зону перекрывает имя части,
            // а правило на имя — покрывается зоной списка. Оба случая мешают.
            bool touches = zones.Any(z =>
                string.Equals(z, zone, StringComparison.OrdinalIgnoreCase)
                || z.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase)
                || zone.EndsWith("." + z, StringComparison.OrdinalIgnoreCase));

            if (!touches)
                continue;

            removed.Add(entry.Value);
            file.RemoveAt(i);
        }

        return removed;
    }

    private static string? ChooseProfile(
        ZapretCatalog? catalog,
        IReadOnlyList<string> services,
        IReadOnlyList<string> zones,
        IReadOnlyList<OwnCatalogEntry> mine)
    {
        // Каталог один, и показывается он одним списком. Наборы Zapret входят
        // в него наравне со своими записями: для человека это один вопрос —
        // чей адрес поставить, — и делить ответ на два списка по признаку
        // того, кто эти адреса собрал, значит объяснять ему наше устройство
        // вместо его задачи.
        Console.WriteLine();
        Console.WriteLine("  Каталог NetZapret — чей адрес поставить:");

        var offered = new List<string>();

        // Свои записи первыми: они заведены под то, чего в наборах Zapret нет,
        // и подходящая среди них — обычно самая точная из предложенных.
        for (int i = 0; i < mine.Count; i++)
        {
            offered.Add(OwnPrefix + i);
            Console.WriteLine(mine[i].Resolve
                ? $"  {offered.Count}. {mine[i].Name} — спросить честный резолвер"
                : $"  {offered.Count}. {mine[i].Name} — {string.Join(", ", mine[i].Addresses)}");
        }

        // Свой адрес — прежде чужого. Честный резолвер отдаёт настоящий адрес
        // сервиса и не стоит ничего; наборы ниже уводят трафик через чужую
        // машину, и предлагать это раньше значит советовать дорогое решение
        // там, где хватает бесплатного.
        //
        // Но не дважды: запись каталога с resolve: honest делает ровно то же
        // самое, и два пункта, отличающиеся только формулировкой, заставляли
        // выбирать между одинаковым.
        if (!mine.Any(m => m.Resolve))
        {
            offered.Add(HonestResolver);
            Console.WriteLine($"  {offered.Count}. Спросить честный резолвер сейчас — настоящий адрес по DoH");
        }

        var sets = (catalog?.Profiles() ?? [])
            .Select(p => (p.Id, p.Name, Covered: catalog!.Answers(services, p.Id).Count(a => Covers(zones, a.Key))))
            .Where(p => p.Covered > 0)
            .ToList();

        if (sets.Count > 0)
        {
            // Чем эти адреса являются, сказано вслух. «XBOX DNS» звучит как
            // название службы имён, а это чужой посредник: через него пойдёт
            // трафик, он видит, куда вы ходите, и живёт ровно столько, сколько
            // его содержат. Годится он там, где сайт отказывает по стране, —
            // честный адрес отказал бы точно так же.
            Console.WriteLine();
            Console.WriteLine("  Через чужой прокси — если сайт отказывает по стране:");

            foreach (var (id, name, covered) in sets)
            {
                offered.Add(id);
                Console.WriteLine($"  {offered.Count}. {name} — имён: {covered}");
            }
        }

        if (offered.Count == 0)
        {
            Message("Ни один набор не покрывает эти имена.", ConsoleColor.Yellow);
            return null;
        }

        Console.WriteLine();
        Console.WriteLine("  0. Отмена");
        Console.Write("Выбор: ");

        var input = Console.ReadLine()?.Trim();

        return int.TryParse(input, out var index) && index >= 1 && index <= offered.Count
            ? offered[index - 1]
            : null;
    }

    /// <summary>Пометка выбора «спросить резолвер», а не взять из каталога.</summary>
    private const string HonestResolver = "\0honest";

    /// <summary>
    /// Берёт адреса у DNS поверх HTTPS вместо каталога.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Лечит тот случай, когда мешает не канал и не страна, а сам ответ
    /// резолвера. <c>image.tmdb.org</c> — CNAME на сеть доставки, и Google
    /// отвечает по нему петлёй <c>127.0.0.1</c>, тогда как Cloudflare отдаёт
    /// живой шард, с которого картинки грузятся. Прибить надо ровно то,
    /// что сказал честный.
    /// </para>
    /// <para>
    /// Годится не всегда, и это стоит понимать. Там, где сайт отказывает
    /// по стране, честный адрес — самый настоящий, и 403 придёт с него же:
    /// ChatGPT так не вылечить, ему нужен чужой прокси из каталога.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string> AskHonestResolver(
        IReadOnlyList<string> zones,
        IReadOnlyList<string> names)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Сами зоны тоже спрашиваем: у списка есть имена, которых нет
        // в каталоге, и именно они чаще всего и ломаются.
        var asked = names.Concat(zones).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        Console.WriteLine();
        Console.WriteLine($"  Спрашиваю адреса и проверяю каждый: имён {asked.Count}…");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };

        foreach (var name in asked)
        {
            var candidates = DohResolver.CandidatesAsync(name, http, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (candidates.Count == 0)
                continue;

            // Проверяется каждый, а не берётся первый. У сети доставки шардов
            // десятки, отвечают не все, и какой достанется — зависит от того,
            // кого спросили: у image.tmdb.org один резолвер отдавал петлю,
            // другой — шард, отдающий постер. Прежде в файл попадал первый
            // непустой ответ, и однажды им оказался адрес, который молчит.
            var working = candidates.FirstOrDefault(
                a => DohResolver.ServesAsync(a, name, CancellationToken.None).GetAwaiter().GetResult());

            if (working is null)
            {
                Console.WriteLine($"    {Truncate(name, 30),-30} ни один из {candidates.Count} не отвечает");
                continue;
            }

            Console.WriteLine(candidates.Count > 1
                ? $"    {Truncate(name, 30),-30} {working} (из {candidates.Count} проверенных)"
                : $"    {Truncate(name, 30),-30} {working}");

            result[name] = working;
        }

        return result;
    }

    /// <summary>
    /// Покрывает ли какая-нибудь зона списка это имя.
    /// </summary>
    /// <remarks>
    /// Сравнение по зоне, а не по точному имени, и это существенно. Списки
    /// Zapret хранят <c>openai.com</c>, каталог — <c>api.openai.com</c>
    /// и ещё три десятка поддоменов; при точном сравнении не совпало бы
    /// ни одно имя, и раздел ответил бы «в каталоге нет адресов» на сервисе,
    /// который каталог покрывает целиком.
    /// </remarks>
    private static bool Covers(IReadOnlyList<string> zones, string name)
    {
        foreach (var zone in zones)
        {
            if (string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Снимает только наши пины для части; чужих не трогает.</summary>
    private static void UnpinOurs(ServiceRouting.PartStatus part)
    {
        if (part.Part.ByAddress)
            return;

        var ours = HostsEditor.Pins();

        if (ours.Count == 0)
            return;

        var zones = HostListReader.Read(part.Part.List, ZapretPaths.Discover()?.Root, out _)
            .Select(d => d.TrimStart('*', '.'))
            .ToList();

        var mine = ours.Keys.Where(name => Covers(zones, name)).ToList();

        if (mine.Count == 0)
            return;

        // Перед снятием проверяем, работает ли прибитое. Пин ставят не от
        // хорошей жизни, и снять рабочий по невнимательности — значит сломать
        // то, что человек чинил; а держать нерабочий незачем вовсе.
        var pins = HostsEditor.Pins();
        var alive = mine
            .Where(name => pins.TryGetValue(name, out var address)
                && DohResolver.ServesAsync(address, name, CancellationToken.None).GetAwaiter().GetResult())
            .ToList();

        if (alive.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Из них отвечают и работают: {alive.Count}.");
            Console.WriteLine("  Снятие вернёт эти имена обычному резолверу — тому самому,");
            Console.WriteLine("  из-за которого пин и понадобился.");
            Console.Write("  Всё равно снять? (д/н): ");

            var answer = Console.ReadLine()?.Trim() ?? string.Empty;

            if (!Is(answer, "д", "y") && !Is(answer, "да", "yes"))
            {
                Message("Оставлено как есть.", ConsoleColor.Green);
                return;
            }
        }

        try
        {
            HostsEditor.Unpin(mine);
            HostsEditor.FlushDns();
            Message($"Снято пинов: {mine.Count}.", ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            Message($"Не удалось снять пины: {ex.Message}", ConsoleColor.Red);
        }
    }

    /// <summary>
    /// Какие имена этой части прибиты в hosts.
    /// </summary>
    /// <remarks>
    /// Сверяется по зоне, как это делает и сам список. Запись
    /// <c>openai.com</c> в списке покрывает <c>api.openai.com</c> в файле,
    /// и при точном сравнении сорок прибитых имён ChatGPT остались бы
    /// незамеченными — а именно они и решают судьбу сервиса.
    /// </remarks>
    private static Dictionary<string, string> PinnedNamesOf(ServiceRouting.PartStatus part)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (part.Part.ByAddress)
            return found;

        var hosts = HostsFile.Read();

        if (hosts.Count == 0)
            return found;

        var zones = HostListReader.Read(part.Part.List, ZapretPaths.Discover()?.Root, out _)
            .Select(d => d.TrimStart('*', '.'))
            .ToList();

        foreach (var (name, addresses) in hosts)
        {
            if (addresses.Count > 0 && Covers(zones, name))
                found[name] = string.Join(", ", addresses);
        }

        return found;
    }

    /// <summary>Выключает записи hosts для названных имён.</summary>
    /// <remarks>
    /// Выключает, а не удаляет: запись может понадобиться обратно, и вернуть
    /// её должно быть так же просто, как снять. Файл при этом копируется —
    /// он общий, и наша ошибка в нём стоит чужой настройки.
    /// </remarks>
    private static void UnpinNames(IReadOnlyList<string> names)
    {
        var lines = HostsEditor.Parse()
            .Where(e => e.Enabled && e.Names.Any(n => names.Contains(n, StringComparer.OrdinalIgnoreCase)))
            .Select(e => e.Line)
            .ToList();

        if (lines.Count == 0)
            return;

        Console.WriteLine();
        Console.Write($"Выключить {lines.Count} {Plural(lines.Count, "строку", "строки", "строк")} в hosts? [д/н]: ");

        var answer = Console.ReadLine()?.Trim();

        if (answer is null || !(answer.StartsWith('д') || answer.StartsWith('y')))
            return;

        try
        {
            var backup = HostsEditor.SetEnabled(lines, enabled: false);

            Say($"Готово. Копия прежнего файла: {backup}", ConsoleColor.Green, pause: false);
            Console.WriteLine(HostsEditor.FlushDns()
                ? "Кэш DNS сброшен — маршрут заработает сразу."
                : "Сбросьте кэш вручную: ipconfig /flushdns");
        }
        catch (UnauthorizedAccessException)
        {
            Say("Нужны права администратора: файл системный.", ConsoleColor.Yellow, pause: false);
        }
        catch (Exception ex)
        {
            Say($"Не вышло: {ex.GetBaseException().Message}", ConsoleColor.Red, pause: false);
        }

        Pause();
    }

    /// <summary>
    /// Предлагает перезапустить Discord, если тронули его маршрут.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Discord запоминает голосовые серверы на сеанс: сменили маршрут голоса,
    /// а он продолжает ходить по-старому, и со стороны это выглядит так,
    /// будто настройка не сработала.
    /// </para>
    /// <para>
    /// Предлагается, а не делается: перезапуск посреди звонка хуже задержки
    /// с применением. И только по прямому согласию в настройках — по
    /// умолчанию выключено.
    /// </para>
    /// </remarks>
    private static void OfferDiscordRestart(AppSettings settings, string list)
    {
        if (!settings.OfferDiscordRestart)
            return;

        if (!list.Contains("discord", StringComparison.OrdinalIgnoreCase))
            return;

        if (System.Diagnostics.Process.GetProcessesByName("Discord").Length == 0)
            return;

        Console.WriteLine();
        Console.WriteLine("  Discord держит голосовые серверы на сеанс и новый маршрут");
        Console.WriteLine("  заметит только после перезапуска.");
        Console.Write("  Перезапустить сейчас? [д/н]: ");

        var answer = Console.ReadLine()?.Trim();

        if (answer is null || !(answer.StartsWith('д') || answer.StartsWith('y')))
            return;

        Report(Maintenance.RestartDiscord());
    }

    private static void EditRoutes(AppSettings settings)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine("Свои домены и программы");
            Console.WriteLine(new string('=', 62));

            var file = UserRulesFile.Load();

            if (file.Entries.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Своих маршрутов нет — всё идёт по базовому набору.");
            }
            else
            {
                Console.WriteLine();

                for (int i = 0; i < file.Entries.Count; i++)
                {
                    var entry = file.Entries[i];
                    var previous = Console.ForegroundColor;

                    if (!entry.Enabled)
                        Console.ForegroundColor = ConsoleColor.DarkGray;

                    Console.WriteLine(
                        $"  {i + 1,-3} {entry.DescribeMatch(),-10} {Truncate(entry.Value, 30),-30} " +
                        $"{entry.DescribeMode(),-10} {(entry.Enabled ? string.Empty : "выключено")}");

                    Console.ForegroundColor = previous;
                }
            }

            Console.WriteLine();
            Console.WriteLine("  Введите приложение или сайт — покажу, куда он пойдёт.");
            Console.WriteLine("  Номер из списка — включить или выключить запись.");
            Console.WriteLine("  Минус перед номером (-2) — удалить запись насовсем.");
            Console.WriteLine("  Два минуса (--) — удалить все записи.");
            Console.WriteLine("  Пусто — назад.");
            Console.WriteLine();
            Console.Write("> ");

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return;

            if (input == "--")
            {
                DeleteAllRoutes(file);
                continue;
            }

            if (int.TryParse(input, out var index))
            {
                // Отрицательный номер — удаление. Выключение запись сохраняет,
                // и накопившийся список выключенного со временем начинает
                // мешать читать нужное.
                if (index < 0 && -index >= 1 && -index <= file.Entries.Count)
                {
                    DeleteRoute(file, -index - 1);
                    continue;
                }

                if (index >= 1 && index <= file.Entries.Count)
                {
                    file.Toggle(index - 1);
                    file.Save();
                    continue;
                }
            }

            AskRoute(settings, input);
        }
    }

    /// <summary>
    /// Удаляет все свои маршруты разом.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Спрашивает дважды и показывает, сколько именно уйдёт. Список копится
    /// месяцами и помнится плохо: человек, набравший два минуса, скорее всего
    /// имел в виду «начать заново», но узнать, что именно он этим стирает,
    /// должен до, а не после.
    /// </para>
    /// <para>
    /// Базовый набор не трогается: удаляется только пользовательский слой,
    /// и всё вернётся к заводским маршрутам, а не к их отсутствию.
    /// </para>
    /// </remarks>
    private static void DeleteAllRoutes(UserRulesFile file)
    {
        if (file.Entries.Count == 0)
            return;

        int count = file.Entries.Count;

        Console.WriteLine();
        Console.WriteLine($"  Удалятся все {count} {Plural(count, "запись", "записи", "записей")}:");

        foreach (var entry in file.Entries.Take(5))
            Console.WriteLine($"    {entry.Value}");

        if (count > 5)
            Console.WriteLine($"    …и ещё {count - 5}");

        Console.WriteLine();
        Console.WriteLine("  Заводские маршруты останутся — уйдёт только ваш выбор поверх них.");
        Console.Write("  Точно удалить всё? [д/н]: ");

        var answer = Console.ReadLine()?.Trim();

        if (answer is null || !(answer.StartsWith('д') || answer.StartsWith('y')))
            return;

        // С конца: удаление сдвигает номера, и проход с начала пропустил бы
        // каждую вторую запись.
        for (int i = file.Entries.Count - 1; i >= 0; i--)
            file.RemoveAt(i);

        file.Save();
        Message($"Удалено записей: {count}. Применится при следующем запуске.", ConsoleColor.Green);
    }

    private static void DeleteRoute(UserRulesFile file, int index)
    {
        var entry = file.Entries[index];

        // Подтверждение потому, что промах по клавише здесь необратим:
        // «-2» вместо «2» стирает запись, а выглядят они рядом.
        Console.WriteLine();
        Console.Write($"Удалить «{entry.Value}» ({entry.DescribeMode()})? [y/N] ");

        var answer = Console.ReadLine()?.Trim();

        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
            return;

        file.RemoveAt(index);
        file.Save();
    }

    private static void AskRoute(AppSettings settings, string target)
    {
        RuleEngine engine;

        try
        {
            engine = RuleSetLoader.LoadLayered(settings.RulesPath, UserRulesFile.DefaultPath);
            RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);
        }
        catch (RuleConfigurationException ex)
        {
            Message($"Не удалось прочитать правила: {ex.Message}", ConsoleColor.Red);
            return;
        }

        var match = RouteCommand.Detect(target);
        var connection = BuildProbe(target, match);
        var decision = engine.Evaluate(connection);

        Console.WriteLine();
        Console.WriteLine($"  {target}  →  {RouteCommand.Describe(decision.Mode)}");
        Console.WriteLine($"  Опознано как: {DescribeMatch(match)}");
        Console.WriteLine($"  Основание:    {decision.Reason ?? "правило по умолчанию"}");

        if (decision.Rule is not null)
            Console.WriteLine($"  Слой:         {(decision.Rule.Source == RuleSource.User ? "ваш выбор" : "базовый набор")}");

        Console.WriteLine();
        Console.WriteLine("  1. Напрямую");
        Console.WriteLine("  2. Десинк");
        Console.WriteLine("  3. VPN");
        Console.WriteLine("  4. Убрать свой выбор");
        Console.WriteLine("  0. Оставить как есть");
        Console.Write("Выбор: ");

        var choice = Console.ReadLine()?.Trim();
        var file = UserRulesFile.Load();

        switch (choice)
        {
            case "1":
                file.Set(match, target, RoutingMode.Direct);
                break;
            case "2":
                file.Set(match, target, RoutingMode.Desync);
                break;
            case "3":
                file.Set(match, target, RoutingMode.Proxy);
                break;
            case "4":
                if (!file.Remove(match, target))
                {
                    Message("Своего правила для него и не было.", ConsoleColor.Yellow);
                    return;
                }

                break;
            default:
                return;
        }

        file.Save();

        // Перехват решается по адресу назначения, поэтому правило по программе
        // сработает лишь для трафика, который вообще дошёл до туннеля.
        if (choice == "3" && match == MatchKind.Process)
        {
            Message(
                "Записано. Учтите: приложениям, которые ходят по голым адресам без DNS, " +
                "нужны их подсети в секции capture — иначе правило не сработает.",
                ConsoleColor.Yellow);

            return;
        }

        Message("Записано. Применится при следующем запуске.", ConsoleColor.Green);
    }

    private static ConnectionEvent BuildProbe(string target, MatchKind match)
    {
        var connection = new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemotePort = 443,
            Direction = ConnectionDirection.Outbound,
        };

        return match switch
        {
            MatchKind.Process => connection with { ExecutablePath = target },
            MatchKind.Ip => connection with { RemoteAddress = System.Net.IPAddress.Parse(target) },
            _ => connection with { Hostname = target.Trim('.') },
        };
    }

    private static string DescribeMatch(MatchKind match) => match switch
    {
        MatchKind.Process => "программа",
        MatchKind.Ip => "адрес",
        MatchKind.IpSet => "список адресов",
        _ => "домен",
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");

    private static void ToggleAutostart(AppSettings settings)
    {
        bool installed = AutostartTask.IsInstalled(AutostartTask.DefaultTaskName);

        if (!IsElevated())
        {
            Message("Нужны права администратора: перезапустите от имени администратора.", ConsoleColor.Yellow);
            return;
        }

        if (installed)
        {
            var (ok, output) = AutostartTask.Remove(AutostartTask.DefaultTaskName);
            Message(ok ? "Автозапуск выключен." : $"Не удалось: {output}", ok ? ConsoleColor.Green : ConsoleColor.Red);
            return;
        }

        var arguments = BuildStartArguments(settings);
        var executable = Environment.ProcessPath;

        if (executable is null)
        {
            Message("Не удалось определить путь к программе.", ConsoleColor.Red);
            return;
        }

        var result = AutostartTask.Install(new AutostartOptions
        {
            ExecutablePath = executable,
            Arguments = arguments,
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UserId = WindowsIdentity.GetCurrent().Name,
        });

        Message(
            result.Ok ? "Автозапуск включён: поднимется при следующем входе." : $"Не удалось: {result.Output}",
            result.Ok ? ConsoleColor.Green : ConsoleColor.Red);
    }

    private static async Task<bool> BuildConfigAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        bool pause = true)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
        {
            Message("Сначала задайте подписку — пункт «VPN».", ConsoleColor.Yellow);
            return false;
        }

        Console.WriteLine();
        Console.WriteLine("Собираю конфиг…");

        try
        {
            var engine = RuleSetLoader.LoadLayered(settings.RulesPath, UserRulesFile.DefaultPath);

            // Режим из настроек перекрывает файл правил: меню не должно
            // переписывать базовый YAML, который ведётся руками.
            var ruleSet = engine.RuleSet with { Operating = settings.Mode };

            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(settings.SubscriptionUrl), cancellationToken);

            var zapretRoot = ZapretPaths.Discover()?.Root;
            var capture = AddressListReader.Expand(ruleSet.CaptureEntries, zapretRoot, out var captureProblems);

            foreach (var problem in captureProblems)
                Console.WriteLine($"Внимание, секция capture — {problem}");

            foreach (var problem in RuleSetExpander.Expand(ruleSet, zapretRoot))
                Console.WriteLine($"Внимание: {problem}");

            var pinned = HostsFile.CollectPinnedProxyAddresses(ruleSet, out var pinnedNotes);

            foreach (var note in pinnedNotes)
                Console.WriteLine($"Файл hosts: {note}");

            // Каталог через резолвер больше не применяется — то же самое
            // делает пин в hosts, и делает на виду. Прежняя настройка
            // называется вслух, чтобы её исчезновение не было тихим.
            if (settings.AddressServices.Count > 0)
            {
                Console.WriteLine(
                    $"Каталог Zapret: прежняя настройка ({settings.AddressServices.Count} сервисов) " +
                    "не применяется — её место заняло «Закрепить пин в hosts».");
            }

            var addresses = AddressOverrides.Merge(new Dictionary<string, string>(), AddressOverrides.Load());

            var result = new SingBoxConfigCompiler().Compile(ruleSet, info.Servers, new SingBoxOptions
            {
                Scope = settings.ProxyOnly ? TunnelScope.ProxyOnly : TunnelScope.Everything,
                DnsServerAddresses = settings.ProxyOnly ? SystemResolvers.Discover() : Array.Empty<string>(),
                DnsServer = settings.DnsServer,
                DnsThroughTunnel = settings.DnsThroughTunnel,
                PreferredServerTag = settings.PreferredServer,
                ForeignExitsOnly = settings.ForeignExitsOnly,
                CaptureAddresses = capture,
                PinnedProxyAddresses = pinned,
                AddressOverrides = addresses,
            });

            SingBoxConfigCompiler.WriteToFile(settings.ProxyConfigPath, result.Json);

            Say(
                $"Готово: {result.UsedServers.Count} серверов, {result.SkippedServers.Count} пропущено.",
                ConsoleColor.Green,
                pause);

            return true;
        }
        catch (Exception ex)
        {
            Say($"Не удалось: {ex.GetBaseException().Message}", ConsoleColor.Red, pause);
            return false;
        }
    }

    private static async Task ToggleRunAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
        {
            try
            {
                using var process = Process.GetProcessById(state.SupervisorProcessId);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception)
            {
                // Мог завершиться сам.
            }

            SupervisorState.Clear(SupervisorState.DefaultPath);
            Message("Остановлено.", ConsoleColor.Green);
            return;
        }

        // Конфиг пересобирается перед каждым запуском. Раздельные пункты
        // «собрать» и «запустить» стоили нам двух вечеров: движок молча
        // поднимался с файлом от прошлой версии правил, и симптом выглядел
        // как «сайт не работает», а не как «вы забыли нажать шестёрку».
        // Правила, подписка и список резолверов меняются чаще, чем человек
        // о них вспоминает, так что дешевле собирать всегда.
        // Без паузы: следом идёт запуск, и «Enter — назад в меню» посреди
        // одного действия читается как его конец.
        if (!settings.NeedsProxy && !settings.NeedsDesync)
        {
            Message(
                "В этом режиме запускать нечего: VPN выключен, пресет не выбран.",
                ConsoleColor.Yellow);
            return;
        }

        if (settings.NeedsProxy && !await BuildConfigAsync(settings, cancellationToken, pause: false))
        {
            if (!File.Exists(settings.ProxyConfigPath))
            {
                Pause();
                return;
            }

            Message("Запускаю с ранее собранным конфигом.", ConsoleColor.Yellow);
        }

        var executable = Environment.ProcessPath;

        if (executable is null)
        {
            Message("Не удалось определить путь к программе.", ConsoleColor.Red);
            return;
        }

        // Без окна: супервизор работает до остановки, и отдельная консоль
        // рядом с меню только мешала — закрыть её случайно значило убить
        // движки. Вывод уходит в журнал, он же показывается пунктом меню.
        using var supervisor = Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = BuildStartArguments(settings),
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Console.WriteLine("Запускаю движки…");

        if (await WaitForSupervisorAsync(supervisor, cancellationToken))
            Message("Запущено.", ConsoleColor.Green);
        else
            Message("Супервизор не поднялся — смотрите журнал супервизора.", ConsoleColor.Red);
    }

    /// <summary>
    /// Ждёт, пока супервизор объявит себя работающим.
    /// </summary>
    /// <remarks>
    /// Опрос, а не фиксированная пауза. Раньше здесь стояла задержка в полторы
    /// секунды, и её не хватало: состояние пишется после того, как поднялись
    /// оба движка, а winws2 с большим пресетом разбирает сотни аргументов
    /// и читает полсотни списков. Меню объявляло неудачу там, где всё
    /// поднималось нормально, — а такое сообщение хуже отсутствия сообщения,
    /// потому что учит не верить сообщениям.
    /// </remarks>
    private static async Task<bool> WaitForSupervisorAsync(
        Process? process,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (DateTime.UtcNow < deadline)
        {
            // Выход процесса — сразу отказ, ждать дальше нечего.
            if (process is { HasExited: true })
                return false;

            var state = SupervisorState.Load(SupervisorState.DefaultPath);

            if (state is not null && state.IsSupervisorAlive())
                return true;

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    public static string SupervisorLogPath => Path.Combine("runtime", "supervisor.log");

    private static string BuildStartArguments(AppSettings settings)
    {
        // Без журнала супервизор запускается с окном: вывод должен куда-то
        // идти, а перенаправление и есть то, что позволяет окно отпустить.
        // Выключив журнал, человек соглашается и на это.
        var arguments = settings.LogsEnabled
            ? $"start --log \"{Path.GetFullPath(SupervisorLogPath)}\""
            : "start";

        // Туннель поднимается только там, где он куда-то ведёт. В режиме
        // «только десинк» sing-box был бы вхолостую поднятым TUN: адаптер
        // есть, маршруты стоят, трафика через них нет — и первая же
        // неисправность ищется вдвое дольше.
        arguments += settings.NeedsProxy
            ? $" --proxy-config \"{Path.GetFullPath(settings.ProxyConfigPath)}\""
            : " --no-proxy";

        if (settings.NeedsDesync)
            arguments += $" --preset \"{settings.PresetName}\"";

        if (settings.VerifyTraffic)
            arguments += " --verify-traffic";

        // Без окна некому подтвердить снятие осиротевших движков, а они
        // удержат TUN и WinDivert, и запуск провалится молча.
        arguments += " --kill-orphans";

        return arguments;
    }

    private static void ShowSupervisorLog()
    {
        var path = Path.GetFullPath(SupervisorLogPath);

        Console.WriteLine();

        if (!File.Exists(path))
        {
            Message("Журнала пока нет — супервизор ещё не запускался.", ConsoleColor.Yellow);
            return;
        }

        Console.WriteLine($"{path}");
        Console.WriteLine(new string('-', 72));

        try
        {
            // Через FileShare.ReadWrite: файл открыт супервизором на запись,
            // и без этого прочитать его во время работы было бы нельзя.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var lines = new Queue<string>();

            while (reader.ReadLine() is { } line)
            {
                lines.Enqueue(line);

                if (lines.Count > 40)
                    lines.Dequeue();
            }

            foreach (var line in lines)
                Console.WriteLine(line);
        }
        catch (Exception ex)
        {
            Message($"Не удалось прочитать: {ex.GetBaseException().Message}", ConsoleColor.Red);
        }

        Pause();
    }

    private static async Task ProbeAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
        {
            Message("Сначала задайте подписку — пункт «VPN».", ConsoleColor.Yellow);
            return;
        }

        Console.WriteLine();

        await ProbeCommand.RunAsync(
            CommandLine.Parse(["probe", "--sub", settings.SubscriptionUrl!]),
            cancellationToken);

        Pause();
    }

    private static void RunDoctor(AppSettings settings)
    {
        Console.WriteLine();
        DoctorCommand.Run(CommandLine.Parse(["doctor", "--config", settings.RulesPath]));
        Pause();
    }

    /// <summary>
    /// Проверка блокировок.
    /// </summary>
    /// <remarks>
    /// Вынесена в главное меню, а не спрятана в диагностику: она отвечает на
    /// вопрос, ради которого программу и открывают, — «почему не работает
    /// и что мне с этим делать». Остальные два пункта показывают состояние
    /// самой программы, и это совсем другая забота.
    /// </remarks>
    private static async Task RunBlockCheckAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ClearScreen();
        Console.WriteLine("Проверка блокировок");
        Console.WriteLine(new string('=', 62));
        Console.WriteLine();
        Console.WriteLine("  1. Быстрая — по имени с каждой части, около минуты");
        Console.WriteLine("  2. Полная — по два имени, дольше, но подробнее");
        Console.WriteLine("  3. Точечная — один сервис, зато целиком");
        Console.WriteLine();
        Console.WriteLine("  0. Назад");
        Console.WriteLine();
        Console.Write("Выбор: ");

        var choice = Console.ReadLine()?.Trim();

        var depth = choice switch
        {
            "1" => CheckDepth.Quick,
            "2" => CheckDepth.Full,
            "3" => CheckDepth.Focused,
            _ => (CheckDepth?)null,
        };

        if (depth is null)
            return;

        string? only = null;

        if (depth == CheckDepth.Focused)
        {
            only = PickService();

            if (only is null)
                return;
        }

        ClearScreen();

        await BlockCheckCommand.ExecuteAsync(
            settings,
            settings.RulesPath,
            UserRulesFile.DefaultPath,
            ZapretPaths.Discover()?.Root,
            only,
            depth.Value,
            cancellationToken);

        Pause();
    }

    /// <summary>
    /// Спрашивает, какой сервис проверять.
    /// </summary>
    /// <remarks>
    /// Списком, а не вводом названия: имена вроде «Instagram и Facebook»
    /// с клавиатуры не угадываются, а промах отвечает «такого нет» —
    /// и человек остаётся гадать, как же он называется.
    /// </remarks>
    private static string? PickService()
    {
        var services = ServiceCatalog.All;

        Console.WriteLine();
        Console.WriteLine("Какой сервис");
        Console.WriteLine();

        for (int i = 0; i < services.Count; i++)
            Console.WriteLine($"  {i + 1,2}. {services[i].Name}");

        Console.WriteLine();
        Console.Write("Номер: ");

        return int.TryParse(Console.ReadLine()?.Trim(), out var index)
            && index >= 1 && index <= services.Count
                ? services[index - 1].Name
                : null;
    }

    /// <summary>
    /// Одна строка про вышедшую версию — если она успела выясниться.
    /// </summary>
    /// <remarks>
    /// Не ждём и не мешаем: проверка идёт в стороне, и пока она не кончилась,
    /// меню о ней молчит. Строка появится при следующей перерисовке, то есть
    /// после первого же действия, — этого достаточно, а держать человека
    /// ради необязательного сообщения нельзя.
    /// </remarks>
    private static void ShowUpdateHint(Task<ReleaseInfo?> check)
    {
        if (!check.IsCompletedSuccessfully || check.Result is not { } release)
            return;

        if (!UpdateCheck.IsNewer(release.Version, UpdateCheck.Current))
            return;

        var previous = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Вышла {release.Version} — обновиться в пункте «Ещё».");
        Console.WriteLine();
        Console.ForegroundColor = previous;
    }

    private static string DescribeHosts()
    {
        var entries = HostsEditor.Parse();
        int active = entries.Count(e => e.Enabled);

        return active == 0 ? "пусто" : $"{active} {Plural(active, "запись", "записи", "записей")}";
    }

    /// <summary>
    /// Редактор файла hosts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Смысл не в том, чтобы повторить блокнот, а в том, чтобы связать файл
    /// с тем, что мы про него знаем. Прибитое имя отменяет любой наш маршрут,
    /// и проверка блокировок это уже показывает; здесь такую запись можно
    /// снять, не уходя под администратора в текстовый редактор.
    /// </para>
    /// <para>
    /// Выключение — комментарий, а не удаление, и перед каждой правкой
    /// делается копия. Файл общий: часть записей ставит редактор Zapret,
    /// часть человек руками, и стереть чужое значит сломать настройку,
    /// которую чинить будут не у нас.
    /// </para>
    /// </remarks>
    private static async Task HostsMenuAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine("Файл hosts");
            Console.WriteLine(new string('=', 62));

            var entries = HostsEditor.Parse();

            if (entries.Count == 0)
            {
                Message("Записей нет — файл пуст или недоступен.", ConsoleColor.Yellow);
                return;
            }

            // Группируем по адресу: имена прибивают пачками, и список
            // из шестисот строк нечитаем, а из десяти адресов — вполне.
            var groups = entries
                .GroupBy(e => new { e.Address, e.Enabled })
                .OrderByDescending(g => g.Sum(e => e.Names.Count))
                .ToList();

            Console.WriteLine();
            Console.WriteLine($"  {"АДРЕС",-40} {"ИМЁН",-6} СОСТОЯНИЕ");
            Console.WriteLine("  " + new string('-', 60));

            var previous = Console.ForegroundColor;

            for (int i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                int names = group.Sum(e => e.Names.Count);

                Console.ForegroundColor = group.Key.Enabled ? previous : ConsoleColor.DarkGray;
                Console.WriteLine(
                    $"  {i + 1,2}. {Truncate(group.Key.Address, 34),-36} {names,-6} " +
                    (group.Key.Enabled ? "действует" : "выключено"));
                Console.ForegroundColor = previous;
            }

            var ours = HostsEditor.Pins();

            if (ours.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  Из них ведём мы: {ours.Count} — они в блоке «netzapret» наверху файла.");
            }

            Console.WriteLine();
            Console.WriteLine("  Номер — включить или выключить все имена этого адреса.");
            Console.WriteLine("  п — проверить, отвечают ли адреса.");
            Console.WriteLine("  с — показать имена одного адреса.");
            // Пункт «к» убран вместе с тем, ради чего был заведён: он гасил
            // записи, чтобы их обслуживал каталог через наш резолвер, а этот
            // путь снят. Гасить их теперь просто некому взамен.
            Console.WriteLine("  о — открыть файл, чтобы дописать своё вручную.");
            Console.WriteLine("  п р — прибрать: забрать все записи к нам и убрать повторы.");
            Console.WriteLine("  Пусто — назад.");
            Console.WriteLine();
            Console.Write("> ");

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return;

            if (Is(input, "п", "p"))
            {
                await CheckPinsAsync(entries, cancellationToken);
                continue;
            }

            if (Is(input, "с", "s"))
            {
                ShowNames(groups.Select(g => (g.Key.Address, g.SelectMany(e => e.Names).ToList())).ToList());
                continue;
            }

            if (Is(input, "о", "o"))
            {
                OpenHostsFile();
                continue;
            }

            if (Is(input, "пр", "pr"))
            {
                TidyHostsFile(entries);
                continue;
            }

            if (int.TryParse(input, out var index) && index >= 1 && index <= groups.Count)
                ToggleGroup(groups[index - 1].ToList(), groups[index - 1].Key.Enabled);
        }
    }

    /// <summary>
    /// Забирает все записи файла в наш блок и убирает повторы.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Спрашивает подтверждение и называет число, потому что действие крупное:
    /// в живом файле оказалось 839 записей на 796 имён, собранных тремя
    /// механизмами сразу. После сбора файл ведём мы одни — и это же значит,
    /// что редактор Zapret GUI, запущенный после, снова допишет своё рядом.
    /// </para>
    /// <para>
    /// Отдельно называется, сколько имён каталог не знает. Их сбор тоже
    /// заберёт, но помнить о них стоит: восстановить их будет неоткуда,
    /// кроме копии файла, — каталог их не отдаст.
    /// </para>
    /// </remarks>
    private static void TidyHostsFile(IReadOnlyList<HostsEntry> entries)
    {
        var active = entries.Where(e => e.Enabled).ToList();
        var names = active.SelectMany(e => e.Names).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var rows = active.Sum(e => e.Names.Count);

        Console.WriteLine();
        Console.WriteLine($"  Записей: {rows}, имён: {names.Count}, повторов: {rows - names.Count}.");

        var catalog = ZapretCatalog.Discover();

        if (catalog is not null)
        {
            var known = catalog.NamesByService().Values
                .SelectMany(v => v)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var strangers = names.Count(n => !known.Contains(n));

            if (strangers > 0)
                Console.WriteLine($"  Из них каталог не знает {strangers} — вернуть их потом будет неоткуда.");
        }

        Console.WriteLine("  Выключенные строки останутся как есть.");
        Console.Write("  Забрать всё к нам? (д/н): ");

        var answer = Console.ReadLine()?.Trim() ?? string.Empty;

        if (!Is(answer, "д", "y") && !Is(answer, "да", "yes"))
            return;

        try
        {
            var result = HostsEditor.Absorb();
            HostsEditor.FlushDns();

            Message(
                $"Собрано имён: {result.Pinned}. Копия прежнего файла: " +
                Path.GetFileName(result.Backup ?? "—"),
                ConsoleColor.Green);
        }
        catch (UnauthorizedAccessException)
        {
            Message("Нет прав на запись в hosts — запустите программу от администратора.", ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            Message($"Не удалось прибрать файл: {ex.Message}", ConsoleColor.Red);
        }
    }

    /// <summary>
    /// Открывает hosts в блокноте для правки руками.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Список с галочками покрывает не всё: свой адрес, которого нет ни в одном
    /// наборе каталога, вписать больше негде. Отправлять человека искать файл
    /// в <c>System32\drivers\etc</c> — лишний повод ошибиться папкой.
    /// </para>
    /// <para>
    /// Блокнот запускается отдельно и не ждётся: файл общий, и держать наше
    /// меню запертым, пока открыто чужое окно, незачем. О сбросе кэша сказано
    /// сразу — без него правка не вступит в силу до перезагрузки, и это самая
    /// частая причина «я исправил, а не работает».
    /// </para>
    /// </remarks>
    private static void OpenHostsFile()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "notepad.exe",
                ArgumentList = { HostsFile.DefaultPath },
                UseShellExecute = true,
            });

            Message(
                "Открыт блокнот. Свои строки пишите вне блока «netzapret» — его мы " +
                "перезаписываем. После правки нажмите «п» здесь либо выполните " +
                "ipconfig /flushdns, иначе изменение не вступит в силу.",
                ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            Message(
                $"Не удалось открыть блокнот: {ex.Message}. Файл лежит здесь: {HostsFile.DefaultPath}",
                ConsoleColor.Red);
        }
    }


    private static bool Is(string input, string ru, string en) =>
        string.Equals(input, ru, StringComparison.OrdinalIgnoreCase)
        || string.Equals(input, en, StringComparison.OrdinalIgnoreCase);

    private static void ToggleGroup(IReadOnlyList<HostsEntry> group, bool enabled)
    {
        var address = group[0].Address;
        int names = group.Sum(e => e.Names.Count);
        var action = enabled ? "Выключить" : "Включить";

        Console.WriteLine();
        Console.Write($"{action} {names} {Plural(names, "имя", "имени", "имён")} на {address}? [д/н]: ");

        var answer = Console.ReadLine()?.Trim();

        if (answer is null || !(answer.StartsWith('д') || answer.StartsWith('y')))
            return;

        try
        {
            var backup = HostsEditor.SetEnabled(group.Select(e => e.Line).ToList(), !enabled);

            Message($"Готово. Копия прежнего файла: {backup}", ConsoleColor.Green);

            // Без сброса кэша правка не вступит в силу, и человек справедливо
            // решит, что редактор не работает.
            Console.WriteLine(HostsEditor.FlushDns()
                ? "Кэш DNS сброшен."
                : "Кэш DNS сбросить не вышло — выполните ipconfig /flushdns вручную.");
        }
        catch (UnauthorizedAccessException)
        {
            Message(
                "Нужны права администратора: файл системный. Запустите NetZapret от имени администратора.",
                ConsoleColor.Yellow);
        }
        catch (Exception ex)
        {
            Message($"Не вышло: {ex.GetBaseException().Message}", ConsoleColor.Red);
        }

        Pause();
    }

    /// <summary>
    /// Проверяет, отвечают ли прибитые адреса.
    /// </summary>
    /// <remarks>
    /// Ради этого редактор во многом и заводился. Прибитый мёртвый адрес
    /// перебивает и наш маршрут, и обычный DNS, а сам молчит — со стороны
    /// неотличимо от блокировки. Так нашёлся «умерший» ChatGPT: 670 имён
    /// на один переставший отвечать адрес.
    /// </remarks>
    private static async Task CheckPinsAsync(
        IReadOnlyList<HostsEntry> entries,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Проверяю адреса…");
        Console.WriteLine();

        var health = await HostsEditor.CheckAsync(entries, cancellationToken);
        var previous = Console.ForegroundColor;

        foreach (var pin in health)
        {
            Console.ForegroundColor = !pin.Tested ? ConsoleColor.DarkGray
                : pin.Alive ? ConsoleColor.DarkGray
                : ConsoleColor.Red;

            var state = !pin.Tested ? $"не проверить — {pin.Skipped}"
                : pin.Alive ? "отвечает"
                : "МОЛЧИТ";

            Console.WriteLine($"  {pin.Address,-40} {pin.Names,-6} {state}");
        }

        Console.ForegroundColor = previous;

        if (SupervisorState.Load(SupervisorState.DefaultPath) is { } state2 && state2.IsSupervisorAlive())
        {
            Console.WriteLine();
            Console.WriteLine("  Движки работают: часть адресов может отвечать через туннель,");
            Console.WriteLine("  а напрямую молчать. Для чистой картины остановите их.");
        }

        var dead = health.Where(h => h.Tested && !h.Alive).ToList();

        if (dead.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Молчат {dead.Count} {Plural(dead.Count, "адрес", "адреса", "адресов")}, " +
                $"на них {dead.Sum(d => d.Names)} {Plural(dead.Sum(d => d.Names), "имя", "имени", "имён")}.");
            Console.WriteLine("  Такая запись отменяет и наш маршрут, и обычный DNS, а сама никуда");
            Console.WriteLine("  не ведёт. Выключите её номером в списке.");
        }

        Pause();
    }

    private static void ShowNames(IReadOnlyList<(string Address, List<string> Names)> groups)
    {
        Console.Write("Номер адреса: ");

        if (!int.TryParse(Console.ReadLine()?.Trim(), out var index)
            || index < 1 || index > groups.Count)
        {
            return;
        }

        var (address, names) = groups[index - 1];

        Console.WriteLine();
        Console.WriteLine($"{address} — {names.Count} {Plural(names.Count, "имя", "имени", "имён")}");
        Console.WriteLine();

        foreach (var chunk in names.Chunk(3))
            Console.WriteLine("  " + string.Join("   ", chunk.Select(n => n.PadRight(28))));

        Pause();
    }

    /// <summary>
    /// Всё, к чему обращаются изредка.
    /// </summary>
    /// <remarks>
    /// Автозапуск, обновление и диагностика вынесены сюда не по важности,
    /// а по частоте: их трогают раз в месяц, и держать их в главном меню
    /// значило бы занимать три строки из десяти под редкое.
    /// </remarks>
    private static async Task<AppSettings> MoreMenuAsync(
        AppSettings settings,
        string settingsPath,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine("Ещё");
            Console.WriteLine(new string('=', 62));
            Console.WriteLine();
            Console.WriteLine($"  1. Автозапуск ............ {(AutostartTask.IsInstalled(AutostartTask.DefaultTaskName) ? "включён" : "выключен")}");
            Console.WriteLine($"  2. Обновление ............ у вас {UpdateCheck.Current}");
            Console.WriteLine($"  3. Журнал ................ {(settings.LogsEnabled ? "ведётся" : "выключен")}");
            Console.WriteLine();
            Console.WriteLine("  4. Обзор состояния");
            Console.WriteLine("  5. Журнал супервизора");
            Console.WriteLine("  6. Обслуживание .......... сброс, сеть, Defender, папка");
            Console.WriteLine();
            Console.WriteLine("  0. Назад");
            Console.WriteLine();
            Console.Write("Выбор: ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    ToggleAutostart(settings);
                    break;

                case "2":
                    await UpdateMenuAsync(cancellationToken);
                    break;

                case "3":
                    settings = settings with { LogsEnabled = !settings.LogsEnabled };
                    settings.Save(settingsPath);
                    Message(
                        settings.LogsEnabled
                            ? "Журнал будет вестись со следующего запуска."
                            : "Журнал выключен. Учтите два следствия: разбор сбоя сведётся "
                                + "к догадкам, и супервизор будет запускаться с окном — "
                                + "отпустить консоль он может только перенаправив вывод.",
                        settings.LogsEnabled ? ConsoleColor.Green : ConsoleColor.Yellow);
                    Pause();
                    break;

                case "4":
                    RunDoctor(settings);
                    break;

                case "5":
                    ShowSupervisorLog();
                    break;

                case "6":
                    settings = MaintenanceMenu(settings, settingsPath);
                    break;

                default:
                    return settings;
            }
        }
    }

    /// <summary>
    /// Проверка и установка обновления.
    /// </summary>
    /// <remarks>
    /// Проверка и установка разделены намеренно. Первая безобидна, вторая
    /// подменяет работающий обход, и делать её продолжением первой значило бы
    /// превратить любопытство в согласие.
    /// </remarks>
    private static async Task UpdateMenuAsync(CancellationToken cancellationToken)
    {
        ClearScreen();
        Console.WriteLine("Обновление");
        Console.WriteLine(new string('=', 62));
        Console.WriteLine();
        Console.WriteLine($"  У вас: {UpdateCheck.Current}");
        Console.WriteLine("  Спрашиваю GitHub…");

        var release = await UpdateCheck.LatestAsync(cancellationToken);

        if (release is null)
        {
            Message("Узнать не удалось: нет связи с GitHub или он недоступен.", ConsoleColor.Yellow);
            return;
        }

        if (!UpdateCheck.IsNewer(release.Version, UpdateCheck.Current))
        {
            Message($"У вас последняя версия ({UpdateCheck.Current}).", ConsoleColor.Green);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  Есть {release.Version}, вышла {release.Published?.ToLocalTime():dd.MM.yyyy}");
        Console.WriteLine($"  Размер: {release.ArchiveSize / 1024.0 / 1024.0:0.0} МБ");

        if (!string.IsNullOrWhiteSpace(release.Notes))
        {
            Console.WriteLine();

            foreach (var line in release.Notes.Split('\n').Take(12))
                Console.WriteLine($"  {Truncate(line.TrimEnd(), 74)}");
        }

        Console.WriteLine();
        Console.WriteLine("  Настройки, свои правила и подставленные адреса не перезаписываются.");
        Console.WriteLine("  После установки программа закроется и запустится заново.");
        Console.WriteLine();

        if (SupervisorState.Load(SupervisorState.DefaultPath) is { } state && state.IsSupervisorAlive())
        {
            Message(
                "Движки работают. Обновление их остановит — все соединения оборвутся.",
                ConsoleColor.Yellow);
        }

        Console.Write("Скачать и установить? [д/н]: ");

        var answer = Console.ReadLine()?.Trim();

        if (answer is null || !(answer.StartsWith('д') || answer.StartsWith('y')))
            return;

        await InstallAsync(release, cancellationToken);
    }

    private static async Task InstallAsync(ReleaseInfo release, CancellationToken cancellationToken)
    {
        Console.WriteLine();

        try
        {
            var progress = new Progress<double>(fraction =>
            {
                // В той же строке: сотня строк прогресса вытеснит из окна
                // всё, что человек читал секунду назад.
                Console.Write($"\r  Скачиваю… {fraction * 100:0}%   ");
            });

            var plan = await UpdateInstaller.StageAsync(release, progress, cancellationToken);

            Console.WriteLine($"\r  Скачано и распаковано: {plan.Files} файлов.   ");

            if (plan.Kept.Count > 0)
                Console.WriteLine($"  Сохранено как есть: {string.Join(", ", plan.Kept)}");

            var script = UpdateInstaller.WriteApplyScript(plan, Path.GetFullPath("."));

            Console.WriteLine();
            Console.WriteLine("  Готово к установке. Программа сейчас закроется, файлы заменятся");
            Console.WriteLine("  и она запустится снова.");
            Console.Write("  Enter — продолжить, иначе отмена: ");

            if (Console.ReadLine() is null)
                return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = script,
                Arguments = Environment.ProcessId.ToString(),
                UseShellExecute = true,
            });

            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Say($"Не вышло: {ex.GetBaseException().Message}", ConsoleColor.Red, pause: false);
            Console.WriteLine("Прежняя версия не тронута.");
            Pause();
        }
    }

    /// <summary>Разовые действия, которых иначе пришлось бы искать по инструкциям.</summary>
    private static AppSettings MaintenanceMenu(AppSettings settings, string settingsPath)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine("Обслуживание");
            Console.WriteLine(new string('=', 62));
            Console.WriteLine();
            Console.WriteLine("  1. Сбросить настройки до заводских");
            Console.WriteLine("  2. Сброс сети Windows .... winsock и TCP/IP, нужен перезапуск ПК");
            Console.WriteLine("  3. Папку в исключения Microsoft Defender");
            Console.WriteLine("  4. Перезапустить Discord . после смены маршрута голоса");
            Console.WriteLine($"  5. Предлагать перезапуск Discord ... {(settings.OfferDiscordRestart ? "да" : "нет")}");
            Console.WriteLine();
            Console.WriteLine("  6. Открыть папку программы");
            Console.WriteLine("  7. Документация");
            Console.WriteLine();
            Console.WriteLine("  0. Назад");
            Console.WriteLine();
            Console.Write("Выбор: ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    ResetSettings();
                    break;

                case "2":
                    Confirm(
                        "Сбросить сетевой стек Windows? Потребуется перезагрузка компьютера.",
                        Maintenance.ResetNetwork);
                    break;

                case "3":
                    Confirm(
                        "Вывести папку программы из-под проверки Defender? Он удаляет winws2.exe, "
                            + "опознавая в нём WinDivert. Защита остального компьютера не меняется.",
                        Maintenance.ExcludeFromDefender);
                    break;

                case "4":
                    Report(Maintenance.RestartDiscord());
                    break;

                case "5":
                    settings = settings with { OfferDiscordRestart = !settings.OfferDiscordRestart };
                    settings.Save(settingsPath);
                    break;

                case "6":
                    Report(Maintenance.OpenFolder());
                    break;

                case "7":
                    Report(Maintenance.OpenDocs());
                    break;

                default:
                    return settings;
            }
        }
    }

    private static void ResetSettings()
    {
        Console.WriteLine();
        Console.WriteLine("  Удалятся настройки, свои маршруты и рабочие файлы.");
        Console.Write("  Оставить ссылку подписки? [д/н]: ");

        var keep = Console.ReadLine()?.Trim();

        if (keep is null)
            return;

        Console.Write("  Точно сбросить? [д/н]: ");

        var confirm = Console.ReadLine()?.Trim();

        if (confirm is null || !(confirm.StartsWith('д') || confirm.StartsWith('y')))
            return;

        Report(Maintenance.ResetSettings(keep.StartsWith('д') || keep.StartsWith('y')));
    }

    private static void Confirm(string question, Func<ActionResult> action)
    {
        Console.WriteLine();

        foreach (var line in Wrap(question, 70))
            Console.WriteLine($"  {line}");

        Console.Write("  Продолжить? [д/н]: ");

        var answer = Console.ReadLine()?.Trim();

        if (answer is null || !(answer.StartsWith('д') || answer.StartsWith('y')))
            return;

        Report(action());
    }

    private static void Report(ActionResult result)
    {
        // Say, а не Message: второй сам делает паузу, и приписка про
        // перезагрузку оказалась бы после неё — то есть после того, как
        // человек уже нажал Enter и ушёл.
        Say(result.Message, result.Ok ? ConsoleColor.Green : ConsoleColor.Yellow, pause: false);

        if (result.NeedsReboot)
            Console.WriteLine("Изменения вступят в силу после перезагрузки компьютера.");

        Pause();
    }

    /// <summary>Разбивает длинную строку по словам.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();

        foreach (var word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
                line.Append(' ');

            line.Append(word);
        }

        if (line.Length > 0)
            yield return line.ToString();
    }

    /// <summary>Согласует существительное с числом.</summary>
    private static string Plural(int count, string one, string few, string many)
    {
        var tens = count % 100;

        if (tens is >= 11 and <= 14)
            return many;

        return (count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>
    /// Состояние самой программы: что найдено и что она делала.
    /// </summary>
    /// <remarks>
    /// Два пункта под одним, потому что спрашивают их вместе и по одному
    /// поводу: что-то работает не так, и надо понять, дошло ли дело до
    /// запуска движков вообще. Держать их в главном меню значило бы занять
    /// две строки из десяти под то, к чему обращаются раз в месяц.
    /// </remarks>
    private static void DiagnosticsMenu(AppSettings settings)
    {
        while (true)
        {
            ClearScreen();
            Console.WriteLine("Диагностика");
            Console.WriteLine(new string('=', 62));
            Console.WriteLine();
            Console.WriteLine("  1. Обзор состояния — что найдено и что готово к работе");
            Console.WriteLine("  2. Журнал супервизора — что происходило при запуске");
            Console.WriteLine();
            Console.WriteLine("  0. Назад");
            Console.WriteLine();
            Console.Write("Выбор: ");

            switch (Console.ReadLine()?.Trim())
            {
                case "1":
                    RunDoctor(settings);
                    break;

                case "2":
                    ShowSupervisorLog();
                    break;

                default:
                    return;
            }
        }
    }

    private static T? Pick<T>(string title, IReadOnlyList<(string Label, T Value)> items, T current)
        where T : struct
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));

        for (int i = 0; i < items.Count; i++)
        {
            bool active = EqualityComparer<T>.Default.Equals(items[i].Value, current);
            Console.WriteLine($"  {i + 1}. {items[i].Label}{(active ? "   <- сейчас" : string.Empty)}");
        }

        Console.WriteLine("  0. Отмена");
        Console.Write("Выбор: ");

        var input = Console.ReadLine()?.Trim();

        return int.TryParse(input, out var index) && index >= 1 && index <= items.Count
            ? items[index - 1].Value
            : null;
    }

    /// <returns>
    /// <c>Raw</c> — то, что человек набрал, если это не номер. Нужен вызывающему,
    /// чтобы принять свои команды буквами, не заводя ради них второй список.
    /// </returns>
    private static (bool Cancelled, string? Value, string? Raw) PickNullable(
        string title,
        IReadOnlyList<(string Label, string? Value)> items,
        string? current)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));

        for (int i = 0; i < items.Count; i++)
        {
            bool active = string.Equals(items[i].Value, current, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {i + 1}. {items[i].Label}{(active ? "   <- сейчас" : string.Empty)}");
        }

        Console.WriteLine("  0. Отмена");
        Console.Write("Выбор: ");

        var input = Console.ReadLine()?.Trim();

        return int.TryParse(input, out var index) && index >= 1 && index <= items.Count
            ? (false, items[index - 1].Value, null)
            : (true, current, input);
    }

    /// <summary>
    /// Сообщение с ожиданием Enter.
    /// </summary>
    /// <remarks>
    /// Пауза внутри — и это надо помнить: <c>Message</c> с последующим
    /// <c>Pause</c> спрашивает Enter дважды, а вывод после него оказывается
    /// уже за паузой, то есть человек его не увидит. Там, где после нужно
    /// ещё что-то напечатать, берётся <see cref="Say"/> с <c>pause: false</c>.
    /// </remarks>
    private static void Message(string text, ConsoleColor color) => Say(text, color, pause: true);

    private static void Say(string text, ConsoleColor color, bool pause)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine();
        Console.WriteLine(text);
        Console.ForegroundColor = previous;

        if (pause)
            Pause();
    }

    private static void Pause()
    {
        Console.WriteLine();
        Console.Write("Enter — назад в меню…");
        Console.ReadLine();
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
