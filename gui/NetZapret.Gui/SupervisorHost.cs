using System.Diagnostics;
using System.IO;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
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
            bool tookOver = false;

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
            if (existing is not null && SupervisorLock.IsOurs(existing.SupervisorProcessId))
            {
                Console.WriteLine(
                    $"Перенимаю у супервизора PID {existing.SupervisorProcessId}: "
                    + "двум сразу нельзя, они дерутся за TUN и WinDivert.");

                await StopAsync(CancellationToken.None);
                tookOver = true;

                // Ждём, пока он вправду уйдёт: стартовать поверх умирающего
                // значит получить обоих сразу, чего мы и избегаем.
                for (int i = 0; i < 20 && SupervisorState.Load(statePath) is { } left && SupervisorLock.IsOurs(left.SupervisorProcessId); i++)
                    await Task.Delay(250, CancellationToken.None);

                existing = SupervisorState.Load(statePath);
            }

            // Состояние от убитого процесса мешает: чистим, раз владелец мёртв.
            if (existing is not null)
                SupervisorState.Clear(statePath);

            // Замок — на всю жизнь супервизора (SupervisorLock). Файл состояния
            // появляется, лишь когда движки подняты, и супервизор, стартовавший
            // секундой раньше, по нему не виден; замок он берёт первым делом.
            using var held = await TakeLockAsync(tookOver);

            if (held is null)
            {
                Console.Error.WriteLine("Прежний супервизор не уступил замок за 15 с — второй не запускаю.");
                return 2;
            }

            var services = new List<SupervisedService>();

            // Десинк — первым: надзор поднимает службы по порядку и ждёт
            // готовности каждой, а готов winws2, когда начал перехват
            // (WinwsService.Ready). Всё, что туннель откроет следом, — уже
            // под десинком. Прежде sing-box шёл первым, и первое соединение
            // WARP уходило мимо десинка и замерзало (01.10).
            AddWinws(options, services);

            if (!options.NoProxy && !TryAddSingBox(options, services))
                return 2;

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
    /// Берёт замок супервизора, снимая прежнего держателя.
    /// </summary>
    /// <remarks>
    /// Держатель снимается так же, как остановкой, — целиком, с движками.
    /// После снятия — те же три секунды, что у перезапуска (EngineControl):
    /// TUN освобождается не мгновенно, и sing-box, начатый сразу, падает
    /// с «Cannot create a file when that file already exists».
    /// </remarks>
    /// <param name="removed">Прежний уже снят — по файлу состояния.</param>
    private static async Task<SupervisorLock?> TakeLockAsync(bool removed)
    {

        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (SupervisorLock.TryAcquire() is { } held)
            {
                if (removed)
                    await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);

                return held;
            }

            if (SupervisorLock.Holder() is { } holder && SupervisorLock.IsOurs(holder))
            {
                Console.WriteLine($"Перенимаю у супервизора PID {holder}: двум сразу нельзя, они дерутся за TUN и WinDivert.");
                await KillAsync(holder, CancellationToken.None);
                removed = true;
                continue;
            }

            // Замок в руках у того, кто в этот миг проверяет, свободен ли он, —
            // окно спрашивает об этом при запуске. Отпустит через мгновение.
            await Task.Delay(250, CancellationToken.None);
        }

        return null;
    }

    /// <summary>
    /// Жив ли супервизор — по файлу состояния, по замку и по запущенному окном процессу.
    /// </summary>
    public static bool IsRunning(int? spawned = null) =>
        Supervisors(spawned).Count > 0;

    /// <summary>
    /// Ждёт, пока запущенный супервизор возьмёт замок, — не дольше срока.
    /// </summary>
    /// <remarks>
    /// Запуск окна считается законченным, когда супервизор виден остальным:
    /// остановка, пришедшая следом, иначе не нашла бы его и сказала бы
    /// «не запущен» — так в отчёте reaass и остался жить супервизор без окна.
    /// </remarks>
    public static async Task<bool> WaitRegisteredAsync(int processId, TimeSpan limit, CancellationToken cancellationToken)
    {
        var until = DateTime.UtcNow + limit;

        while (DateTime.UtcNow < until)
        {
            if (SupervisorLock.Holder() == processId)
                return true;

            if (!SupervisorLock.IsOurs(processId))
                return false;

            await Task.Delay(100, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Гасит супервизор вместе со службами.
    /// </summary>
    /// <remarks>
    /// <para>
    /// По файлу состояния и замку, а не по имени процесса: имя теперь то же
    /// самое, что у окна, и поиск по нему закрыл бы и само окно.
    /// </para>
    /// <para>
    /// Замок и номер, запущенный окном (<paramref name="spawned"/>), — с 01.10:
    /// файл состояния пишется, только когда движки подняты, и остановка
    /// посреди подъёма его не находила.
    /// </para>
    /// </remarks>
    public static async Task<string> StopAsync(CancellationToken cancellationToken, int? spawned = null)
    {
        var statePath = SupervisorState.DefaultPath;
        var targets = Supervisors(spawned);

        foreach (var target in targets)
            await KillAsync(target, cancellationToken);

        SupervisorState.Clear(statePath);

        // Драйвер выгружается и без супервизора: WinDivert от прошлого
        // запуска может держать файл до перезагрузки. Замер 23.09: движки
        // опущены, служба Monkey — RUNNING. Без этого папку программы нельзя
        // было удалить до перезагрузки (жалоба 23.09).
        var driver = await WinDivertDriver.TryUnloadAsync(cancellationToken);

        return (targets.Count == 0 ? "Супервизор не запущен. " : "Остановлено. ") + driver;
    }

    /// <summary>Живые супервизоры: из файла состояния, из замка, запущенный окном.</summary>
    private static List<int> Supervisors(int? spawned)
    {
        var found = new List<int>();

        if (SupervisorState.Load(SupervisorState.DefaultPath) is { } state && state.IsSupervisorAlive())
            found.Add(state.SupervisorProcessId);

        if (SupervisorLock.Holder() is { } holder)
            found.Add(holder);

        if (spawned is { } own)
            found.Add(own);

        return found.Distinct().Where(SupervisorLock.IsOurs).ToList();
    }

    private static async Task KillAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (Exception)
        {
            // Мог завершиться сам между проверкой и вызовом.
        }
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
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        services.Add(new SingBoxService(
            singBox, options.ProxyConfig, 9090, trafficPort,
            bypassWhenDead: settings.BypassWhenTunnelDead,
            preferredExit: Warp.PreferredExit(settings),
            exitCheckSeconds: settings.ExitCheckSeconds,
            replacePinned: settings.ReplaceSilentServer)
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

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        bool gameFilter = settings.GameFilter;

        // Соединения туннеля с его серверами десинку не нужны, а стоят дорого
        // (замер 30.09 — в TunnelEndpoints). Без туннеля выводить нечего.
        IReadOnlyList<System.Net.IPAddress> servers = options.NoProxy
            ? []
            : TunnelEndpoints.Read(options.ProxyConfig, Warp.PreferredExit(settings));

        // И соединения, которые уходят в туннель по подменным адресам.
        var fakeRange = options.NoProxy ? null : TunnelEndpoints.FakeRange(options.ProxyConfig);

        // Игровой UDP Riot, пока его часть не на «десинке» (UdpOffDesync, 30.09).
        // Сети выбирает сборка списков по правилам, здесь их только подбирают.
        var udpOff = UdpOffDesync.Read();

        // Пакеты по дороге в наш TUN и из него (TunnelEndpoints.TunAddresses, 01.10).
        IReadOnlyList<System.Net.IPAddress> tun = options.NoProxy
            ? []
            : TunnelEndpoints.TunAddresses(options.ProxyConfig);

        var arguments = WinwsCommandLine.Build(preset, exclude, own, gameFilter, servers, fakeRange, udpOff, tun);

        bool carried = TunnelCapture.Carries(arguments, servers, fakeRange, udpOff, tun);

        if (fakeRange is not null && carried)
            Console.WriteLine($"Десинк не перехватывает трафик в туннель: подменные адреса {fakeRange}.");

        if (tun.Count > 0 && carried)
            Console.WriteLine($"Десинк не перехватывает пакеты на пути в TUN и из него: {string.Join(", ", tun)}.");

        if (udpOff.Count > 0)
        {
            Console.WriteLine(carried
                ? $"Десинк не перехватывает игровой UDP Riot: сетей — {udpOff.Count}."
                : "Игровой UDP Riot остался в перехвате десинка: у пресета свой --wf-raw-filter файлом.");
        }

        if (servers.Count > 0)
        {
            int skipped = TunnelCapture.Applied(arguments, servers, fakeRange, udpOff, tun);

            Console.WriteLine(skipped == 0
                ? "Серверы туннеля остались в перехвате десинка: у пресета свой --wf-raw-filter файлом."
                : skipped < servers.Count
                    ? $"Десинк не перехватывает соединения туннеля: {skipped} адресов серверов из {servers.Count}, остальные в фильтр не поместились."
                    : $"Десинк не перехватывает соединения туннеля: адресов серверов — {skipped}.");
        }

        if (gameFilter)
            Console.WriteLine($"Game filter: секции игр по ipset-all на портах {GameFilter.Ports}");

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
