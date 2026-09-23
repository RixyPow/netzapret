using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetZapret.Core.Themes;

namespace NetZapret.Gui;

/// <summary>Чем кончилось применение темы.</summary>
public sealed record ThemeApplied(string Id, bool Ok, IReadOnlyList<string> Problems);

/// <summary>
/// Применяет тему: подменяет словарь палитры на собранный из <c>theme.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// Стили про тему не знают: они берут кисти и шрифты по ключам через
/// DynamicResource. Поэтому тема из файла — это тот же словарь с теми же
/// ключами, только собранный в коде, а не прочитанный из XAML.
/// </para>
/// <para>
/// Palette.xaml и Light.xaml остаются запасом: если папки тем нет или
/// тема не прошла проверку, применяется встроенный XAML того же имени.
/// Их значения совпадают с themes\dark и themes\light — это сторожит тест.
/// </para>
/// </remarks>
public static class Themes
{
    private const string DarkPath = "Theme/Palette.xaml";
    private const string LightPath = "Theme/Light.xaml";

    public const string DefaultId = "dark";

    /// <summary>Применённая сейчас тема.</summary>
    public static string Current { get; private set; } = DefaultId;

    /// <summary>
    /// Применяет тему по имени папки; при отказе — встроенную из XAML.
    /// </summary>
    public static ThemeApplied Apply(string? id)
    {
        var wanted = string.IsNullOrWhiteSpace(id) ? DefaultId : id.Trim();

        ThemeLoad load;

        try
        {
            load = ThemeLoader.Load(wanted);
        }
        catch (Exception ex)
        {
            load = new ThemeLoad(wanted, string.Empty, null, [ex.GetBaseException().Message]);
        }

        var problems = new List<string>(load.Problems);

        if (load.Theme is { } theme && problems.Count == 0)
        {
            try
            {
                var dictionary = Build(theme, problems);

                if (problems.Count == 0)
                {
                    Swap(dictionary);
                    Current = wanted;
                    return new ThemeApplied(wanted, true, []);
                }
            }
            catch (Exception ex)
            {
                problems.Add("не собралась: " + ex.GetBaseException().Message);
            }
        }

        if (problems.Count == 0)
            problems.Add("темы нет в папке themes");

        // Тема не применилась — остаётся встроенная, ближайшая по смыслу:
        // светлая, если просили её или подложка темы светлая, иначе тёмная.
        var fallback = wanted == "light" || load.Theme?.Colors[ThemeSlots.Backdrop].Luminance > 0.5
            ? LightPath
            : DarkPath;

        Swap(new ResourceDictionary { Source = new Uri(fallback, UriKind.Relative) });
        Current = fallback == LightPath ? "light" : DefaultId;

        return new ThemeApplied(wanted, false, problems);
    }

    /// <summary>Словарь с теми же ключами, что в Palette.xaml.</summary>
    internal static ResourceDictionary Build(Theme theme, List<string> problems)
    {
        var dictionary = new ResourceDictionary();

        foreach (var slot in ThemeSlots.All)
        {
            var key = ThemeSlots.ResourceKey(slot);
            var color = ToColor(theme[slot]);

            dictionary[key + "Color"] = color;
            dictionary[key] = Frozen(new SolidColorBrush(color));
        }

        dictionary["UiFont"] = Font(theme, theme.Fonts.Ui);
        dictionary["MonoFont"] = Font(theme, theme.Fonts.Mono);
        dictionary["DisplayFont"] = Font(theme, theme.Fonts.Display);

        var backdrop = (Brush)dictionary["Backdrop"];

        if (theme.Background is { } background)
        {
            var image = Load(background.Image, decodeWidth: 2560);

            // Контраст на картинке: заголовки и меню стоят прямо на ней.
            // Меряется по уменьшенной копии — нужны края яркости, а не детали.
            var range = LuminanceRange(Load(background.Image, decodeWidth: 96), theme[ThemeSlots.Backdrop], background.Dim);

            foreach (var failure in ThemeContrast.Check(theme, range))
                problems.Add("не читается: " + failure);

            dictionary["BackdropImage"] = Frozen(Stretch(new ImageBrush(image), background.Fit, image));
            dictionary["BackdropDim"] = background.Dim;

            // Меню прозрачное: под ним тот же фон, что под содержимым
            // (владелец, 23.09: «давай попробуем и под боковым меню»).
            dictionary["Chrome"] = Frozen(new SolidColorBrush(Colors.Transparent));
        }
        else
        {
            dictionary["BackdropImage"] = Frozen(new SolidColorBrush(Colors.Transparent));
            dictionary["BackdropDim"] = 1.0;
            dictionary["Chrome"] = backdrop;
        }

        return dictionary;
    }

    /// <summary>
    /// Шрифт темы: из её папки, если файлы есть, иначе системный.
    /// </summary>
    /// <remarks>
    /// Файл ищется по имени семейства внутри его папки («./fonts/#Имя»),
    /// а системное имя стоит запасом: не нашёлся файл — будет Segoe UI,
    /// а не квадраты вместо букв.
    /// </remarks>
    private static FontFamily Font(Theme theme, string names)
    {
        if (theme.Fonts.Files.Count == 0)
            return new FontFamily(names);

        var folder = new Uri(theme.Folder.TrimEnd('\\') + "\\");

        var local = theme.Fonts.Files
            .Select(file => Path.GetRelativePath(theme.Folder, Path.GetDirectoryName(file)!).Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(dir => names.Split(',').Select(n => $"./{(dir == "." ? string.Empty : dir + "/")}#{n.Trim()}"));

        return new FontFamily(folder, string.Join(", ", local.Append(names).Append("Segoe UI")));
    }

    private static BitmapImage Load(string path, int decodeWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);

        // Под размер окна, а не целиком: обои 4K в полном размере —
        // сотня мегабайт памяти ради картинки, которую видно в 1080 точек.
        image.DecodePixelWidth = decodeWidth;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();

        return image;
    }

    private static TileBrush Stretch(ImageBrush brush, BackgroundFit fit, BitmapSource image)
    {
        switch (fit)
        {
            case BackgroundFit.Contain:
                brush.Stretch = System.Windows.Media.Stretch.Uniform;
                break;

            case BackgroundFit.Tile:
                brush.Stretch = System.Windows.Media.Stretch.None;
                brush.TileMode = TileMode.Tile;
                brush.ViewportUnits = BrushMappingMode.Absolute;
                brush.Viewport = new Rect(0, 0, image.Width, image.Height);
                break;

            default:
                brush.Stretch = System.Windows.Media.Stretch.UniformToFill;
                break;
        }

        return brush;
    }

    /// <summary>
    /// Яркость самого тёмного и самого светлого места картинки под затемнением.
    /// </summary>
    /// <remarks>
    /// По второму и девяносто восьмому процентилю, а не по крайним точкам:
    /// один белый пиксель в углу не должен забраковать тему, текст над ним
    /// всё равно не стоит.
    /// </remarks>
    internal static (double Darkest, double Lightest) LuminanceRange(BitmapSource image, ThemeColor backdrop, double dim)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var shade = backdrop with { A = (byte)Math.Round(dim * 255) };
        var values = new List<double>(pixels.Length / 4);

        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            var pixel = new ThemeColor(255, pixels[i + 2], pixels[i + 1], pixels[i]);
            values.Add(shade.Over(pixel).Luminance);
        }

        if (values.Count == 0)
            return (backdrop.Luminance, backdrop.Luminance);

        values.Sort();

        return (values[(int)(values.Count * 0.02)], values[Math.Min(values.Count - 1, (int)(values.Count * 0.98))]);
    }

    /// <summary>Подменяет словарь палитры — узнаётся по ключу, а не по месту.</summary>
    private static void Swap(ResourceDictionary dictionary)
    {
        var application = Application.Current;

        if (application is null)
            return;

        var merged = application.Resources.MergedDictionaries;

        // По ключу, а не по индексу: словари могут переставить, и подмена
        // по месту однажды заменила бы стили.
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Contains("BackdropColor"))
            {
                merged[i] = dictionary;
                return;
            }
        }

        merged.Insert(0, dictionary);
    }

    private static Color ToColor(ThemeColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
