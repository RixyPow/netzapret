using System.Diagnostics;
using System.Text;

namespace NetZapret.Supervisor;

public sealed class SupervisorOptions
{
    /// <summary>Как часто опрашивать службы.</summary>
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Сколько ждать готовности при запуске и перезапуске.</summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Сколько раз подряд перезапускать упавшую службу, прежде чем сдаться.
    /// </summary>
    /// <remarks>
    /// Ограничение обязательно: без него неверный конфиг превращается
    /// в бесконечный цикл запусков, который выглядит как работающая система.
    /// </remarks>
    public int MaxRestarts { get; init; } = 5;

    /// <summary>Задержка перед первым перезапуском; дальше удваивается.</summary>
    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan MaxRestartBackoff { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Сколько подряд неудачных функциональных проверок терпеть, прежде чем
    /// считать службу мёртвой и перезапускать.
    /// </summary>
    /// <remarks>
    /// Не единица: разовый промах бывает от сетевой икоты, и перезапуск
    /// прокси из-за неё сделает хуже, чем просто подождать.
    /// </remarks>
    public int DegradedChecksBeforeRestart { get; init; } = 3;

    public string StatePath { get; init; } = SupervisorState.DefaultPath;

    public string LogPath { get; init; } = Path.Combine("runtime", "supervisor.log");
}

/// <summary>
/// Следит за службами: запускает, проверяет живость, перезапускает упавшие.
/// </summary>
/// <remarks>
/// Работает в переднем плане — <see cref="RunAsync"/> блокируется до отмены.
/// Отдельного демона нет намеренно: служба Windows потребовала бы прав
/// администратора, а фоновый процесс без консоли невозможно остановить
/// по Ctrl+C. Взаимодействие между запусками программы идёт через файл
/// состояния.
/// </remarks>
public sealed class ProcessSupervisor
{
    private readonly IReadOnlyList<SupervisedService> _services;
    private readonly SupervisorOptions _options;
    private readonly Dictionary<string, int> _degradedStreak = new();
    private readonly Dictionary<string, ServiceHealth> _health = new();

    /// <summary>С какого момента держится нынешнее состояние службы.</summary>
    private readonly Dictionary<string, DateTimeOffset> _since = new();
    private readonly RollingLog _log;

    public ProcessSupervisor(IReadOnlyList<SupervisedService> services, SupervisorOptions? options = null)
    {
        _services = services;
        _options = options ?? new SupervisorOptions();
        _log = new RollingLog(_options.LogPath);

        foreach (var service in services)
        {
            _degradedStreak[service.Name] = 0;
            SetHealth(service, ServiceHealth.Stopped);
        }
    }

    /// <summary>Вызывается при изменении состояния — для вывода в консоль.</summary>
    public Action<string>? OnEvent { get; set; }

    /// <summary>
    /// Находит движки, оставшиеся от прошлых запусков.
    /// </summary>
    /// <remarks>
    /// Осиротевший движок ломает запуск молча и неочевидно: старый sing-box
    /// держит TUN-адаптер, и новый падает с «Cannot create a file when that
    /// file already exists», а старый winws2 удерживает WinDivert. Наблюдалось
    /// вживую после закрытия окна супервизора крестиком.
    /// </remarks>
    public static IReadOnlyList<Process> FindOrphans(IReadOnlyList<SupervisedService> services)
    {
        var names = services.SelectMany(s => s.EngineProcessNames).Distinct(StringComparer.OrdinalIgnoreCase);
        var found = new List<Process>();

        foreach (var name in names)
        {
            try
            {
                found.AddRange(Process.GetProcessesByName(name));
            }
            catch (Exception)
            {
                // Перечисление процессов может отказать; это не повод падать.
            }
        }

        return found;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Log($"супервизор запущен, PID {Environment.ProcessId}");

        using var job = new ProcessJob();

        if (!job.IsAvailable)
            Log("объект задания недоступен: движки могут пережить супервизор при аварийном завершении");

        foreach (var service in _services)
            service.Job = job;

        foreach (var service in _services)
            await StartServiceAsync(service, cancellationToken);

        SaveState();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.CheckInterval, cancellationToken);
                await CheckAllAsync(cancellationToken);
                SaveState();
            }
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка.
        }
        finally
        {
            Log("останавливаю службы");

            foreach (var service in _services)
            {
                await service.StopAsync(CancellationToken.None);
                SetHealth(service, ServiceHealth.Stopped);
            }

            SupervisorState.Clear(_options.StatePath);
            Log("супервизор остановлен");
            _log.Dispose();
        }
    }

    private async Task StartServiceAsync(SupervisedService service, CancellationToken cancellationToken)
    {
        Log($"{service.Name}: запуск");

        bool ok = await service.StartAsync(_options.ReadinessTimeout, cancellationToken);

        if (ok)
        {
            SetHealth(service, ServiceHealth.Healthy);
            _degradedStreak[service.Name] = 0;
            Log($"{service.Name}: работает, PID {service.ProcessId}");
            return;
        }

        // Неудачный запуск обязан убрать за собой. Прежде не убирал: процесс
        // оставался жить, а супервизор считал его несостоявшимся и заводил
        // следующий. За несколько попыток набиралось несколько sing-box, и
        // однажды один из них успевал занять netzapret0 — дальше он работал
        // без присмотра, держа адаптер и порт API, а каждый новый падал
        // с «Cannot create a file when that file already exists».
        //
        // Со стороны это выглядело так: движок то поднимается на пару секунд,
        // то отваливается, и в окне «запущен, но не отвечает». Замер
        // 2026-09-09: супервизор считал своим PID 33384, а адаптер и порт
        // 9090 держал PID 35440, о котором он не знал.
        await service.StopAsync(CancellationToken.None);

        SetHealth(service, ServiceHealth.Dead);
        Log($"{service.Name}: не запустился — {service.LastError}");
    }

    private async Task CheckAllAsync(CancellationToken cancellationToken)
    {
        foreach (var service in _services)
        {
            if (_health[service.Name] == ServiceHealth.Faulted)
                continue;

            if (!service.IsProcessAlive)
            {
                Log($"{service.Name}: процесс исчез");
                await RestartAsync(service, cancellationToken);
                continue;
            }

            var check = await service.CheckFunctionalAsync(cancellationToken);

            if (check == ServiceCheck.Healthy)
            {
                if (_health[service.Name] != ServiceHealth.Healthy)
                    Log($"{service.Name}: снова здоров");

                SetHealth(service, ServiceHealth.Healthy);
                _degradedStreak[service.Name] = 0;
                continue;
            }

            // Мёртвый выход — не повод убивать движок. Состояние показываем
            // честно: трафик не идёт, и человеку это видно. А перезапуск
            // не чинил бы ничего — он гасит туннель на восемнадцать секунд
            // и поднимает его в ту же мёртвую подписку. Счётчик при этом
            // не растёт, иначе первая же затяжная беда у провайдера
            // исчерпала бы попытки и служба сдалась бы навсегда.
            //
            // Замер 20.09: семь из девяти серверов подписки не отвечали,
            // движок исправно держал Clash API, а супервизор гасил его
            // по кругу. Это и была жалоба «отваливается на пару секунд».
            if (!SupervisorRules.CountsTowardRestart(check))
            {
                if (_health[service.Name] != ServiceHealth.Degraded)
                    Log($"{service.Name}: трафик не идёт, но движок отвечает — перезапуск не поможет");

                SetHealth(service, SupervisorRules.HealthFor(check));
                continue;
            }

            _degradedStreak[service.Name]++;
            SetHealth(service, SupervisorRules.HealthFor(check));

            Log($"{service.Name}: проверка не прошла " +
                $"({_degradedStreak[service.Name]} из {_options.DegradedChecksBeforeRestart})");

            if (_degradedStreak[service.Name] >= _options.DegradedChecksBeforeRestart)
                await RestartAsync(service, cancellationToken);
        }
    }

    private async Task RestartAsync(SupervisedService service, CancellationToken cancellationToken)
    {
        if (service.RestartCount >= _options.MaxRestarts)
        {
            SetHealth(service, ServiceHealth.Faulted);
            Log($"{service.Name}: исчерпаны попытки перезапуска ({_options.MaxRestarts}), сдаюсь");
            return;
        }

        service.NoteRestart();
        _degradedStreak[service.Name] = 0;

        var delay = ComputeBackoff(service.RestartCount);
        Log($"{service.Name}: перезапуск #{service.RestartCount} через {delay.TotalSeconds:0} с");

        await service.StopAsync(cancellationToken);

        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await StartServiceAsync(service, cancellationToken);
    }

    /// <summary>
    /// Меняет состояние службы и помнит, с какого момента оно держится.
    /// </summary>
    /// <remarks>
    /// Время — только при смене: «выходы не отвечают с 23:30» говорит больше,
    /// чем «не отвечают», и отличает затяжную беду от минутной (26.09).
    /// </remarks>
    private void SetHealth(SupervisedService service, ServiceHealth health)
    {
        if (!_health.TryGetValue(service.Name, out var was) || was != health)
            _since[service.Name] = DateTimeOffset.Now;

        _health[service.Name] = health;

        // Здорова — значит прежней причины больше нет. Прежде она оставалась,
        // и состояние «работает» шло вместе с «выходы подписки не отвечают»:
        // 25.09 я на этой устаревшей строке объявил мёртвым живой VPN.
        if (health == ServiceHealth.Healthy)
            service.Recovered();
    }

    private TimeSpan ComputeBackoff(int restartCount)
    {
        // Удвоение при каждой попытке: если служба падает из-за постоянной
        // причины, частые перезапуски только жгут ресурсы.
        var delay = _options.RestartBackoff * Math.Pow(2, Math.Max(0, restartCount - 1));

        return delay > _options.MaxRestartBackoff ? _options.MaxRestartBackoff : delay;
    }

    public SupervisorState BuildState() => new()
    {
        SupervisorProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.Now,
        Services = _services.Select(s => new ServiceState
        {
            Name = s.Name,
            Health = _health[s.Name],
            HealthSince = _since.TryGetValue(s.Name, out var since) ? since : null,
            ProcessId = s.ProcessId,
            StartedAt = s.StartedAt,
            RestartCount = s.RestartCount,
            LastError = s.LastError,
        }).ToList(),
    };

    private void SaveState()
    {
        try
        {
            BuildState().Save(_options.StatePath);
        }
        catch (Exception ex) when (SupervisorState.IsBusy(ex))
        {
            // Файл состояния вспомогательный: не смогли записать — не повод падать.
            // И UnauthorizedAccessException тоже: прежде ловилось одно IOException,
            // и занятый окном файл ронял супервизор вместе с движками (19.09).
        }
    }

    private void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";

        OnEvent?.Invoke(line);
        _log.AppendLine(line);
    }
}
