using System.Diagnostics;
using System.Globalization;

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
/// бывают и чужие (Zapret GUI, Happ), и их нагрузка не наша. Процессор
/// читается без прав администратора. Шаг <see cref="Process.TotalProcessorTime"/>
/// — 15,6 мс: при замере раз в две секунды это 0,8 % ядра.
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

            try
            {
                using var process = Process.GetProcessById(pid);

                var cpu = process.TotalProcessorTime;
                long now = Stopwatch.GetTimestamp();

                double? cores = _seen.TryGetValue(pid, out var was)
                    ? Share(cpu - was.Cpu, Stopwatch.GetElapsedTime(was.At, now))
                    : null;

                _seen[pid] = (cpu, now);
                alive.Add(pid);
                result.Add(new EngineUse(service.Name, cores, process.WorkingSet64));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Процесс вышел между чтением состояния и замером — его просто нет.
            }
        }

        foreach (var gone in _seen.Keys.Where(pid => !alive.Contains(pid)).ToList())
            _seen.Remove(gone);

        return result;
    }

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
