using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace NetZapret.Supervisor;

/// <summary>Что берёт один движок.</summary>
/// <param name="Cores">Процессор в долях одного ядра (1 — ядро целиком); <c>null</c> — первый замер, сравнивать не с чем.</param>
/// <param name="Memory">Рабочий набор процесса, байтами.</param>
public sealed record EngineUse(string Name, double? Cores, long Memory);

/// <summary>
/// Нагрузка наших движков — процессор и память — для «Главной».
/// </summary>
/// <remarks>
/// <para>
/// Процессор — в долях ядра, а не процентах машины (CLAUDE.md, «Разбор
/// неисправностей»): winws2 однопоточный, и 30.09 «5,7 %» от шестнадцати ядер
/// оказались 91 % одного — winws2 был занят целиком. Доля машины прячет
/// ровно то, ради чего на это число смотрят.
/// </para>
/// <para>
/// Процессы — по номерам из состояния надзора, а не по именам: winws2 и sing-box
/// бывают и чужие (Zapret GUI, Happ), и их нагрузка не наша. Процессор и память
/// читаются без прав администратора (<see cref="Read"/>). Шаг процессорного
/// времени — 15,6 мс: при замере раз в две секунды это 0,8 % ядра.
/// </para>
/// </remarks>
public sealed class EngineLoad
{
    private readonly Dictionary<int, (TimeSpan Cpu, long At)> _seen = [];

    /// <summary>Замер всех служб надзора; движки, которых нет, пропускаются.</summary>
    public IReadOnlyList<EngineUse> Sample(SupervisorState? state)
    {
        var result = new List<EngineUse>();
        var alive = new HashSet<int>();

        foreach (var service in state?.Services ?? [])
        {
            if (service.ProcessId is not { } pid)
                continue;

            // Процесс вышел между чтением состояния и замером — его просто нет.
            if (Read(pid) is not { } read)
                continue;

            long now = Stopwatch.GetTimestamp();

            double? cores = _seen.TryGetValue(pid, out var was)
                ? Share(read.Cpu - was.Cpu, Stopwatch.GetElapsedTime(was.At, now))
                : null;

            _seen[pid] = (read.Cpu, now);
            alive.Add(pid);
            result.Add(new EngineUse(service.Name, cores, read.Memory));
        }

        foreach (var gone in _seen.Keys.Where(pid => !alive.Contains(pid)).ToList())
            _seen.Remove(gone);

        return result;
    }

    /// <summary>
    /// Процессорное время и рабочий набор процесса; <c>null</c> — процесса нет
    /// или он не даёт себя спросить.
    /// </summary>
    /// <remarks>
    /// Прямо у системы, по дескриптору процесса, а не через <see cref="Process"/>:
    /// его <c>WorkingSet64</c> ради одного числа снимает все процессы и потоки
    /// системы. Замер 09.11 на машине владельца: 13 мс и 12,5 мс процессора
    /// на замер двух движков, раз в две секунды, пока открыта «Главная», —
    /// две трети всего её опроса (19 мс на тик). Право
    /// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> даётся и без прав администратора.
    /// </remarks>
    private static (TimeSpan Cpu, long Memory)? Read(int pid)
    {
        nint handle = OpenProcess(QueryLimitedInformation, false, pid);

        if (handle == 0)
            return null;

        try
        {
            // Завершившийся процесс, чей дескриптор ещё кто-то держит, открывается
            // и отдаёт времена — но это уже не работающий движок.
            if (!GetExitCodeProcess(handle, out int exit) || exit != StillActive)
                return null;

            if (!GetProcessTimes(handle, out _, out _, out long kernel, out long user))
                return null;

            var counters = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };

            if (!K32GetProcessMemoryInfo(handle, ref counters, counters.Size))
                return null;

            return (TimeSpan.FromTicks(kernel + user), (long)counters.WorkingSetSize);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const int QueryLimitedInformation = 0x1000;
    private const int StillActive = 259;

    /// <summary>PROCESS_MEMORY_COUNTERS: из него нужен только рабочий набор.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(nint handle, out int code);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(nint handle, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool K32GetProcessMemoryInfo(nint handle, ref MemoryCounters counters, uint size);

    /// <summary>Доля ядра: процессорное время за промежуток к самому промежутку.</summary>
    public static double Share(TimeSpan cpu, TimeSpan wall) =>
        wall <= TimeSpan.Zero ? 0 : Math.Max(0, cpu.TotalSeconds / wall.TotalSeconds);

    /// <summary>«9 %» — доля одного ядра; «—», пока сравнивать не с чем.</summary>
    public static string Cores(IReadOnlyList<EngineUse> engines) =>
        engines.Count == 0 || engines.All(e => e.Cores is null)
            ? "—"
            : Percent(engines.Sum(e => e.Cores ?? 0));

    /// <summary>Подсказка: каждый движок отдельно и что значит число.</summary>
    public static string Explain(IReadOnlyList<EngineUse> engines, Func<long, string> size)
    {
        if (engines.Count == 0)
            return "Движки не запущены.";

        var lines = engines.Select(e =>
            $"{e.Name}: {(e.Cores is { } cores ? Percent(cores) + " ядра" : "замеряю")}, {size(e.Memory)}");

        return string.Join("\n", lines)
            + "\n\nПроцессор — в долях одного ядра: 100 % — ядро занято целиком. "
            + "winws2 однопоточный, и для него 100 % — потолок: дальше он не ускорится, "
            + "а соединения начнут ждать. Память — рабочий набор процессов.";
    }

    private static string Percent(double cores) =>
        (cores * 100).ToString(cores * 100 < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + " %";
}
