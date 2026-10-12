using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetZapret.Core.Updates;

/// <summary>Программа, которая держит файл.</summary>
/// <param name="Path">Где лежит её exe; <c>null</c> — прав посмотреть не хватило.</param>
public sealed record FileLocker(string Name, int ProcessId, string? Path);

/// <summary>
/// Кто держит файл — через Restart Manager Windows.
/// </summary>
/// <remarks>
/// До 12.10 о неудачной подмене человек узнавал «Update failed: some files
/// could not be replaced» и больше ничего: robocopy пишет код ошибки,
/// но не того, кто файл занял. Restart Manager — та же служба, через которую
/// установщики Windows спрашивают «закройте такую-то программу»; прав
/// администратора ему не нужно.
/// </remarks>
public static class FileLockers
{
    public static IReadOnlyList<FileLocker> Find(string path)
    {
        var found = new List<FileLocker>();

        try
        {
            var key = new StringBuilder(64);

            if (RmStartSession(out uint session, 0, key) != 0)
                return found;

            try
            {
                if (RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, null) != 0)
                    return found;

                uint needed = 0;
                uint count = 0;
                uint reasons = 0;

                int result = RmGetList(session, out needed, ref count, null, ref reasons);

                // ERROR_MORE_DATA — нормальный ответ первого вызова: столько нужно места.
                if (result != 234 || needed == 0)
                    return found;

                var infos = new RmProcessInfo[needed];
                count = needed;

                if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0)
                    return found;

                for (int i = 0; i < count; i++)
                {
                    int id = infos[i].Process.ProcessId;
                    found.Add(new FileLocker(infos[i].AppName, id, PathOf(id)));
                }
            }
            finally
            {
                RmEndSession(session);
            }
        }
        catch (Exception)
        {
            // Без ответа Restart Manager ошибка просто будет без имени программы.
        }

        return found;
    }

    /// <summary>Словами: «winws2.exe (PID 7340, C:\Zapret)».</summary>
    public static string Describe(FileLocker locker)
    {
        var folder = locker.Path is null ? null : System.IO.Path.GetDirectoryName(locker.Path);
        var name = locker.Path is null ? locker.Name : System.IO.Path.GetFileName(locker.Path);

        return folder is null
            ? $"{name} (PID {locker.ProcessId})"
            : $"{name} (PID {locker.ProcessId}, папка {folder})";
    }

    private static string? PathOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string AppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string ServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, StringBuilder key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint session, uint fileCount, string[] files, uint appCount, IntPtr apps, uint serviceCount, string[]? services);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? infos, ref uint rebootReasons);
}
