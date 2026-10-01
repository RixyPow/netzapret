using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetZapret.Core.Programs;

/// <summary>Запущенная программа: имя файла и полный путь.</summary>
public sealed record RunningProgram(string Name, string Path);

/// <summary>
/// Запущенные программы — для выбора программы в «Маршрутах», как в Happ.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 01.10, снимки Happ: программы там выбираются «из списка
/// процессов» или файлом, со значками и путями. У нас программу надо было
/// угадать и вписать в поле поиска, и человек с Diablo 4 так её и не нашёл.
/// </para>
/// <para>
/// Путь — через <c>QueryFullProcessImageName</c>, а не
/// <see cref="Process.MainModule"/>: тот бросает на части процессов (чужая
/// разрядность, защищённые), и список выходил бы с дырами. Процессы,
/// путь которых не отдают и так, пропускаются — выбрать их всё равно нечем.
/// </para>
/// </remarks>
public static class RunningPrograms
{
    /// <summary>По одной записи на путь, по алфавиту имён.</summary>
    public static IReadOnlyList<RunningProgram> List()
    {
        var found = new Dictionary<string, RunningProgram>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id is 0 or 4)
                    continue;

                if (PathOf(process.Id) is { } path && !found.ContainsKey(path))
                    found[path] = new RunningProgram(System.IO.Path.GetFileName(path), path);
            }
        }

        return found.Values
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Подходит ли программа под строку поиска — по имени или по пути, как в Happ.
    /// </summary>
    public static bool Matches(RunningProgram program, string? query)
    {
        var text = query?.Trim();

        return string.IsNullOrEmpty(text)
            || program.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || program.Path.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Полный путь исполняемого файла процесса; <c>null</c> — не отдают.</summary>
    public static string? PathOf(int processId)
    {
        const uint QueryLimitedInformation = 0x1000;

        var handle = OpenProcess(QueryLimitedInformation, false, (uint)processId);

        if (handle == IntPtr.Zero)
            return null;

        try
        {
            var buffer = new StringBuilder(1024);
            int size = buffer.Capacity;

            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
