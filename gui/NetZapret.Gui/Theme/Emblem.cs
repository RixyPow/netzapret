using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetZapret.Gui;

/// <summary>
/// Эмблема NetZapret — «туннельный эффект»: кривая барьера в скруглённом
/// квадрате (владелец 21.10, svg 256×256).
/// </summary>
/// <remarks>
/// <para>
/// Три части — верх над кривой, сама кривая и низ под ней, — и у каждой
/// свой цвет темы (<see cref="Core.Themes.ThemeSlots.Logo"/>): владелец
/// показал листом вариантов зелёный/серый/белый, зелёный/чёрный/белый и
/// чёрный с белой кривой — «пусть в каждой теме можно будет настроить
/// 3 цвета». Пути — из svg дословно.
/// </para>
/// <para>
/// Кривая рисуется первой и с обводкой в пиксель, верх и низ — поверх неё.
/// Иначе в стыке двух заливок край каждой сглаживается к прозрачному, и на
/// 16–20 точках вдоль кривой просвечивал бы светлый шов. Всё обрезано
/// скруглённым квадратом — обводка за его край не выходит.
/// </para>
/// </remarks>
public static class Emblem
{
    /// <summary>Сторона эмблемы в точках svg.</summary>
    public const double Side = 256;

    public static Geometry Top { get; } = Parse(
        "M255.99,27.63v121.74c-84.22-.01-137.17-69.9-159.78-99.75l-.55-.72c-5.41-7.15-14.76-10.02-23.25-7.17"
        + "-8.51,2.86-14.23,10.81-14.23,19.79v123.71H0V27.63C0,12.37,12.37,0,27.62,0h200.75c15.25,0,27.62,12.37,27.62,27.63Z");

    public static Geometry Band { get; } = Parse(
        "M255.99,149.37v30.14c-81.42,0-137.42-54.66-167.67-91.15v127.01H0v-30.14h58.18V61.52c0-8.98,5.72-16.93,14.23-19.79"
        + ",8.49-2.85,17.84.02,23.25,7.17l.55.72c22.61,29.85,75.56,99.74,159.78,99.75Z");

    public static Geometry Bottom { get; } = Parse(
        "M255.99,179.51v48.86c0,15.26-12.37,27.63-27.62,27.63H27.62c-15.25,0-27.62-12.37-27.62-27.63v-13h88.32"
        + "v-127.01c30.25,36.49,86.25,91.15,167.67,91.15Z");

    /// <summary>Скруглённый квадрат — очертание эмблемы, по нему обрезается обводка кривой.</summary>
    public static Geometry Outline { get; } = Freeze(new RectangleGeometry(new Rect(0, 0, Side, Side), 27.62, 27.62));

    /// <summary>Размеры значка: к обычным семи — 20 и 40, их берут трей, заголовок и панель задач при 125 %.</summary>
    public static IReadOnlyList<int> IconSizes { get; } = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>Эмблема заданных цветов в <paramref name="pixels"/> точках.</summary>
    /// <param name="pixels">Сторона, в которую ляжет рисунок: обводка кривой — в один её пиксель.</param>
    public static Drawing Drawing(Color top, Color band, Color bottom, double pixels = Side)
    {
        var bandBrush = Freeze(new SolidColorBrush(band));
        var seam = Freeze(new Pen(bandBrush, Side / Math.Max(pixels, 1)) { LineJoin = PenLineJoin.Round });

        var group = new DrawingGroup { ClipGeometry = Outline };
        group.Children.Add(new GeometryDrawing(bandBrush, seam, Band));
        group.Children.Add(new GeometryDrawing(Freeze(new SolidColorBrush(top)), null, Top));
        group.Children.Add(new GeometryDrawing(Freeze(new SolidColorBrush(bottom)), null, Bottom));
        group.Freeze();

        return group;
    }

    /// <summary>Цвета эмблемы текущей темы окна (ресурсы приложения).</summary>
    public static (Color Top, Color Band, Color Bottom) Current() =>
        (Pick("LogoTopColor", Color.FromRgb(0x85, 0xB2, 0x67)),
         Pick("LogoBandColor", Colors.Black),
         Pick("LogoBottomColor", Colors.White));

    /// <summary>Эмблема картинкой <paramref name="size"/>×<paramref name="size"/>, с прозрачными углами.</summary>
    public static BitmapSource Render(int size, Color top, Color band, Color bottom)
    {
        var visual = new DrawingVisual();

        using (var context = visual.RenderOpen())
        {
            context.PushTransform(new ScaleTransform(size / Side, size / Side));
            context.DrawDrawing(Drawing(top, band, bottom, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        return bitmap;
    }

    /// <summary>
    /// Значок .ico со всеми <see cref="IconSizes"/>: каждый размер нарисован
    /// заново, а не уменьшен из большого.
    /// </summary>
    /// <remarks>
    /// До 48 — 32-битной картинкой BMP, от 64 — PNG: так их читают
    /// и Windows (значок файла, трей, заголовок), и System.Drawing.Icon,
    /// а файл весит 36 КБ вместо 116 со всеми размерами в BMP.
    /// Пиксели в .ico — без предумножения альфы, а WPF рисует с ним:
    /// цвета полупрозрачного края возвращаются делением на альфу, иначе
    /// край кривой на светлом фоне темнел бы.
    /// </remarks>
    public static byte[] Ico(Color top, Color band, Color bottom)
    {
        var images = IconSizes.Select(size => (size, Data: Entry(Render(size, top, band, bottom)))).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)images.Count);

        int offset = 6 + 16 * images.Count;

        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in images)
            writer.Write(data);

        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] Entry(BitmapSource image)
    {
        int size = image.PixelWidth;

        if (size >= 64)
        {
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using var buffer = new MemoryStream();
            png.Save(buffer);
            return buffer.ToArray();
        }

        int stride = size * 4;
        var pixels = new byte[stride * size];
        image.CopyPixels(pixels, stride, 0);

        // Из предумноженной альфы — в обычную.
        for (int i = 0; i < pixels.Length; i += 4)
        {
            int alpha = pixels[i + 3];

            if (alpha is > 0 and < 255)
            {
                for (int c = 0; c < 3; c++)
                    pixels[i + c] = (byte)Math.Min(255, pixels[i + c] * 255 / alpha);
            }
        }

        int maskStride = (size + 31) / 32 * 4;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        // BITMAPINFOHEADER: высота вдвое — картинка и маска под ней.
        writer.Write(40);
        writer.Write(size);
        writer.Write(size * 2);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(0);
        writer.Write(stride * size + maskStride * size);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);
        writer.Write(0);

        // Строки снизу вверх, как у BMP.
        for (int row = size - 1; row >= 0; row--)
            writer.Write(pixels, row * stride, stride);

        // Маска пустая: прозрачность несёт альфа.
        writer.Write(new byte[maskStride * size]);

        writer.Flush();
        return stream.ToArray();
    }

    // Через TryFindResource: словари темы — вложенные (MergedDictionaries), и
    // поиск по одному верхнему словарю их не видит.
    private static Color Pick(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) is Color color ? color : fallback;

    private static Geometry Parse(string data) => Freeze(Geometry.Parse(data));

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
