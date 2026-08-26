using System.Security.Principal;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Обзор состояния системы одной командой.
/// </summary>
/// <remarks>
/// Отвечает на вопрос «что вообще готово к работе» без запуска чего-либо.
/// Ничего не меняет и прав администратора не требует.
/// </remarks>
internal static class DoctorCommand
{
    public static int Run(CommandLine cmd)
    {
        bool allGood = true;

        Console.WriteLine("NetZapret — обзор состояния");
        Console.WriteLine(new string('=', 60));
        Console.WriteLine();

        allGood &= Section("Права", CheckElevation());
        allGood &= Section("Движки", CheckEngines());
        allGood &= Section("Zapret", CheckZapret(cmd));
        allGood &= Section("Правила", CheckRules(cmd));
        allGood &= Section("Конфиг прокси", CheckProxyConfig(cmd));
        allGood &= Section("DNS", CheckDns(cmd));
        allGood &= Section("Супервизор", CheckSupervisor(cmd));

        Console.WriteLine();
        Console.WriteLine(allGood
            ? "Всё необходимое на месте."
            : "Часть компонентов не готова — подробности выше.");

        return allGood ? 0 : 1;
    }

    private static bool Section(string title, IReadOnlyList<Check> checks)
    {
        Console.WriteLine($"{title}:");

        bool ok = true;

        foreach (var check in checks)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = check.Level switch
            {
                Level.Ok => ConsoleColor.Green,
                Level.Warning => ConsoleColor.Yellow,
                Level.Problem => ConsoleColor.Red,
                _ => previous,
            };

            var mark = check.Level switch
            {
                Level.Ok => "  +",
                Level.Warning => "  ~",
                Level.Problem => "  -",
                _ => "   ",
            };

            Console.Write(mark);
            Console.ForegroundColor = previous;
            Console.WriteLine($" {check.Text}");

            if (check.Level == Level.Problem)
                ok = false;
        }

        Console.WriteLine();
        return ok;
    }

    private static IReadOnlyList<Check> CheckElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);

        return
        [
            elevated
                ? new Check(Level.Ok, "администратор: доступны TUN, winws2 и наблюдение")
                : new Check(Level.Warning,
                    "обычные права: доступны probe, config, preset, sub. " +
                    "Для TUN, winws2 и watch нужна консоль администратора"),
        ];
    }

    private static IReadOnlyList<Check> CheckEngines()
    {
        var checks = new List<Check>();

        var singBox = EngineLocator.FindSingBox();
        checks.Add(singBox is null
            ? new Check(Level.Problem, "sing-box.exe не найден в tools/")
            : new Check(Level.Ok, $"sing-box: {Shorten(singBox)}"));

        var xray = EngineLocator.FindXray();
        checks.Add(xray is null
            ? new Check(Level.Warning, "xray.exe не найден — серверы с транспортом xhttp будут недоступны")
            : new Check(Level.Ok, $"xray: {Shorten(xray)}"));

        if (singBox is not null)
        {
            var wintun = Path.Combine(Path.GetDirectoryName(singBox)!, "wintun.dll");
            checks.Add(File.Exists(wintun)
                ? new Check(Level.Ok, "wintun.dll на месте — TUN сможет подняться")
                : new Check(Level.Problem, "wintun.dll отсутствует рядом с sing-box: TUN не поднимется"));
        }

        return checks;
    }

    private static IReadOnlyList<Check> CheckZapret(CommandLine cmd)
    {
        var paths = ZapretPaths.Discover(cmd.Value("zapret-root"));

        if (paths is null)
            return [new Check(Level.Warning, "установка Zapret не найдена — десинк недоступен")];

        var checks = new List<Check> { new(Level.Ok, $"установка: {paths.Root}") };

        checks.Add(File.Exists(paths.ExecutablePath)
            ? new Check(Level.Ok, "winws2.exe на месте")
            : new Check(Level.Warning, $"winws2.exe не найден: {paths.ExecutablePath}"));

        try
        {
            var presets = new PresetReader().List(paths.PresetDirectory);
            checks.Add(presets.Count > 0
                ? new Check(Level.Ok, $"пресетов: {presets.Count}")
                : new Check(Level.Warning, "пресеты не найдены"));
        }
        catch (Exception ex)
        {
            checks.Add(new Check(Level.Warning, $"пресеты не читаются: {ex.GetBaseException().Message}"));
        }

        return checks;
    }

    private static IReadOnlyList<Check> CheckRules(CommandLine cmd)
    {
        var path = cmd.Value("config", "config/rules.yaml");

        if (!File.Exists(path))
        {
            return
            [
                new Check(Level.Warning,
                    $"{path} не найден — возьмите за основу config/rules.example.yaml"),
            ];
        }

        try
        {
            var engine = RuleSetLoader.LoadFromFile(path);

            return
            [
                new Check(Level.Ok,
                    $"{path}: режим {engine.RuleSet.Operating.ToString().ToLowerInvariant()}, " +
                    $"правил {engine.RuleSet.Rules.Count}"),
            ];
        }
        catch (RuleConfigurationException ex)
        {
            return [new Check(Level.Problem, $"{path}: {ex.Message}")];
        }
    }

    private static IReadOnlyList<Check> CheckProxyConfig(CommandLine cmd)
    {
        var path = cmd.Value("proxy-config", Path.Combine("runtime", "singbox.json"));

        if (!File.Exists(path))
        {
            return
            [
                new Check(Level.Warning,
                    $"{path} не собран — выполните netzapret config --sub <ссылка>"),
            ];
        }

        var age = DateTime.Now - File.GetLastWriteTime(path);

        var checks = new List<Check>
        {
            new(Level.Ok, $"{path}, собран {FormatAge(age)} назад"),
        };

        // Кто собрал, а не когда. Развёрнутых копий бывает несколько, и старая
        // молча пересобирает конфиг по-своему: файл свежий, а полей, которых
        // та версия не знает, в нём нет. По времени это не отличить никак,
        // и трижды за день мы искали неисправность не там.
        var built = SingBoxConfigCompiler.ReadStamp(path);
        var running = typeof(DoctorCommand).Assembly.GetName().Version?.ToString();

        if (built is null)
        {
            checks.Add(new Check(Level.Warning,
                "собран версией до появления отметки — пересоберите на всякий случай"));
        }
        else if (running is not null && built != running)
        {
            checks.Add(new Check(Level.Problem,
                $"собран версией {built}, а работает {running} — " +
                "запущена не та копия, что собирала конфиг"));
        }
        else
        {
            checks.Add(new Check(Level.Ok, $"собран этой же версией ({built})"));
        }

        return checks;
    }

    /// <summary>
    /// Достижим ли апстрим-резолвер.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Проверка появилась после того, как российские операторы начали
    /// перекрывать DoH и DoT. Отказ апстрима выглядит как «перестало
    /// открываться вообще всё»: имена не разрешаются, и на маршруты это
    /// не походит ничем. Лучше назвать причину прямо.
    /// </para>
    /// <para>
    /// Проверяется TCP на 443, а не запрос: настоящий запрос требовал бы
    /// разбора DoH, а закрывают обычно само соединение.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<Check> CheckDns(CommandLine cmd)
    {
        var settings = AppSettings.Load(cmd.Value("settings", AppSettings.DefaultPath));
        var server = settings.DnsServer;

        if (settings.DnsThroughTunnel)
        {
            return
            [
                new Check(Level.Ok,
                    $"{server} через туннель — оператор такой запрос не видит"),
            ];
        }

        var checks = new List<Check>();

        try
        {
            using var client = new System.Net.Sockets.TcpClient();

            if (client.ConnectAsync(server, 443).Wait(TimeSpan.FromSeconds(4)))
            {
                checks.Add(new Check(Level.Ok, $"{server} отвечает по 443 (DoH напрямую)"));
            }
            else
            {
                checks.Add(new Check(Level.Problem,
                    $"{server} не отвечает по 443 — возможно, оператор перекрыл DoH"));
                checks.Add(new Check(Level.Warning,
                    "лечится так: DnsThroughTunnel = true в config/netzapret.json, " +
                    "тогда имена будут разрешаться внутри туннеля"));
            }
        }
        catch (Exception ex)
        {
            checks.Add(new Check(Level.Problem, $"{server}: {ex.GetBaseException().Message}"));
        }

        return checks;
    }

    private static IReadOnlyList<Check> CheckSupervisor(CommandLine cmd)
    {
        var state = SupervisorState.Load(cmd.Value("state", SupervisorState.DefaultPath));

        if (state is null)
            return [new Check(Level.Warning, "не запущен")];

        if (!state.IsSupervisorAlive())
            return [new Check(Level.Warning, $"состояние от мёртвого процесса {state.SupervisorProcessId}")];

        var checks = new List<Check> { new(Level.Ok, $"работает, PID {state.SupervisorProcessId}") };

        foreach (var service in state.Services)
        {
            var level = service.Health switch
            {
                ServiceHealth.Healthy => Level.Ok,
                ServiceHealth.Degraded => Level.Warning,
                _ => Level.Problem,
            };

            checks.Add(new Check(level,
                $"{service.Name}: {service.Health.ToString().ToLowerInvariant()}" +
                (service.RestartCount > 0 ? $", рестартов {service.RestartCount}" : string.Empty)));
        }

        return checks;
    }

    private static string Shorten(string path)
    {
        var directory = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        return Path.Combine(directory, Path.GetFileName(path));
    }

    private static string FormatAge(TimeSpan age) =>
        age.TotalDays >= 1 ? $"{(int)age.TotalDays} дн."
        : age.TotalHours >= 1 ? $"{(int)age.TotalHours} ч."
        : $"{(int)age.TotalMinutes} мин.";

    private enum Level
    {
        Ok,
        Warning,
        Problem,
    }

    private readonly record struct Check(Level Level, string Text);
}
