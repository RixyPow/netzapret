using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetZapret.Supervisor;

/// <summary>
/// Объект задания Windows, убивающий дочерние процессы вместе с родителем.
/// </summary>
/// <remarks>
/// <para>
/// Без него закрытие окна супервизора крестиком оставляет sing-box и winws2
/// работать бесхозно. Наблюдалось вживую: осиротевший sing-box продолжал
/// держать TUN-адаптер, и следующий запуск падал с «Cannot create a file
/// when that file already exists», а осиротевший winws2 удерживал WinDivert.
/// При этом команда stop помочь не могла: она знает только PID супервизора
/// из файла состояния, а тот уже был удалён.
/// </para>
/// <para>
/// Задание с флагом KILL_ON_JOB_CLOSE решает это на уровне ядра: когда
/// последняя ссылка на задание закрывается — а она закрывается при любом
/// завершении процесса, включая аварийное, — система гасит всех его членов.
/// </para>
/// </remarks>
public sealed class ProcessJob : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    public ProcessJob()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);

        if (_handle == IntPtr.Zero)
            return;

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, buffer, fDeleteOld: false);
            SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Удалось ли создать задание. При неудаче поведение прежнее.</summary>
    public bool IsAvailable => _handle != IntPtr.Zero;

    /// <summary>
    /// Включает процесс в задание.
    /// </summary>
    /// <returns><c>false</c>, если не вышло — тогда процесс просто остаётся сам по себе.</returns>
    public bool Assign(Process process)
    {
        if (!IsAvailable)
            return false;

        try
        {
            return AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (Exception)
        {
            // Процесс мог успеть завершиться между запуском и включением.
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        IntPtr info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
