using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;

namespace NetZapret.Gui;

/// <summary>
/// Значки программ — из самого исполняемого файла, как в Happ.
/// </summary>
/// <remarks>
/// <para>
/// Готовыми замороженными картинками: окно выбора достаёт их в фоне, а
/// незамороженную картинку из чужого потока WPF показать не даст. В памяти
/// на время работы окна: в списке сотни процессов с повторами путей.
/// </para>
/// <para>
/// Маршрут программы хранит только имя файла (путь в WindowsApps меняется
/// с каждым обновлением), и до 03.11 значок у папки программы в «Маршрутах»
/// был, только пока программа запущена, — иначе буква. Поэтому однажды
/// увиденный значок ложится на диск по имени файла (<see cref="Remember"/>)
/// и берётся оттуда, когда программы нет среди запущенных (<see cref="Saved"/>).
/// </para>
/// </remarks>
internal static class ProgramIcons
{
    private static readonly ConcurrentDictionary<string, BitmapImage?> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, BitmapImage?> Stored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Значки по имени программы — рядом со значками сайтов, в рантайме.</summary>
    private static string Folder => Path.Combine("runtime", "icons", "programs");

    /// <summary>Значок файла; <c>null</c> — файла нет или значок не достаётся.</summary>
    public static BitmapImage? Of(string path) => Cache.GetOrAdd(path, Load);

    /// <summary>
    /// Значок файла по <paramref name="path"/>, заодно сохранённый на диск под
    /// именем программы — чтобы показать его, когда программа не запущена.
    /// </summary>
    public static BitmapImage? Remember(string program, string path)
    {
        var icon = Of(path);

        if (icon is null)
            return null;

        Stored[program] = icon;

        try
        {
            var file = FileOf(program);

            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Folder);

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(icon));
                using var stream = File.Create(file);
                encoder.Save(stream);
            }
        }
        catch (Exception)
        {
            // Не записался — покажется в следующий раз, когда программа запущена.
        }

        return icon;
    }

    /// <summary>Сохранённый значок программы; <c>null</c> — её ещё не видели запущенной.</summary>
    public static BitmapImage? Saved(string program) => Stored.GetOrAdd(program, name =>
    {
        try
        {
            var file = FileOf(name);

            if (!File.Exists(file))
                return null;

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(file));
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception)
        {
            return null;
        }
    });

    private static string FileOf(string program) =>
        Path.Combine(Folder, string.Concat(program.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".png");

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
