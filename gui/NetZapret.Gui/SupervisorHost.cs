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
/// Консольная копия этой логики ушла вместе с консолью 23.09; эта —
/// единственная.
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

    /// <summary>
    /// Разобранная командная строка супервизора.
    /// </summary>
    /// <remarks>
    /// Видна тестам, а не наружу. Между <see cref="BuildArguments"/>
    /// и <see cref="Parse"/> лежит шов, где строку расщепляет система,
    /// и проверять надо ровно то, что этот шов переживает.
    /// </remarks>
    internal sealed record Options
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

            // Живой супервизор — не повод отказаться, а повод его сменить.
            //
            // Прежде здесь стоял отказ с кодом 2. У консоли это читалось:
            // человек видел строку и понимал, что делать. У окна консоли нет,
            // сообщение уходило в журнал, и снаружи выглядело так, будто
            // «Запустить» не работает вовсе — та самая жалоба про кнопку.
            //
            // Снимаем именно СУПЕРВИЗОР, а не его движки. Он погасит их сам,
            // своим же порядком: движки включены в объект задания и уходят
            // вместе с ним. Убивать чужие движки напрямую мы однажды уже
            // пробовали, и это гасило исправный sing-box — см. ниже.
            if (existing is not null && existing.IsSupervisorAlive())
            {
                Console.WriteLine(
                    $"Перенимаю у супервизора PID {existing.SupervisorProcessId}: "
                    + "двум сразу нельзя, они дерутся за TUN и WinDivert.");

                await StopAsync(CancellationToken.None);

                // Ждём, пока он вправду уйдёт: стартовать поверх умирающего
                // значит получить обоих сразу, чего мы и избегаем.
                for (int i = 0; i < 20 && SupervisorState.Load(statePath)?.IsSupervisorAlive() == true; i++)
                    await Task.Delay(250, CancellationToken.None);

                existing = SupervisorState.Load(statePath);
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
            //
            // Раньше мы тут отказывались стартовать и предлагали человеку
            // снять их самому. Причина была уважительная: однажды мы убивали
            // движки не глядя и погасили исправный sing-box, которым владел
            // живой супервизор. Но отказ лечил это слишком широко — у окна
            // консоли нет, и «остановите их и повторите» не читал никто:
            // движки просто не поднимались.
            //
            // Теперь узкое лекарство. Живого супервизора мы уже сменили выше,
            // по-хорошему, и он забрал свои движки с собой. Всё, что дожило
            // до этой строки, не принадлежит никому: владельца нет, а TUN
            // и WinDivert они держат. Такое снимаем — иначе не поднимется
            // ничего, и человек опять останется с кнопкой, которая не работает.
            var orphans = ProcessSupervisor.FindOrphans(services);

            if (orphans.Count > 0)
            {
                Console.WriteLine($"Движки от прошлого запуска без хозяина: {orphans.Count}, снимаю");

                foreach (var orphan in orphans)
                {
                    try
                    {
                        Console.WriteLine($"  {orphan.ProcessName} (PID {orphan.Id})");
                        orphan.Kill(entireProcessTree: true);
                        orphan.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        // Мог уйти сам между поиском и снятием — обычное дело.
                        // А мог и не даться: тогда движок не поднимется,
                        // и супервизор скажет об этом своим порядком.
                        Console.WriteLine($"  не снялся: {ex.GetBaseException().Message}");
                    }
                    finally
                    {
                        orphan.Dispose();
                    }
                }

                // Драйверу нужно мгновение, чтобы отпустить фильтр.
                await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
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

        // Обход при мёртвых выходах читается из настроек, а не из ключей
        // запуска: решение это не про один запуск, а про то, чем человек
        // готов платить за работающую сеть, — и меняется оно там же, где
        // остальные такие решения.
        bool bypass = AppSettings.Load(AppSettings.DefaultPath).BypassWhenTunnelDead;

        services.Add(new SingBoxService(
            singBox, options.ProxyConfig, 9090, trafficPort, bypassWhenDead: bypass)
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

    /// <summary>Читает то, что собрал <see cref="BuildArguments"/>.</summary>
    internal static Options Parse(string[] args)
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
