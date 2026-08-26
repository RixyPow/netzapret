using System.Diagnostics;
using System.Security.Principal;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
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

        while (!cancellationToken.IsCancellationRequested)
        {
            Draw(settings);

            Console.Write("Выбор: ");
            var choice = Console.ReadLine()?.Trim();

            if (choice is null or "0" or "q" or "Q")
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

        switch (Console.ReadLine()?.Trim())
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
        Console.WriteLine($"  4. Автозапуск ............ {(AutostartTask.IsInstalled(AutostartTask.DefaultTaskName) ? "включён" : "выключен")}");
        Console.WriteLine($"  5. Сервисы и маршруты .... {DescribeRoutes()}");
        Console.WriteLine();
        // Отдельного «собрать конфиг» здесь нет намеренно: он собирается при
        // каждом запуске, а сам по себе ничего не применяет — действует только
        // перезапуск. Пункт предлагал мнимое действие и создавал впечатление
        // обязательного шага, который на деле выполняется сам.
        Console.WriteLine(running ? "  6. Остановить" : "  6. Запустить");
        Console.WriteLine("  7. Обзор состояния");
        Console.WriteLine("  8. Журнал супервизора");
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
                ToggleAutostart(settings);
                return settings;
            case "5":
                EditServices(settings);
                return settings;
            case "6":
                await ToggleRunAsync(settings, cancellationToken);
                return settings;
            case "7":
                RunDoctor(settings);
                return settings;
            case "8":
                ShowSupervisorLog();
                return settings;
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

        var presets = new PresetReader().List(paths.PresetDirectory);
        var items = new List<(string, string?)> { ("Не запускать десинк", null) };
        items.AddRange(presets.Select(p => ($"{p.Name}  ({p.ActiveSections.Count()} секций)", (string?)p.Name)));

        var chosen = PickNullable("Пресет Zapret", items, settings.PresetName);
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
            Console.WriteLine("  1. Выбрать сервер");
            Console.WriteLine("  2. Проверить серверы");
            Console.WriteLine("  3. Изменить ссылку подписки");
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
    private static void EditServices(AppSettings settings)
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

                Console.WriteLine(
                    $"  {i + 1}. {p.Part.Name,-24} {p.DescribeMode(),-10} {p.DomainCount,3} дом.{mark}");

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

                ChoosePartMode(parts[index - 1]);
            }
        }
    }

    /// <summary>
    /// Куда направить часть сервиса.
    /// </summary>
    private static void ChoosePartMode(ServiceRouting.PartStatus part)
    {
        Console.WriteLine();
        Console.WriteLine($"  {part.Part.Name}: сейчас {part.DescribeMode()}");
        Console.WriteLine($"  Список: {part.Part.List} ({part.DomainCount} доменов, например {part.Example})");
        Console.WriteLine();
        Console.WriteLine("  1. Напрямую");
        Console.WriteLine("  2. Десинк");
        Console.WriteLine("  3. VPN");
        Console.WriteLine("  4. Убрать свой выбор — вернуть как было");
        Console.WriteLine("  0. Оставить как есть");
        Console.Write("Выбор: ");

        var choice = Console.ReadLine()?.Trim();
        var file = UserRulesFile.Load();

        switch (choice)
        {
            case "1":
                file.Set(MatchKind.HostList, part.Part.List, RoutingMode.Direct);
                break;
            case "2":
                file.Set(MatchKind.HostList, part.Part.List, RoutingMode.Desync);
                break;
            case "3":
                file.Set(MatchKind.HostList, part.Part.List, RoutingMode.Proxy);
                break;
            case "4":
                file.Remove(MatchKind.HostList, part.Part.List);
                break;
            default:
                return;
        }

        file.Save();
        Message("Записано. Применится при следующем запуске.", ConsoleColor.Green);
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
            Console.WriteLine("  Пусто — назад.");
            Console.WriteLine();
            Console.Write("> ");

            var input = Console.ReadLine()?.Trim();

            if (string.IsNullOrWhiteSpace(input))
                return;

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
                Console.WriteLine($"Внимание, hosts: {note}");

            var result = new SingBoxConfigCompiler().Compile(ruleSet, info.Servers, new SingBoxOptions
            {
                Scope = settings.ProxyOnly ? TunnelScope.ProxyOnly : TunnelScope.Everything,
                DnsServerAddresses = settings.ProxyOnly ? SystemResolvers.Discover() : Array.Empty<string>(),
                DnsServer = settings.DnsServer,
                DnsThroughTunnel = settings.DnsThroughTunnel,
                PreferredServerTag = settings.PreferredServer,
                CaptureAddresses = capture,
                PinnedProxyAddresses = pinned,
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
        var arguments = $"start --log \"{Path.GetFullPath(SupervisorLogPath)}\"";

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
            Pause();
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

    private static (bool Cancelled, string? Value) PickNullable(
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
            ? (false, items[index - 1].Value)
            : (true, current);
    }

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
