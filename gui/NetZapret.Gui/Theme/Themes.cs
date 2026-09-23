using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetZapret.Core;
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
    // Полным адресом ресурса, а не относительным путём: относительный
    // находится только из самой программы, а из тестов и чужих сборок —
    // нет (найдено 24.09 тестом окон редактора).
    private const string DarkPath = "pack://application:,,,/NetZapret;component/Theme/Palette.xaml";
    private const string LightPath = "pack://application:,,,/NetZapret;component/Theme/Light.xaml";

    public const string DefaultId = "dark";

    /// <summary>Применённая сейчас тема.</summary>
    public static string Current { get; private set; } = DefaultId;

    /// <summary>
    /// Применяет тему по имени папки; при отказе — встроенную из XAML.
    /// </summary>
    public static ThemeApplied Apply(string? id, AppSettings? settings = null)
    {
        var wanted = string.IsNullOrWhiteSpace(id) ? DefaultId : id.Trim();
        var look = Appearance.From(settings ?? AppSettings.Load(AppSettings.DefaultPath));

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
                var dictionary = Build(theme, problems, look);

                if (problems.Count == 0)
                {
                    Swap(dictionary);
                    Glass.Set(dictionary["GlassImage"] as ImageSource, System.Windows.Media.Stretch.UniformToFill);
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

        var builtIn = new ResourceDictionary { Source = new Uri(fallback, UriKind.Absolute) };

        // Масштаб — настройка человека, а не темы: переживает и отказ темы.
        builtIn["UiScaleTransform"] = Scale(look.Scale);

        Swap(builtIn);
        Glass.Set(null, System.Windows.Media.Stretch.UniformToFill);
        Current = fallback == LightPath ? "light" : DefaultId;

        return new ThemeApplied(wanted, false, problems);
    }

    /// <summary>
    /// Показывает тему, которой ещё нет на диске, — для редактора.
    /// </summary>
    /// <remarks>
    /// Правка в редакторе видна сразу (владелец, 24.09: «изменения должны
    /// сразу применяться»). Нечитаемую не показываем: окно редактора красится
    /// теми же ресурсами, и нечитаемая тема сделала бы нечитаемым и его —
    /// вместе с кнопкой, которой её чинить. Остаётся последняя читаемая,
    /// а причина возвращается словами.
    /// </remarks>
    public static IReadOnlyList<string> Preview(Theme theme, AppSettings? settings = null)
    {
        var problems = ThemeContrast.Check(theme).Select(f => "не читается: " + f).ToList();

        if (problems.Count > 0)
            return problems;

        try
        {
            var dictionary = Build(theme, problems, Appearance.From(settings ?? AppSettings.Load(AppSettings.DefaultPath)));

            if (problems.Count == 0)
            {
                Swap(dictionary);
                Glass.Set(dictionary["GlassImage"] as ImageSource, System.Windows.Media.Stretch.UniformToFill);
            }
        }
        catch (Exception ex)
        {
            problems.Add("не собралась: " + ex.GetBaseException().Message);
        }

        return problems;
    }

    /// <summary>Словарь с теми же ключами, что в Palette.xaml.</summary>
    internal static ResourceDictionary Build(Theme theme, List<string> problems, Appearance? appearance = null)
    {
        var look = appearance ?? Appearance.Default;
        var dictionary = new ResourceDictionary();

        foreach (var slot in ThemeSlots.All)
        {
            var key = ThemeSlots.ResourceKey(slot);
            var color = ToColor(theme[slot]);

            dictionary[key + "Color"] = color;
            dictionary[key] = Frozen(new SolidColorBrush(color));
        }

        // Шрифт человека — один на всю программу (владелец, 24.09: «лучше
        // было бы, если бы на всю программу был один шрифт»): интерфейс,
        // заголовки и данные. «Как в теме» — шрифты темы, как задумано.
        if (look.Font is { } font)
        {
            var family = new FontFamily(font);
            dictionary["UiFont"] = family;
            dictionary["MonoFont"] = family;
            dictionary["DisplayFont"] = family;
        }
        else
        {
            dictionary["UiFont"] = Font(theme, theme.Fonts.Ui);
            dictionary["MonoFont"] = Font(theme, theme.Fonts.Mono);
            dictionary["DisplayFont"] = Font(theme, theme.Fonts.Display);
        }
        dictionary["UiScaleTransform"] = Scale(look.Scale);

        var backdrop = (Brush)dictionary["Backdrop"];

        dictionary["GlassImage"] = null;

        if (theme.Background is { } background && look.Background)
        {
            // Настройка «темнее» прибавляет к задуманному, но не убавляет:
            // затемнение — то, чем тема проходит проверку читаемости.
            double dim = Math.Min(0.95, background.Dim + Appearance.DimStep * look.DimSteps);

            var image = Load(background.Image, decodeWidth: 2560);

            // Контраст на картинке: заголовки и меню стоят прямо на ней.
            // Меряется по уменьшенной копии — нужны края яркости, а не детали.
            var range = LuminanceRange(Load(background.Image, decodeWidth: 96), theme[ThemeSlots.Backdrop], dim);

            foreach (var failure in ThemeContrast.Check(theme, range))
                problems.Add("не читается: " + failure);

            dictionary["BackdropImage"] = Frozen(Stretch(new ImageBrush(image), background.Fit, image));
            dictionary["BackdropDim"] = dim;

            // Разделы прозрачные: под ними тот же фон, что под всем окном
            // (владелец, 23.09: «давай попробуем и под боковым меню»).
            dictionary["Chrome"] = Frozen(new SolidColorBrush(Colors.Transparent));

            var surface = theme[ThemeSlots.Surface];

            if (look.Blur && background.Blur > 0)
            {
                var (glass, alpha, reads) = RenderGlass(image, theme, dim, background.Blur);

                // Уплотнение помогает, только когда оттенок стекла тёмный
                // под светлый текст (или наоборот). Светлое стекло под светлый
                // текст от уплотнения только хуже — такое называется прямо,
                // а не остаётся молча нечитаемым.
                if (!reads)
                    problems.Add("не читается: текст на стекле под карточками — оттенок surface слишком близок к тексту; "
                        + "выключите размытие или возьмите surface темнее");

                dictionary["GlassImage"] = glass;

                // Пока стекло не легло (элемент ещё не в окне), меню —
                // плашкой той же плотности, что стекло.
                dictionary["RailFill"] = Frozen(new SolidColorBrush(ToColor(surface with { A = (byte)Math.Round(alpha * 255) })));
            }
            else
            {
                // Подложка меню без размытия (владелец, 23.09: «шрифт
                // становится слишком незаметен, стоит сделать подложку»).
                // Не прозрачнее карточек и не меньше 85 %: пункты меню —
                // тонкий текст, и картинка под ним рябит.
                var plate = surface with { A = Math.Max(surface.A, (byte)217) };
                dictionary["RailFill"] = Frozen(new SolidColorBrush(ToColor(plate)));

                // Полупрозрачные карточки стоят прямо на картинке: текст
                // на них сверяется с её краями под затемнением.
                if (!surface.Opaque)
                    CheckOver(theme, surface, range, "карточка над картинкой", problems);
            }
        }
        else
        {
            dictionary["BackdropImage"] = Frozen(new SolidColorBrush(Colors.Transparent));
            dictionary["BackdropDim"] = 1.0;
            dictionary["Chrome"] = backdrop;
            dictionary["RailFill"] = backdrop;
        }

        return dictionary;
    }

    /// <summary>
    /// Размытая копия фона под затемнением, с оттенком карточки поверх.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Считается один раз при применении темы; карточки берут из неё кусок
    /// под собой (<see cref="Glass"/>). Размывать на лету при каждой
    /// перерисовке WPF не тянет.
    /// </para>
    /// <para>
    /// Плотность оттенка подбирается проверкой, а не на глаз: начинается
    /// с прозрачности карточки из темы и растёт, пока текст, пояснения
    /// и цвета состояния не станут читаться на самом светлом и самом тёмном
    /// месте стекла. Стекло, на котором не прочесть подпись, хуже
    /// непрозрачной карточки.
    /// </para>
    /// </remarks>
    private static (BitmapSource Glass, double Alpha, bool Reads) RenderGlass(BitmapSource image, Theme theme, double dim, double blur)
    {
        var surface = theme[ThemeSlots.Surface];
        double start = surface.Opaque ? 0.7 : surface.A / 255.0;

        BitmapSource glass = image;
        double alpha = start;
        bool reads = false;

        foreach (var candidate in new[] { start, 0.8, 0.88, 0.95 }.Where(a => a >= start))
        {
            alpha = candidate;
            glass = RenderLayer(image, theme, dim, blur, surface with { A = (byte)Math.Round(alpha * 255) });

            var range = LuminanceRange(new TransformedBitmap(glass, new ScaleTransform(0.1, 0.1)), theme[ThemeSlots.Backdrop], 0);

            if (GlassReads(theme, range))
            {
                reads = true;
                break;
            }
        }

        return (glass, alpha, reads);
    }

    private static bool GlassReads(Theme theme, (double Darkest, double Lightest) range)
    {
        foreach (var slot in new[] { ThemeSlots.Text, ThemeSlots.Muted, ThemeSlots.Accent, ThemeSlots.Danger, ThemeSlots.Warn })
        {
            double l = theme[slot].Luminance;

            if (Math.Min(ThemeContrast.Ratio(l, range.Darkest), ThemeContrast.Ratio(l, range.Lightest)) < ThemeContrast.Text)
                return false;
        }

        return true;
    }

    private static BitmapSource RenderLayer(BitmapSource image, Theme theme, double dim, double blur, ThemeColor tint)
    {
        const double width = 1280;
        double height = Math.Round(width * image.PixelHeight / Math.Max(1, image.PixelWidth));

        var backdrop = new SolidColorBrush(ToColor(theme[ThemeSlots.Backdrop]));

        var root = new System.Windows.Controls.Grid { Width = width, Height = height, Background = backdrop };

        // Картинка шире холста на радиус: края размытия иначе тянут
        // к прозрачному и дают тёмную рамку по краю окна.
        root.Children.Add(new System.Windows.Controls.Image
        {
            Source = image,
            Stretch = System.Windows.Media.Stretch.Fill,
            Margin = new Thickness(-blur),
            Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = blur,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality,
            },
        });

        root.Children.Add(new System.Windows.Shapes.Rectangle { Fill = backdrop, Opacity = dim });
        root.Children.Add(new System.Windows.Shapes.Rectangle { Fill = new SolidColorBrush(ToColor(tint)) });

        var size = new Size(width, height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();

        return bitmap;
    }

    /// <summary>
    /// Полупрозрачный слой над картинкой: читается ли на нём текст.
    /// </summary>
    /// <remarks>
    /// Смешение считается в линейной яркости — приближение, но с запасом
    /// в пользу строгости не ошибается настолько, чтобы пропустить
    /// нечитаемое: крайние участки берутся по процентилям, а не средние.
    /// </remarks>
    private static void CheckOver(Theme theme, ThemeColor layer, (double Darkest, double Lightest) range, string what, List<string> problems)
    {
        double a = layer.A / 255.0;
        double ls = (layer with { A = 255 }).Luminance;
        var under = (Darkest: a * ls + (1 - a) * range.Darkest, Lightest: a * ls + (1 - a) * range.Lightest);

        foreach (var slot in new[] { ThemeSlots.Text, ThemeSlots.Muted })
        {
            double l = theme[slot].Luminance;
            double worst = Math.Min(ThemeContrast.Ratio(l, under.Darkest), ThemeContrast.Ratio(l, under.Lightest));

            if (worst < ThemeContrast.Text)
                problems.Add($"не читается: {slot} на слое «{what}»: {worst:0.00}:1, нужно 4.5:1 — сделайте surface плотнее");
        }
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

    private static ScaleTransform Scale(double scale) => Frozen(new ScaleTransform(scale, scale));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}

/// <summary>
/// Что человек хочет видеть из задуманного темой: фон, стекло, затемнение.
/// </summary>
public sealed record Appearance(bool Background, bool Blur, int DimSteps,
    string? Font = null, double Scale = 1.0)
{
    /// <summary>Ступени масштаба окна — как в Telegram.</summary>
    public static IReadOnlyList<double> Scales { get; } = [0.9, 1.0, 1.1, 1.25, 1.5];

    /// <summary>На сколько темнее за одну ступень «темнее».</summary>
    public const double DimStep = 0.12;

    public static Appearance Default { get; } = new(true, true, 0);

    public static Appearance From(AppSettings settings) =>
        new(settings.ThemeBackgroundShown, settings.ThemeBlur, Math.Clamp(settings.ThemeDimSteps, 0, 2),
            Blank(settings.UiFont), Math.Clamp(settings.UiScale, 0.8, 1.75));

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
