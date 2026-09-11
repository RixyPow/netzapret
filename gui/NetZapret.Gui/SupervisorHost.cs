using System.Diagnostics;
using System.IO;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui;

/// <summary>
/// Супервизор внутри самого окна.
/// </summary>
/// <remarks>
/// <para>
/// Окно поднимает движки, перезапуская само себя с ключом
/// <see cref="Switch"/>, а не отдельной консольной программой. Отдельный
/// процесс всё равно нужен — супервизор обязан пережить закрытие окна, —
/// меняется лишь то, чей это процесс. Взамен в дистрибутиве остаётся один
/// исполняемый файл: два файла рядом, из которых правильный для двойного
/// щелчка менее очевиден, — выбор, который никто не должен делать.
/// </para>
/// <para>
/// Логика повторена, а не взята из <c>NetZapret.Cli</c>: и запуск служб,
/// и поиск движка там <c>internal</c>, а библиотеки под ними у окна те же.
/// </para>
/// </remarks>
internal static class SupervisorHost
{
    /// <summary>Ключ, по которому окно узнаёт себя в роли супервизора.</summary>
    public const string Switch = "--supervisor";

    /// <summary>
    /// Ключ остановки без окна.
    /// </summary>
    /// <remarks>
    /// Нужен скрипту удаления: тот обязан погасить движки перед тем, как
    /// стирать папку, а открывать ради этого окно и просить человека нажать
    /// кнопку — не то, чего ждут от удаления.
    /// </remarks>
    public const string StopSwitch = "--stop";

    private sealed record Options
    {
        public bool NoProxy { get; init; }

        public string ProxyConfig { get; init; } = Path.Combine("runtime", "singbox.json");

        public string? Preset { get; init; }

        public bool VerifyTraffic { get; init; }

        public string? LogPath { get; init; }
    }

    /// <summary>
    /// Собирает командную строку для себя же.
    /// </summary>
    /// <remarks>
    /// Имена ключей те же, что у консоли, намеренно: журнал супервизора
    /// читают рядом с консольным, и расхождение в словах стоило бы лишнего
    /// вопроса при первом же разборе.
    /// </remarks>
    public static string BuildArguments(AppSettings settings)
    {
        var arguments = Switch;

        if (settings.LogsEnabled)
            arguments += $" --log \"{Path.GetFullPath(Path.Combine("runtime", "supervisor.log"))}\"";

        arguments += settings.NeedsProxy
            ? $" --proxy-config \"{Path.GetFullPath(settings.ProxyConfigPath)}\""
            : " --no-proxy";

        if (settings.NeedsDesync)
            arguments += $" --preset \"{settings.PresetName}\"";

        if (settings.VerifyTraffic)
            arguments += " --verify-traffic";

        return arguments;
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var options = Parse(args);

        // Весь вывод уходит в файл: у окна консоли нет, и писать ему некуда.
        // Раздел «Журнал» показывает этот файл отдельным пунктом.
        SharedLogWriter? log = null;

        if (!string.IsNullOrWhiteSpace(options.LogPath))
        {
            log = SharedLogWriter.TryOpen(options.LogPath);

            if (log is not null)
            {
                Console.SetOut(log);
                Console.SetError(log);
            }
        }

        try
        {
            var statePath = SupervisorState.DefaultPath;
            var existing = SupervisorState.Load(statePath);

            if (existing is not null && existing.IsSupervisorAlive())
            {
                Console.Error.WriteLine(
                    $"Супервизор уже запущен (PID {existing.SupervisorProcessId}).");

                return 2;
            }

            // Состояние от убитого процесса мешает: чистим, раз владелец мёртв.
            if (existing is not null)
                SupervisorState.Clear(statePath);

            var services = new List<SupervisedService>();

            if (!options.NoProxy && !TryAddSingBox(options, services))
                return 2;

            AddWinws(options, services);

            if (services.Count == 0)
            {
                Console.Error.WriteLine("Нечего запускать.");
                return 2;
            }

            // Осиротевшие движки от прошлых запусков ломают старт неочевидно:
            // старый sing-box держит TUN-адаптер, старый winws2 — WinDivert.
            // Сами их не убиваем: однажды именно это и гасило исправный
            // sing-box, поэтому решение остаётся за человеком.
            var orphans = ProcessSupervisor.FindOrphans(services);

            if (orphans.Count > 0)
            {
                Console.Error.WriteLine($"Найдены движки от прошлого запуска: {orphans.Count}");

                foreach (var orphan in orphans)
                {
                    Console.Error.WriteLine($"  {orphan.ProcessName} (PID {orphan.Id})");
                    orphan.Dispose();
                }

                Console.Error.WriteLine(
                    "Они удержат TUN-адаптер и WinDivert, и новые движки не поднимутся. " +
                    "Остановите их и повторите.");

                return 2;
            }

            var supervisor = new ProcessSupervisor(services, new SupervisorOptions
            {
                StatePath = statePath,
            })
            {
                OnEvent = Console.WriteLine,
            };

            Console.WriteLine(
                $"Служб под присмотром: {services.Count} " +
                $"({string.Join(", ", services.Select(s => s.Name))})");

            await supervisor.RunAsync(cancellationToken);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Супервизор сорвался: " + ex);
            return 1;
        }
        finally
        {
            log?.Dispose();
        }
    }

    /// <summary>
    /// Гасит супервизор вместе со службами.
    /// </summary>
    /// <remarks>
    /// По файлу состояния, а не по имени процесса: имя теперь то же самое,
    /// что у окна, и поиск по нему закрыл бы и само окно.
    /// </remarks>
    public static async Task<string> StopAsync(CancellationToken cancellationToken)
    {
        var statePath = SupervisorState.DefaultPath;
        var state = SupervisorState.Load(statePath);

        if (state is null || !state.IsSupervisorAlive())
        {
            SupervisorState.Clear(statePath);
            return "Супервизор не запущен.";
        }

        try
        {
            using var process = Process.GetProcessById(state.SupervisorProcessId);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception)
        {
            // Мог завершиться сам между проверкой и вызовом.
        }

        SupervisorState.Clear(statePath);
        return "Остановлено.";
    }

    private static bool TryAddSingBox(Options options, List<SupervisedService> services)
    {
        var singBox = Engines.FindSingBox();

        if (singBox is null)
        {
            Console.Error.WriteLine("sing-box.exe не найден в engines/.");
            return false;
        }

        if (!File.Exists(options.ProxyConfig))
        {
            Console.Error.WriteLine($"Конфиг прокси не найден: {options.ProxyConfig}");
            return false;
        }

        // Порт берётся оттуда же, откуда его берёт компилятор конфига:
        // разъехавшись, эти два значения дают вечно проваливающуюся проверку,
        // а выглядит она как неисправный движок.
        int? trafficPort = options.VerifyTraffic ? SingBoxOptions.DefaultHealthPort : null;

        services.Add(new SingBoxService(singBox, options.ProxyConfig, 9090, trafficPort)
        {
            OutputLogPath = Path.Combine("runtime", "sing-box.log"),
        });

        return true;
    }

    private static void AddWinws(Options options, List<SupervisedService> services)
    {
        if (options.Preset is null)
            return;

        var paths = ZapretPaths.Discover();

        if (paths is null)
        {
            Console.Error.WriteLine("Установка Zapret не найдена, десинк не будет запущен.");
            return;
        }

        var presetPath = ZapretPaths.FindPreset(options.Preset);

        if (presetPath is null)
        {
            Console.Error.WriteLine($"Пресет «{options.Preset}» не найден, десинк не будет запущен.");
            return;
        }

        var preset = new PresetReader().Load(presetPath);

        // Список исключений пишется при сборке конфига; если его нет —
        // исключать нечего, и командная строка остаётся нетронутой.
        var exclude = File.Exists(WinwsCommandLine.DefaultExcludeListPath)
            ? Path.GetFullPath(WinwsCommandLine.DefaultExcludeListPath)
            : null;

        // Свои профили: имена, которым рецепт выбран руками в «Маршрутах».
        // Списки под них пишет сборка конфига — здесь их только подбирают,
        // потому что правила читаются там, а запуск живёт тут.
        var own = OwnDesyncLists.Read();

        var arguments = WinwsCommandLine.Build(preset, exclude, own);

        if (exclude is not null)
            Console.WriteLine($"Десинк не трогает прибитые имена: {exclude}");

        foreach (var profile in own)
            Console.WriteLine($"Свой рецепт «{profile.Name}»: {profile.HostListPath}");

        var missing = WinwsCommandLine.FindMissingFiles(preset, paths.Root);

        if (missing.Count > 0)
        {
            Console.WriteLine(
                $"Предупреждение: пресет ссылается на {missing.Count} отсутствующих файлов, " +
                $"например {missing[0]}. Соответствующие секции ничего не сопоставят.");
        }

        Console.WriteLine($"Пресет: {preset.Name} ({arguments.Count} аргументов)");

        services.Add(new WinwsService(paths.ExecutablePath, arguments, paths.Root)
        {
            OutputLogPath = Path.Combine("runtime", "winws2.log"),
        });
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();

        for (int i = 0; i < args.Length; i++)
        {
            string? next = i + 1 < args.Length ? args[i + 1] : null;

            switch (args[i])
            {
                case "--no-proxy":
                    options = options with { NoProxy = true };
                    break;

                case "--verify-traffic":
                    options = options with { VerifyTraffic = true };
                    break;

                case "--proxy-config" when next is not null:
                    options = options with { ProxyConfig = next };
                    i++;
                    break;

                case "--preset" when next is not null:
                    options = options with { Preset = next };
                    i++;
                    break;

                case "--log" when next is not null:
                    options = options with { LogPath = next };
                    i++;
                    break;
            }
        }

        return options;
    }
}
