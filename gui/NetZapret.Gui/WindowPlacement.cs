using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NetZapret.Core;

namespace NetZapret.Gui;

/// <summary>
/// Запоминает место и размер главного окна между открытиями и запусками (10.11).
/// </summary>
/// <remarks>
/// <para>
/// Жалоба 10.11: куда окно ни передвинь, как ни растяни, даже развёрнутое —
/// после перезапуска оно снова маленькое посередине. Места не хранил никто:
/// в разметке стояло <c>CenterScreen</c> и 1080×720, а из трея окно через
/// минуту закрывается по-настоящему и создаётся заново.
/// </para>
/// <para>
/// Через <c>GetWindowPlacement</c>/<c>SetWindowPlacement</c>, а не через
/// <c>Left</c>/<c>Width</c> WPF: система отдаёт обычное место и у развёрнутого
/// окна (куда вернуться), считает в пикселях рабочей области — мониторы
/// с разным масштабом не путают размер, — и сама сдвигает на экран окно,
/// которое оказалось бы целиком за ним (монитор отключили).
/// </para>
/// </remarks>
internal static class WindowPlacement
{
    private const int SwHide = 0;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 2;

    /// <summary>Сохранённое место есть — окно не ставит себя посередине.</summary>
    public static void Prepare(Window window, WindowPlace? place)
    {
        if (place is null || place.Right - place.Left < 100 || place.Bottom - place.Top < 100)
            return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;

        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var handle = new WindowInteropHelper(window).Handle;

                var placement = new Native
                {
                    Length = Marshal.SizeOf<Native>(),

                    // Окно ещё не показано, и показывать его здесь нельзя:
                    // при запуске в трей оно должно остаться спрятанным.
                    // Разворачивает WPF своим WindowState, когда покажет.
                    ShowCmd = SwHide,
                    MinPosition = new Point32 { X = -1, Y = -1 },
                    MaxPosition = new Point32 { X = -1, Y = -1 },
                    Normal = new Rect32 { Left = place.Left, Top = place.Top, Right = place.Right, Bottom = place.Bottom },
                };

                SetWindowPlacement(handle, ref placement);

                if (place.Maximized)
                    window.WindowState = WindowState.Maximized;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                // Не встало — откроется как прежде; место не стоит падения окна.
            }
        };
    }

    /// <summary>Где окно сейчас; <c>null</c> — спросить не вышло.</summary>
    public static WindowPlace? Read(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            if (handle == IntPtr.Zero)
                return null;

            var placement = new Native { Length = Marshal.SizeOf<Native>() };

            if (!GetWindowPlacement(handle, ref placement))
                return null;

            return new WindowPlace
            {
                Left = placement.Normal.Left,
                Top = placement.Normal.Top,
                Right = placement.Normal.Right,
                Bottom = placement.Normal.Bottom,

                // Свёрнутое окно помнит, каким было до сворачивания.
                Maximized = placement.ShowCmd == SwShowMaximized
                    || (placement.ShowCmd == SwShowMinimized && (placement.Flags & WpfRestoreToMaximized) != 0),
            };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ExternalException)
        {
            return null;
        }
    }

    /// <summary>Записывает место окна в настройки, если оно изменилось.</summary>
    public static void Remember(Window window)
    {
        if (Read(window) is not { } place)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (settings.Window != place)
                (settings with { Window = place }).Save(AppSettings.DefaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не записалось — в следующий раз откроется на прежнем месте.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Native
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point32 MinPosition;
        public Point32 MaxPosition;
        public Rect32 Normal;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref Native lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr hWnd, ref Native lpwndpl);
}
