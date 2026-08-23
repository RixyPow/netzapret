using System.Diagnostics;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Команды управления супервизором: <c>start</c>, <c>stop</c>, <c>status</c>.
/// </summary>
internal static class SupervisorCommands
{
    public static async Task<int> StartAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var statePath = cmd.Value("state", SupervisorState.DefaultPath);

        // Холостой прогон проверяется до всего остального: он ничего не
        // запускает, поэтому уже работающий супервизор ему не помеха.
        if (cmd.Has("dry-run"))
        {
            Console.WriteLine("Холостой прогон: ничего не запускается.");
            PrintDryRun(cmd);
            return 0;
        }

        var existing = SupervisorState.Load(statePath);

        if (existing is not null && existing.IsSupervisorAlive())
        {
            Console.Error.WriteLine(
                $"Супервизор уже запущен (PID {existing.SupervisorProcessId}). " +
                "Остановите его командой stop.");
            return 2;
        }

        // Состояние от убитого процесса мешает: чистим, раз владелец мёртв.
        if (existing is not null)
            SupervisorState.Clear(statePath);

        var services = new List<SupervisedService>();

        if (!TryAddSingBox(cmd, services))
            return 2;

        AddWinws(cmd, services);

        if (services.Count == 0)
        {
            Console.Error.WriteLine("Нечего запускать.");
            return 2;
        }

        var supervisor = new ProcessSupervisor(services, new SupervisorOptions
        {
            StatePath = statePath,
            CheckInterval = TimeSpan.FromSeconds(cmd.Int("check-interval", 5)),
            MaxRestarts = cmd.Int("max-restarts", 5),
        })
        {
            OnEvent = Console.WriteLine,
        };

        Console.WriteLine($"Служб под присмотром: {services.Count} ({string.Join(", ", services.Select(s => s.Name))})");
        Console.WriteLine($"Логи служб: {Path.GetFullPath("runtime")}");
        Console.WriteLine("Ctrl+C — остановить всё и выйти.");
        Console.WriteLine();

        await supervisor.RunAsync(cancellationToken);
        return 0;
    }

    private static bool TryAddSingBox(CommandLine cmd, List<SupervisedService> services)
    {
        if (cmd.Has("no-proxy"))
            return true;

        var singBox = EngineLocator.FindSingBox();

        if (singBox is null)
        {
            Console.Error.WriteLine("sing-box.exe не найден в tools/. Запустите с --no-proxy, если он не нужен.");
            return false;
        }

        var configPath = cmd.Value("proxy-config", Path.Combine("runtime", "singbox.json"));

        if (!File.Exists(configPath))
        {
            Console.Error.WriteLine(
                $"Конфиг прокси не найден: {configPath}. " +
                "Соберите его командой config или укажите --proxy-config.");
            return false;
        }

        int? trafficPort = cmd.Has("verify-traffic") ? cmd.Int("local-port", 21080) : null;

        services.Add(new SingBoxService(
            singBox,
            configPath,
            cmd.Int("clash-port", 9090),
            trafficPort,
            cmd.Int("traffic-check-every", 6))
        {
            OutputLogPath = Path.Combine("runtime", "sing-box.log"),
        });

        return true;
    }

    private static void AddWinws(CommandLine cmd, List<SupervisedService> services)
    {
        if (!cmd.Has("preset"))
            return;

        var paths = ZapretPaths.Discover(cmd.Value("zapret-root"));

        if (paths is null)
        {
            Console.Error.WriteLine("Установка Zapret не найдена, десинк не будет запущен.");
            return;
        }

        var presetName = cmd.Value("preset")!;
        var presetPath = Directory
            .EnumerateFiles(paths.PresetDirectory, "*.txt")
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                .Contains(presetName, StringComparison.OrdinalIgnoreCase));

        if (presetPath is null)
        {
            Console.Error.WriteLine($"Пресет '{presetName}' не найден, десинк не будет запущен.");
            return;
        }

        var preset = new PresetReader().Load(presetPath);
        var arguments = WinwsCommandLine.Build(preset);

        var missing = WinwsCommandLine.FindMissingFiles(preset, paths.Root);
        if (missing.Count > 0)
        {
            Console.WriteLine(
                $"Предупреждение: пресет ссылается на {missing.Count} отсутствующих файлов, " +
                $"например {missing[0]}. Соответствующие секции ничего не сопоставят.");
        }

        Console.WriteLine($"Пресет: {preset.Name} ({arguments.Count} аргументов)");
        Console.WriteLine($"Рабочий каталог: {paths.Root}");
        Console.WriteLine($"Движок: {paths.ExecutablePath}");
        Console.WriteLine(
            $"Списки: {preset.Sections.Sum(s => s.HostListPaths.Count)} hostlist, " +
            $"{preset.Sections.Sum(s => s.IpSetPaths.Count)} ipset — читаются winws2 из вашей установки как есть");
        Console.WriteLine(
            "ВНИМАНИЕ: запуск winws2 требует прав администратора (драйвер WinDivert). " +
            "Без них служба не поднимется — это ожидаемо.");

        services.Add(new WinwsService(paths.ExecutablePath, arguments, paths.Root)
        {
            OutputLogPath = Path.Combine("runtime", "winws2.log"),
        });
    }

    /// <summary>
    /// Показывает командную строку winws2 без запуска.
    /// </summary>
    /// <remarks>
    /// Отвечает на вопрос «что именно уедет движку»: аргументы передаются
    /// как есть, а пути к спискам остаются относительными, потому что winws2
    /// резолвит их от своего рабочего каталога.
    /// </remarks>
    private static void PrintDryRun(CommandLine cmd)
    {
        if (!cmd.Has("preset"))
            return;

        var paths = ZapretPaths.Discover(cmd.Value("zapret-root"));
        if (paths is null)
            return;

        var presetPath = Directory
            .EnumerateFiles(paths.PresetDirectory, "*.txt")
            .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                .Contains(cmd.Value("preset")!, StringComparison.OrdinalIgnoreCase));

        if (presetPath is null)
            return;

        var preset = new PresetReader().Load(presetPath);
        var arguments = WinwsCommandLine.Build(preset);

        Console.WriteLine();
        Console.WriteLine("Командная строка winws2 (первые 25 аргументов):");

        foreach (var argument in arguments.Take(25))
            Console.WriteLine($"  {argument}");

        Console.WriteLine($"  … и ещё {Math.Max(0, arguments.Count - 25)}");
    }

    public static int Status(CommandLine cmd)
    {
        var statePath = cmd.Value("state", SupervisorState.DefaultPath);
        var state = SupervisorState.Load(statePath);

        if (state is null)
        {
            Console.WriteLine("Супервизор не запущен.");
            Console.WriteLine("Запуск: netzapret start --proxy-config runtime/singbox.json");
            return 1;
        }

        if (!state.IsSupervisorAlive())
        {
            Console.WriteLine($"Супервизор (PID {state.SupervisorProcessId}) мёртв, состояние устарело.");
            Console.WriteLine($"Обновлено: {state.UpdatedAt.LocalDateTime:dd.MM.yyyy HH:mm:ss}");
            return 1;
        }

        var uptime = DateTimeOffset.Now - state.StartedAt;

        Console.WriteLine($"Супервизор:  PID {state.SupervisorProcessId}, работает {FormatUptime(uptime)}");
        Console.WriteLine($"Обновлено:   {state.UpdatedAt.LocalDateTime:HH:mm:ss}");
        Console.WriteLine();
        Console.WriteLine($"  {"СЛУЖБА",-12} {"СОСТОЯНИЕ",-12} {"PID",-8} {"РЕСТАРТОВ",-10} ПРИМЕЧАНИЕ");
        Console.WriteLine("  " + new string('-', 72));

        foreach (var service in state.Services)
        {
            var previous = Console.ForegroundColor;
            Console.ForegroundColor = service.Health switch
            {
                ServiceHealth.Healthy => ConsoleColor.Green,
                ServiceHealth.Degraded => ConsoleColor.Yellow,
                ServiceHealth.Dead or ServiceHealth.Faulted => ConsoleColor.Red,
                _ => previous,
            };

            Console.WriteLine(
                $"  {service.Name,-12} {Describe(service.Health),-12} {service.ProcessId?.ToString() ?? "-",-8} " +
                $"{service.RestartCount,-10} {service.LastError ?? string.Empty}");

            Console.ForegroundColor = previous;
        }

        bool allHealthy = state.Services.All(s => s.Health == ServiceHealth.Healthy);
        return allHealthy ? 0 : 1;
    }

    public static async Task<int> StopAsync(CommandLine cmd, CancellationToken cancellationToken)
    {
        var statePath = cmd.Value("state", SupervisorState.DefaultPath);
        var state = SupervisorState.Load(statePath);

        if (state is null)
        {
            Console.WriteLine("Супервизор не запущен.");
            return 0;
        }

        if (!state.IsSupervisorAlive())
        {
            Console.WriteLine("Супервизор уже мёртв, убираю устаревшее состояние.");
            SupervisorState.Clear(statePath);
            return 0;
        }

        try
        {
            using var process = Process.GetProcessById(state.SupervisorProcessId);

            // Убиваем всё дерево: супервизор при жёстком завершении не успеет
            // остановить дочерние движки, и те останутся висеть.
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);

            Console.WriteLine($"Супервизор (PID {state.SupervisorProcessId}) остановлен вместе со службами.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Не удалось остановить: {ex.GetBaseException().Message}");
            return 1;
        }
        finally
        {
            SupervisorState.Clear(statePath);
        }

        return 0;
    }

    private static string Describe(ServiceHealth health) => health switch
    {
        ServiceHealth.Healthy => "работает",
        ServiceHealth.Degraded => "сбоит",
        ServiceHealth.Dead => "упала",
        ServiceHealth.Faulted => "сдалась",
        _ => "остановлена",
    };

    private static string FormatUptime(TimeSpan uptime) =>
        uptime.TotalHours >= 1
            ? $"{(int)uptime.TotalHours} ч {uptime.Minutes} мин"
            : uptime.TotalMinutes >= 1
                ? $"{(int)uptime.TotalMinutes} мин"
                : $"{(int)uptime.TotalSeconds} с";
}
