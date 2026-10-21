using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Эмблема (21.10): пути svg разбираются в квадрат 256, значок .ico несёт
/// все размеры, и цвета темы ложатся каждый на свою часть.
/// </summary>
public sealed class EmblemTests
{
    [Fact]
    public void ThePathsFillTheSquare()
    {
        Sta.Run(() =>
        {
            var all = new[] { Emblem.Top, Emblem.Band, Emblem.Bottom }
                .Select(g => g.Bounds)
                .Aggregate(Rect.Union);

            Assert.Equal(0, all.Left, 1);
            Assert.Equal(0, all.Top, 1);
            Assert.Equal(Emblem.Side, all.Right, 1);
            Assert.Equal(Emblem.Side, all.Bottom, 1);
        });
    }

    [Fact]
    public void TheIconCarriesEverySizeAndTheChosenColors()
    {
        Sta.Run(() =>
        {
            var (top, band, bottom) = (Color.FromRgb(0x85, 0xB2, 0x67), Colors.Black, Colors.White);
            var ico = Emblem.Ico(top, band, bottom);

            var decoder = new IconBitmapDecoder(new MemoryStream(ico), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(Emblem.IconSizes.OrderBy(s => s), decoder.Frames.Select(f => f.PixelWidth).OrderBy(s => s));

            // Трей и заголовок окна берут малые размеры через System.Drawing — они там есть как есть.
            foreach (var size in new[] { 16, 20, 32, 40 })
            {
                using var icon = new System.Drawing.Icon(new MemoryStream(ico), size, size);
                Assert.Equal(size, icon.Width);
            }

            // Точки внутри частей на 256: верх слева сверху, кривая на её подъёме, низ справа снизу.
            var big = decoder.Frames.Single(f => f.PixelWidth == 256);
            Assert.Equal(top, At(big, 30, 60));
            Assert.Equal(band, At(big, 70, 120));
            Assert.Equal(bottom, At(big, 200, 230));

            // Угол скруглён — прозрачен.
            Assert.Equal(0, At(big, 1, 1).A);
        });
    }

    private static Color At(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);

        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
