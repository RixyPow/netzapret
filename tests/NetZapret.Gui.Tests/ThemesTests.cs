using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetZapret.Core.Themes;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Тема из файла собирается в словарь с теми же ключами, что Palette.xaml.
/// </summary>
/// <remarks>
/// Стили берут кисти и шрифты по ключам. Ключ, которого нет в собранном
/// словаре, в окне молча остаётся от прежней темы — и тема выходит смесью
/// двух, чего по виду не понять.
/// </remarks>
public sealed class ThemesTests
{
    private static string? Themes()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var folder = Path.Combine(directory.FullName, "themes");

            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")) && Directory.Exists(folder))
                return folder;

            directory = directory.Parent;
        }

        return null;
    }

    [Fact]
    public void ABuiltThemeHasEveryKeyOfTheXamlPalette()
    {
        if (Themes() is not { } root)
            return;

        Sta.Run(() =>
        {
            var palette = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/NetZapret;component/Theme/Palette.xaml", UriKind.Absolute),
            };

            foreach (var id in ThemeLoader.BuiltIn)
            {
                var theme = ThemeLoader.Load(id, root).Theme!;
                var problems = new List<string>();

                var built = NetZapret.Gui.Themes.Build(theme, problems);

                Assert.Empty(problems);

                foreach (var key in palette.Keys.Cast<object>())
                    Assert.True(built.Contains(key), $"{id}: нет ключа {key}");
            }
        });
    }

    /// <summary>
    /// Край яркости картинки считается под затемнением: чёрная картинка
    /// с белым пятном в два процента не должна объявляться белой.
    /// </summary>
    [Fact]
    public void TheBackgroundRangeIgnoresASpeck()
    {
        Sta.Run(() =>
        {
            const int size = 100;
            var pixels = new byte[size * size * 4];

            // Пятно в один процент площади — белое; остальное чёрное.
            for (int i = 0; i < size * size; i++)
            {
                byte v = i < size ? (byte)255 : (byte)0;
                pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
                pixels[i * 4 + 3] = 255;
            }

            var image = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            var (darkest, lightest) = NetZapret.Gui.Themes.LuminanceRange(image, ThemeColor.Parse("#000000"), 0.5);

            Assert.Equal(0, darkest, 3);
            Assert.Equal(0, lightest, 3);
        });
    }
}
