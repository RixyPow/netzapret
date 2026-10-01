using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace NetZapret.Gui;

/// <summary>
/// Значки программ — из самого исполняемого файла, как в Happ.
/// </summary>
/// <remarks>
/// Готовыми замороженными картинками: окно выбора достаёт их в фоне, а
/// незамороженную картинку из чужого потока WPF показать не даст. В памяти
/// на время работы окна: в списке сотни процессов с повторами путей.
/// </remarks>
internal static class ProgramIcons
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Значок файла; <c>null</c> — файла нет или значок не достаётся.</summary>
    public static BitmapImage? Of(string path) => Cache.GetOrAdd(path, Load);

    private static BitmapImage? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);

            if (icon is null)
                return null;

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            // Значок — украшение: без него строка всё равно выбирается.
            return null;
        }
    }
}
