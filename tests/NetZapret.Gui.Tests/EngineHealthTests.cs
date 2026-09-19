using System.Diagnostics;
using System.IO;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Поднялись ли движки на самом деле.
/// </summary>
/// <remarks>
/// <para>
/// Заведено по жалобе 19.09: «трей запускается, программа запускается,
/// а движки нужно поднимать кнопкой». В журнале при этом семь раз подряд
/// стояло «автозапуск: движки подняты с попытки 1» — и стояло правдиво
/// по прежней мерке: успехом считался порождённый процесс супервизора.
/// Дальше тот мог пять раз не поднять sing-box и сдаться, о чём никто
/// не спрашивал.
/// </para>
/// <para>
/// Состояние подкладывается файлом, как его и пишет супервизор. Живость
/// процесса берётся настоящая: чтобы состояние считалось живым, в нём стоит
/// номер процесса самих тестов, а чтобы мёртвым — номер, которого заведомо
/// нет. Подменять это нечем, и выдумывать подмену ради теста значило бы
/// проверять подмену, а не проверку.
/// </para>
/// </remarks>
public sealed class EngineHealthTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"netzapret-health-{Guid.NewGuid():N}");

    private readonly string _was = Directory.GetCurrentDirectory();

    public EngineHealthTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "runtime"));
        Directory.SetCurrentDirectory(_root);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_was);

        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    /// <summary>Номер процесса, которого нет.</summary>
    /// <remarks>
    /// Ищется, а не выдумывается: взятое с потолка число может оказаться
    /// чужим живым процессом, и тест начнёт врать через раз.
    /// </remarks>
    private static int DeadPid()
    {
        for (int candidate = 60000; candidate > 1000; candidate -= 7)
        {
            try
            {
                using var _ = Process.GetProcessById(candidate);
            }
            catch (ArgumentException)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("свободный номер процесса не нашёлся");
    }

    private static SupervisorState State(bool alive, params (string Name, ServiceHealth Health, string? Error)[] services) =>
        new()
        {
            SupervisorProcessId = alive ? Environment.ProcessId : DeadPid(),
            StartedAt = DateTimeOffset.Now,
            Services = services
                .Select(s => new ServiceState { Name = s.Name, Health = s.Health, LastError = s.Error })
                .ToList(),
        };

    /// <summary>Все службы работают — вот это и есть успех.</summary>
    [Fact]
    public void Everything_running_is_healthy()
    {
        var state = State(alive: true,
            ("sing-box", ServiceHealth.Healthy, null),
            ("winws2", ServiceHealth.Healthy, null));

        Assert.True(EngineHealth.AllHealthy(state));
    }

    /// <summary>
    /// Живой супервизор при мёртвой службе здоровьем не считается.
    /// </summary>
    /// <remarks>
    /// Тот самый случай из жалобы: супервизор порождён и жив, а sing-box
    /// не поднялся. Прежняя мерка звала это успехом.
    /// </remarks>
    [Fact]
    public void A_live_supervisor_with_a_dead_service_is_not_healthy()
    {
        var state = State(alive: true,
            ("sing-box", ServiceHealth.Dead, "процесс завершился с кодом 1"),
            ("winws2", ServiceHealth.Healthy, null));

        Assert.False(EngineHealth.AllHealthy(state));
        Assert.Contains("sing-box: процесс завершился с кодом 1", EngineHealth.Complaint(state));
    }

    /// <summary>«Жив, но не отвечает» — тоже не здоровье.</summary>
    /// <remarks>
    /// Так выглядит поднятый sing-box с мёртвым сервером подписки: процесс
    /// на месте, трафик не идёт. Объявить это успехом значило бы оставить
    /// человека с работающим значком и неработающим обходом.
    /// </remarks>
    [Fact]
    public void A_degraded_service_is_not_healthy()
    {
        var state = State(alive: true, ("sing-box", ServiceHealth.Degraded, null));

        Assert.False(EngineHealth.AllHealthy(state));
        Assert.Contains("проверку не проходит", EngineHealth.Complaint(state));
    }

    /// <summary>Сдавшийся супервизор — не здоровье, что бы ни стояло в службах.</summary>
    /// <remarks>
    /// Состояние на диске переживает процесс, который его написал: файл
    /// остаётся лежать со словом «работает» у службы, которой давно нет.
    /// Поэтому живость проверяется у самого супервизора, а не читается
    /// из файла.
    /// </remarks>
    [Fact]
    public void A_supervisor_that_gave_up_is_not_healthy()
    {
        var state = State(alive: false, ("sing-box", ServiceHealth.Healthy, null));

        Assert.False(EngineHealth.AllHealthy(state));
        Assert.Contains("не дожил", EngineHealth.Complaint(state));
    }

    /// <summary>
    /// Супервизор без служб — не работающий обход.
    /// </summary>
    /// <remarks>
    /// Так выглядит запуск без пресета: sing-box поднят, winws2 не заведён
    /// вовсе, десинка нет. Проверено на себе 19.09 — консольный <c>start</c>
    /// без <c>--preset</c> даёт ровно это, и в списке служб остаётся одна.
    /// </remarks>
    [Fact]
    public void A_supervisor_without_services_is_not_healthy()
    {
        var state = State(alive: true);

        Assert.False(EngineHealth.AllHealthy(state));
        Assert.Contains("ни одной службы", EngineHealth.Complaint(state));
    }

    /// <summary>Нет состояния — нечего и считать здоровым.</summary>
    [Fact]
    public void No_state_at_all_is_not_healthy()
    {
        Assert.False(EngineHealth.AllHealthy(null));
        Assert.Contains("не оставил состояния", EngineHealth.Complaint(null));
    }

    /// <summary>Причина берётся у службы, а не сочиняется.</summary>
    /// <remarks>
    /// «sing-box: не запустился — процесс завершился с кодом 1» говорит
    /// больше, чем «не получилось», и это та строка, которую утром читают
    /// в журнале.
    /// </remarks>
    [Fact]
    public void The_complaint_quotes_the_service()
    {
        var state = State(alive: true,
            ("sing-box", ServiceHealth.Dead, "configure tun interface: Access is denied"),
            ("winws2", ServiceHealth.Dead, "WinDivert не установился"));

        var said = EngineHealth.Complaint(state);

        Assert.Contains("Access is denied", said);
        Assert.Contains("WinDivert", said);
    }

    /// <summary>Здоровые в жалобу не попадают.</summary>
    [Fact]
    public void Healthy_services_are_not_complained_about()
    {
        var state = State(alive: true,
            ("sing-box", ServiceHealth.Healthy, null),
            ("winws2", ServiceHealth.Dead, "не поднялся"));

        Assert.DoesNotContain("sing-box", EngineHealth.Complaint(state));
        Assert.Contains("winws2", EngineHealth.Complaint(state));
    }

    /// <summary>
    /// Состояние читается с диска тем же путём, каким его пишет супервизор.
    /// </summary>
    /// <remarks>
    /// Проверка сквозная: запись, чтение и вывод. Разойдись формат — и
    /// автозапуск стал бы считать движки мёртвыми при живых, то есть
    /// перезапускать работающее.
    /// </remarks>
    [Fact]
    public void The_state_survives_a_round_trip_through_the_file()
    {
        var state = State(alive: true, ("sing-box", ServiceHealth.Healthy, null));

        state.Save(SupervisorState.DefaultPath);

        Assert.True(EngineHealth.AllHealthy(SupervisorState.Load(SupervisorState.DefaultPath)));
    }
}
