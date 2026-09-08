using System.IO;
using System.Windows.Media.Imaging;

namespace NetZapret.Gui;

/// <summary>
/// Картинки флагов из папки рядом с программой.
/// </summary>
/// <remarks>
/// <para>
/// Windows флаги стран не рисует: в Segoe UI Emoji их нет вовсе, и пара
/// региональных букв показывается как «de». Шрифтом это не лечится, поэтому
/// флаги лежат картинками в <c>Assets\flags\</c> и зовутся по коду страны:
/// <c>de.png</c>, <c>ru.png</c>, <c>us.png</c>.
/// </para>
/// <para>
/// Чего нет, того не выдумываем: для страны без картинки остаётся значок
/// с буквами. Пустая папка — рабочее состояние, а не поломка.
/// </para>
/// <para>
/// SVG не годится — WPF его не читает без сторонней библиотеки. Нужны
/// растровые: png, jpg, gif или bmp.
/// </para>
/// </remarks>
public static class FlagImages
{
    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".gif", ".bmp"];

    /// <summary>Загруженное запоминается: строк с одной страной бывает много.</summary>
    private static readonly Dictionary<string, BitmapImage?> Loaded = new(StringComparer.OrdinalIgnoreCase);

    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Assets", "flags");

    /// <summary>Есть ли вообще папка с флагами.</summary>
    public static bool Any => Directory.Exists(Folder);

    /// <summary>
    /// Флаг по коду страны; <c>null</c> — картинки нет.
    /// </summary>
    /// <param name="code">Две буквы, как в теге подписки: <c>DE</c>, <c>RU</c>.</param>
    public static BitmapImage? For(string code)
    {
        if (code.Length != 2)
            return null;

        lock (Loaded)
        {
            if (Loaded.TryGetValue(code, out var known))
                return known;

            var image = Load(code);
            Loaded[code] = image;

            return image;
        }
    }

    private static BitmapImage? Load(string code)
    {
        foreach (var extension in Extensions)
        {
            var path = Path.Combine(Folder, code.ToLowerInvariant() + extension);

            if (!File.Exists(path))
                continue;

            try
            {
                var image = new BitmapImage();
                image.BeginInit();

                // Читаем в память целиком: иначе файл остаётся открытым,
                // и папку с флагами нельзя будет обновить, пока окно живо.
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze();

                return image;
            }
            catch (Exception)
            {
                // Битый файл — не повод падать. Останется значок с буквами.
                return null;
            }
        }

        return null;
    }
}
