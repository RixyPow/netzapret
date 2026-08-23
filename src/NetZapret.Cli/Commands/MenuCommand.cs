using System.Diagnostics;
using System.Security.Principal;
using NetZapret.Core;
using NetZapret.Core.Rules;
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
                return 0;

            settings = await HandleAsync(choice, settings, settingsPath, cancellationToken);
        }

        return 0;
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

    private static void Draw(AppSettings settings)
    {
        ClearScreen();
        Console.WriteLine("NetZapret");
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
        Console.WriteLine($"  3. Сервер VPN ............ {settings.DescribeServer()}");
        Console.WriteLine($"  4. Подписка .............. {DescribeSubscription(settings)}");
        Console.WriteLine($"  5. Автозапуск ............ {(AutostartTask.IsInstalled(AutostartTask.DefaultTaskName) ? "включён" : "выключен")}");
        Console.WriteLine();
        Console.WriteLine("  6. Собрать конфиг");
        Console.WriteLine(running ? "  7. Остановить" : "  7. Запустить");
        Console.WriteLine("  8. Проверить серверы");
        Console.WriteLine("  9. Обзор состояния");
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
                settings = await ChooseServerAsync(settings, cancellationToken);
                break;
            case "4":
                settings = AskSubscription(settings);
                break;
            case "5":
                ToggleAutostart(settings);
                return settings;
            case "6":
                await BuildConfigAsync(settings, cancellationToken);
                return settings;
            case "7":
                await ToggleRunAsync(settings, cancellationToken);
                return settings;
            case "8":
                await ProbeAsync(settings, cancellationToken);
                return settings;
            case "9":
                RunDoctor(settings);
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
                ("Выключено — весь трафик напрямую", OperatingMode.Off),
                ("Выборочно — десинк по умолчанию, VPN по правилам", OperatingMode.Selective),
                ("Всё через VPN", OperatingMode.ProxyAll),
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
            Message("Сначала задайте подписку (пункт 4).", ConsoleColor.Yellow);
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

        var items = new List<(string, string?)> { ("Авто — быстрейший из живых", null) };
        items.AddRange(servers.Select(s => ($"{s.Tag}  [{s.Protocol.ToString().ToLowerInvariant()}]", (string?)s.Tag)));

        var chosen = PickNullable("Сервер VPN", items, settings.PreferredServer);
        return chosen.Cancelled ? settings : settings with { PreferredServer = chosen.Value };
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

    private static async Task BuildConfigAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
        {
            Message("Сначала задайте подписку (пункт 4).", ConsoleColor.Yellow);
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Собираю конфиг…");

        try
        {
            var engine = RuleSetLoader.LoadFromFile(settings.RulesPath);

            // Режим из настроек перекрывает файл правил: меню не должно
            // переписывать YAML, который человек ведёт руками.
            var ruleSet = engine.RuleSet with { Operating = settings.Mode };

            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(settings.SubscriptionUrl), cancellationToken);

            var zapretRoot = ZapretPaths.Discover()?.Root;
            var capture = AddressListReader.Expand(ruleSet.CaptureEntries, zapretRoot, out var captureProblems);

            foreach (var problem in captureProblems)
                Console.WriteLine($"Внимание, секция capture — {problem}");

            var result = new SingBoxConfigCompiler().Compile(ruleSet, info.Servers, new SingBoxOptions
            {
                Scope = settings.ProxyOnly ? TunnelScope.ProxyOnly : TunnelScope.Everything,
                DnsServerAddresses = settings.ProxyOnly ? SystemResolvers.Discover() : Array.Empty<string>(),
                PreferredServerTag = settings.PreferredServer,
                CaptureAddresses = capture,
            });

            SingBoxConfigCompiler.WriteToFile(settings.ProxyConfigPath, result.Json);

            Message(
                $"Готово: {result.UsedServers.Count} серверов, {result.SkippedServers.Count} пропущено.",
                ConsoleColor.Green);
        }
        catch (Exception ex)
        {
            Message($"Не удалось: {ex.GetBaseException().Message}", ConsoleColor.Red);
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

        if (!File.Exists(settings.ProxyConfigPath))
        {
            Message("Конфиг не собран — сначала пункт 6.", ConsoleColor.Yellow);
            return;
        }

        // Супервизор запускается в отдельном окне: он работает в переднем плане
        // до Ctrl+C, и запуск его здесь заблокировал бы меню.
        var executable = Environment.ProcessPath;

        if (executable is null)
        {
            Message("Не удалось определить путь к программе.", ConsoleColor.Red);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executable,
            Arguments = BuildStartArguments(settings),
            WorkingDirectory = Directory.GetCurrentDirectory(),
            UseShellExecute = true,
        });

        // Даём супервизору записать состояние, иначе меню перерисуется
        // с надписью «остановлено» сразу после запуска.
        await Task.Delay(1500, cancellationToken);
        Message("Запущено в отдельном окне.", ConsoleColor.Green);
    }

    private static string BuildStartArguments(AppSettings settings)
    {
        var arguments = $"start --proxy-config \"{Path.GetFullPath(settings.ProxyConfigPath)}\"";

        if (settings.PresetName is { } preset)
            arguments += $" --preset \"{preset}\"";

        if (settings.VerifyTraffic)
            arguments += " --verify-traffic";

        return arguments;
    }

    private static async Task ProbeAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
        {
            Message("Сначала задайте подписку (пункт 4).", ConsoleColor.Yellow);
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

    private static void Message(string text, ConsoleColor color)
    {
        var previous = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine();
        Console.WriteLine(text);
        Console.ForegroundColor = previous;

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
