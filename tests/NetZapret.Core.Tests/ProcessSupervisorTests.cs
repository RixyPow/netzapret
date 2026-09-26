using System.Diagnostics;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Цикл надзора: запуск, проверка, перезапуск, отказ — на подставной службе.
/// </summary>
/// <remarks>
/// <para>
/// До 24.09 надзор не проверялся ничем, кроме файла состояния, — README так
/// и признавал: «нет супервизора и запуска движков». А решения в нём дорогие:
/// перезапуск исправного движка при мёртвом выходе стоил туннелю восемнадцати
/// секунд тишины по кругу (20.09), несостоявшийся запуск без уборки плодил
/// по несколько sing-box, и один из них держал адаптер без присмотра (09.09).
/// </para>
/// <para>
/// Служба — настоящий процесс (ping на себя), а не заглушка: надзор меряет
/// живость по процессу, и без процесса проверялась бы не она.
/// </para>
/// </remarks>
public sealed class ProcessSupervisorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"netzapret-supervisor-{Guid.NewGuid():N}");

    public ProcessSupervisorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Служба с настоящим процессом и проверкой, которой управляет тест.</summary>
    private sealed class FakeService : SupervisedService
    {
        private volatile int _answer = (int)ServiceCheck.Healthy;

        public ServiceCheck Answer
        {
            get => (ServiceCheck)_answer;
            set => _answer = (int)value;
        }

        public string? Problem { get; set; }

        public int Checks;

        public Process? Current => Process;

        public override string Name => "проба";

        public override IReadOnlyList<string> EngineProcessNames => [];

        public override string? ValidatePrerequisites() => Problem;

        public override Task<ServiceCheck> CheckFunctionalAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Checks);

            // Как sing-box: при мёртвом выходе называет причину.
            if (Answer == ServiceCheck.UpstreamDown)
                LastError = "выходы подписки не отвечают";

            return Task.FromResult(Answer);
        }

        protected override ProcessStartInfo BuildStartInfo()
        {
            var info = new ProcessStartInfo("ping.exe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add("-n");
            info.ArgumentList.Add("600");
            info.ArgumentList.Add("127.0.0.1");

            return info;
        }
    }

    private SupervisorOptions Options(int maxRestarts = 5, int degraded = 3) => new()
    {
        CheckInterval = TimeSpan.FromMilliseconds(80),
        ReadinessTimeout = TimeSpan.FromSeconds(2),
        RestartBackoff = TimeSpan.FromMilliseconds(10),
        MaxRestartBackoff = TimeSpan.FromMilliseconds(40),
        MaxRestarts = maxRestarts,
        DegradedChecksBeforeRestart = degraded,
        StatePath = Path.Combine(_dir, "supervisor.state.json"),
        LogPath = Path.Combine(_dir, "supervisor.log"),
    };

    private static async Task<ServiceState> WaitFor(SupervisorOptions options, Func<ServiceState, bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (SupervisorState.Load(options.StatePath)?.Services.FirstOrDefault() is { } state && condition(state))
                return state;

            await Task.Delay(40);
        }

        throw new TimeoutException($"не дождались: {what}");
    }

    /// <summary>Жив ли ещё наш ping — по номеру, объект процесса надзор уже освободил.</summary>
    private static bool IsPingAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.ProcessName.Equals("PING", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task Stop(CancellationTokenSource cts, Task run)
    {
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task StartsWritesStateAndCleansUpOnStop()
    {
        var service = new FakeService();
        var options = Options();
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        var state = await WaitFor(options, s => s.Health == ServiceHealth.Healthy, "служба поднялась");

        Assert.NotNull(state.ProcessId);
        int pid = state.ProcessId!.Value;

        await Stop(cts, run);

        // Остановка обязана погасить процесс и стереть файл состояния:
        // оставшийся файл следующий запуск принял бы за живой экземпляр.
        Assert.False(IsPingAlive(pid));
        Assert.False(File.Exists(options.StatePath));
    }

    [Fact]
    public async Task AVanishedProcessIsRestarted()
    {
        var service = new FakeService();
        var options = Options();
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        var first = await WaitFor(options, s => s.Health == ServiceHealth.Healthy, "служба поднялась");

        service.Current!.Kill();

        var second = await WaitFor(options,
            s => s.RestartCount == 1 && s.Health == ServiceHealth.Healthy && s.ProcessId is not null && s.ProcessId != first.ProcessId,
            "перезапуск после исчезновения процесса");

        Assert.Equal(1, second.RestartCount);

        await Stop(cts, run);
    }

    [Fact]
    public async Task ABrokenEngineIsRestartedOnlyAfterSeveralChecksInARow()
    {
        var service = new FakeService();
        var options = Options(degraded: 3);
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        await WaitFor(options, s => s.Health == ServiceHealth.Healthy, "служба поднялась");

        // Разовый промах перезапуском не лечится: сетевая икота бывает.
        int before = service.Checks;
        service.Answer = ServiceCheck.Broken;
        // Первая неудача — только пометка, не перезапуск. Состояние — из
        // ожидания, а не перечитанное: чтение на занятом файле даёт null.
        var early = await WaitFor(options, s => s.Health == ServiceHealth.Degraded, "служба помечена нездоровой");
        Assert.Equal(0, early.RestartCount);

        var restarted = await WaitFor(options, s => s.RestartCount >= 1, "перезапуск после трёх неудач");

        // Сразу «здоров»: новому процессу надо пройти готовность, иначе
        // надзор пошёл бы на следующий круг.
        service.Answer = ServiceCheck.Healthy;

        Assert.True(service.Checks - before >= options.DegradedChecksBeforeRestart);
        Assert.Equal(1, restarted.RestartCount);

        await Stop(cts, run);
    }

    [Fact]
    public async Task ADeadExitIsNotTreatedByRestarting()
    {
        // Замер 20.09: семь из девяти серверов подписки молчали, движок
        // исправно отвечал, а надзор гасил его по кругу — туннель пропадал
        // на восемнадцать секунд раз за разом и поднимался в ту же беду.
        var service = new FakeService();
        var options = Options(degraded: 2);
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        var first = await WaitFor(options, s => s.Health == ServiceHealth.Healthy, "служба поднялась");

        service.Answer = ServiceCheck.UpstreamDown;
        int before = service.Checks;

        // Состояние — то, что вернуло ожидание, а не перечитанное следом:
        // супервизор пишет файл в своём цикле, и чтение на занятом файле
        // отвечает «состояния нет». В CI 26.09 тест дважды упал на этом
        // NullReferenceException при исправном супервизоре.
        var state = await WaitFor(options, s => s.Health == ServiceHealth.Degraded && service.Checks - before >= 8,
            "восемь проверок при мёртвом выходе");

        Assert.Equal(0, state.RestartCount);
        Assert.Equal(first.ProcessId, state.ProcessId);

        await Stop(cts, run);
    }

    /// <summary>
    /// Причина уходит вместе с бедой, а время начала меняется вместе с состоянием.
    /// </summary>
    /// <remarks>
    /// 25.09 состояние «работает» шло вместе с «выходы подписки не отвечают»:
    /// причина оставалась от прошлой беды, и на ней я объявил живой VPN мёртвым.
    /// </remarks>
    [Fact]
    public async Task RecoveryClearsTheReasonAndStampsTheTime()
    {
        var service = new FakeService();
        var options = Options(degraded: 2);
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        var healthy = await WaitFor(options, s => s.Health == ServiceHealth.Healthy, "служба поднялась");

        service.Answer = ServiceCheck.UpstreamDown;
        var down = await WaitFor(options, s => s.Health == ServiceHealth.Degraded, "мёртвый выход");

        Assert.Equal("выходы подписки не отвечают", down.LastError);
        Assert.NotNull(down.HealthSince);
        Assert.True(down.HealthSince >= healthy.HealthSince);

        service.Answer = ServiceCheck.Healthy;
        var back = await WaitFor(options, s => s.Health == ServiceHealth.Healthy, "выход ожил");

        Assert.Null(back.LastError);
        Assert.True(back.HealthSince >= down.HealthSince);

        await Stop(cts, run);
    }

    [Fact]
    public async Task AfterTheRestartLimitTheServiceGivesUp()
    {
        var service = new FakeService();
        var options = Options(maxRestarts: 2);
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        await WaitFor(options, s => s.Health == ServiceHealth.Healthy, "служба поднялась");

        // Убиваем каждый новый процесс, пока надзор не сдастся.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        ServiceState? state = null;

        while (DateTime.UtcNow < deadline)
        {
            state = SupervisorState.Load(options.StatePath)?.Services.FirstOrDefault();

            if (state?.Health == ServiceHealth.Faulted)
                break;

            try
            {
                if (service.Current is { HasExited: false } process)
                    process.Kill();
            }
            catch (InvalidOperationException)
            {
                // Процесс уже ушёл между проверкой и выстрелом.
            }

            await Task.Delay(40);
        }

        Assert.NotNull(state);
        Assert.Equal(ServiceHealth.Faulted, state!.Health);
        Assert.Equal(2, state.RestartCount);

        await Stop(cts, run);
    }

    [Fact]
    public async Task AStartThatCannotHappenIsReportedDead()
    {
        var service = new FakeService { Problem = "нет исполняемого файла" };
        var options = Options();
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        var state = await WaitFor(options, s => s.Health is ServiceHealth.Dead or ServiceHealth.Faulted, "отказ запуска");

        Assert.Equal("нет исполняемого файла", state.LastError);
        Assert.Null(state.ProcessId);

        await Stop(cts, run);
    }

    [Fact]
    public async Task AStartThatNeverGetsReadyLeavesNoProcessBehind()
    {
        // 09.09: несостоявшийся запуск не убирал за собой, за несколько попыток
        // набиралось несколько sing-box, и один держал адаптер без присмотра.
        var service = new FakeService { Answer = ServiceCheck.Broken };
        var options = Options();
        using var cts = new CancellationTokenSource();

        var run = new ProcessSupervisor([service], options).RunAsync(cts.Token);
        var state = await WaitFor(options, s => s.Health == ServiceHealth.Dead, "отказ готовности");

        Assert.Null(state.ProcessId);
        Assert.Null(service.Current);

        await Stop(cts, run);
    }
}

/// <summary>Состояние службы словами — одно для окна, трея и nz status.</summary>
public sealed class EngineStatusTests
{
    private static ServiceState State(ServiceHealth health, string? error = null, int? pid = null) => new()
    {
        Name = "sing-box",
        Health = health,
        ProcessId = pid,
        LastError = error,
        HealthSince = new DateTimeOffset(2026, 9, 26, 23, 30, 0, DateTimeOffset.Now.Offset),
    };

    [Fact]
    public void AHealthyEngineShowsItsProcess() =>
        Assert.Equal("работает, процесс 6784", EngineHealth.Status(State(ServiceHealth.Healthy, pid: 6784)));

    [Fact]
    public void ADegradedEngineSaysWhyAndSince() =>
        Assert.Equal(
            "выходы подписки не отвечают — трафик идёт мимо туннеля · с 23:30",
            EngineHealth.Status(State(ServiceHealth.Degraded, "выходы подписки не отвечают — трафик идёт мимо туннеля")));

    [Fact]
    public void WithoutAReasonDegradedStillSaysSomething() =>
        Assert.Equal("запущен, но не отвечает · с 23:30", EngineHealth.Status(State(ServiceHealth.Degraded)));

    [Fact]
    public void AStoppedEngineHasNoTime() =>
        Assert.Equal("остановлен", EngineHealth.Status(State(ServiceHealth.Stopped)));
}
