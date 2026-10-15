using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NetZapret.Core.Themes;

namespace NetZapret.Gui;

/// <summary>
/// Картинка темы на «Главной», уложенная руками на миниатюре в редакторе тем.
/// </summary>
/// <remarks>
/// Владелец 15.10: «сделай миниатюру главной и дай возможность разместить там
/// фон, всё остальное заливается градиентом». Середина и высота — доли
/// страницы (<see cref="SideArtLayout.Place"/>); что картинка не закрывает —
/// градиент (<see cref="Ground"/>), край картинки в него растворяется
/// (<see cref="Feather"/>). Одно и то же рисует «Главная» и миниатюра.
/// </remarks>
public sealed record HomePlacement(double X, double Y, double Size)
{
    /// <summary>Ключ ресурса темы с местом картинки.</summary>
    public const string Key = "HomePlacement";

    /// <summary>Ключ ресурса темы с градиентом под картинкой.</summary>
    public const string GroundKey = "HomeGround";

    /// <summary>Доля края картинки, растворяемая в градиент, — с каждой стороны.</summary>
    private const double Edge = 0.1;

    /// <summary>
    /// Градиент из цвета фона окна в средний цвет картинки — сверху слева вниз направо.
    /// </summary>
    /// <remarks>
    /// Средний цвет, а не один выбранный: с ним градиент подходит к любой
    /// картинке без настройки, и край картинки, растворяясь, не даёт шва.
    /// </remarks>
    public static Brush Ground(BitmapSource? sample, ThemeColor backdrop)
    {
        var dark = Color.FromRgb(backdrop.R, backdrop.G, backdrop.B);
        var tone = sample is null ? dark : Average(sample);

        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1),
            GradientStops =
            {
                new GradientStop(dark, 0),
                new GradientStop(Mix(dark, tone, 0.55), 0.55),
                new GradientStop(tone, 1),
            },
        };
    }

    /// <summary>Маска, растворяющая края картинки со всех четырёх сторон.</summary>
    /// <remarks>
    /// Две полосы прозрачности друг на друге — по ширине и по высоте: маска
    /// у фигуры одна, а маска внутри картинки-маски перемножает их.
    /// </remarks>
    public static Brush Feather(Rect place)
    {
        static LinearGradientBrush Strip(Point from, Point to) => new()
        {
            StartPoint = from,
            EndPoint = to,
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, Edge),
                new GradientStop(Colors.Black, 1 - Edge),
                new GradientStop(Colors.Transparent, 1),
            },
        };

        var shape = new Rectangle
        {
            Width = 100,
            Height = 100,
            Fill = Strip(new Point(0, 0), new Point(1, 0)),
            OpacityMask = Strip(new Point(0, 0), new Point(0, 1)),
        };

        return new VisualBrush(shape)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = place,
        };
    }

    private static Color Average(BitmapSource sample)
    {
        var converted = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        long r = 0, g = 0, b = 0, n = 0;

        for (int i = 0; i + 3 < pixels.Length; i += 4, n++)
        {
            b += pixels[i];
            g += pixels[i + 1];
            r += pixels[i + 2];
        }

        return n == 0 ? Colors.Black : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t));
}
