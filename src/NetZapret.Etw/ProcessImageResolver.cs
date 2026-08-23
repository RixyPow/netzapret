using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace NetZapret.Etw;

/// <summary>
/// Определяет путь к исполняемому файлу по PID.
/// </summary>
/// <remarks>
/// ETW отдаёт PID, а правила сопоставляются по имени exe. <c>Process.MainModule</c>
/// для этого не годится: он открывает процесс с лишними правами и падает на
/// защищённых. <c>QueryFullProcessImageNameW</c> обходится
/// PROCESS_QUERY_LIMITED_INFORMATION и работает почти всегда.
/// <para>
/// Результат кэшируется: PID переиспользуются, но в пределах сессии наблюдения
/// вероятность коллизии пренебрежимо мала по сравнению с ценой открытия процесса
/// на каждое соединение.
/// </para>
/// </remarks>
public sealed class ProcessImageResolver
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    private readonly ConcurrentDictionary<int, string?> _cache = new();

    public string? Resolve(int processId)
    {
        if (processId <= 0)
            return null;

        return _cache.GetOrAdd(processId, static pid =>
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;

                return QueryFullProcessImageNameW(handle, 0, buffer, ref size)
                    ? buffer.ToString()
                    : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        });
    }

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
