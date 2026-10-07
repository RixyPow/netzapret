using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace NetZapret.Gui;

/// <summary>
/// Кадр видео — тот, что проводник показывает значком файла.
/// </summary>
/// <remarks>
/// <para>
/// Нужен проверке читаемости живого фона: она мерит яркость картинки под
/// затемнением, а кадр из проигрывателя WPF не снимается —
/// <c>RenderTargetBitmap</c> рисует видео чёрным (замер 07.10 на fish.mp4:
/// оба кадра, через 1 и 6 с, — сплошная чернота). Значок проводника берёт
/// кадр средствами самой Windows.
/// </para>
/// <para>
/// Один кадр, а не всё видео: у ролика, который за двенадцать секунд
/// темнеет и светлеет, проверка увидит только его. Нет значка (нет кодека,
/// проводник не умеет) — проверка по картинке не проводится вовсе.
/// </para>
/// </remarks>
internal static class VideoPoster
{
    /// <summary>Кадр не шире <paramref name="size"/> точек; <c>null</c> — не получилось.</summary>
    public static BitmapSource? Load(string path, int size)
    {
        IntPtr bitmap = IntPtr.Zero;
        IShellItemImageFactory? factory = null;

        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);

            // Только значок-кадр: без него проводник отдал бы значок программы,
            // и проверка мерила бы его.
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, ThumbnailOnly, out bitmap) != 0 || bitmap == IntPtr.Zero)
                return null;

            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException
            or NotImplementedException or System.IO.IOException or UnauthorizedAccessException)
        {
            // Ошибка оболочки приходит разными исключениями: «нет файла» —
            // FileNotFoundException, «не умею» — NotImplementedException.
            return null;
        }
        finally
        {
            if (bitmap != IntPtr.Zero)
                DeleteObject(bitmap);

            if (factory is not null)
                Marshal.ReleaseComObject(factory);
        }
    }

    private const int ThumbnailOnly = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path,
        IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
}
