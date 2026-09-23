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
    /// Стекло: есть при фоне и размытии, с плотностью карточки из темы,
    /// пропадает вместе с картинкой, а нечитаемое — называется.
    /// </summary>
    [Fact]
    public void GlassFollowsTheSettings()
    {
        if (Themes() is not { } repo)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"netzapret-glass-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dark"));
            File.Copy(Path.Combine(repo, "dark", "theme.json"), Path.Combine(root, "dark", "theme.json"));
            Directory.CreateDirectory(Path.Combine(root, "glass"));

            Sta.Run(() =>
            {
                // Полосы чёрного и белого под затемнением 0.92: заголовки
                // читаются, а тёмное стекло поверх — тем более.
                const int size = 64;
                var pixels = new byte[size * size * 4];

                for (int i = 0; i < size * size; i++)
                {
                    byte v = (i / size) % 16 < 8 ? (byte)0 : (byte)255;
                    pixels[i * 4] = pixels[i * 4 + 1] = pixels[i * 4 + 2] = v;
                    pixels[i * 4 + 3] = 255;
                }

                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(
                    BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4)));

                using (var file = File.Create(Path.Combine(root, "glass", "bg.png")))
                    encoder.Save(file);

                File.WriteAllText(Path.Combine(root, "glass", "theme.json"), """
                    { "base": "dark", "colors": { "surface": "#33161B22" },
                      "background": { "image": "bg.png", "dim": 0.92, "blur": 12 } }
                    """);

                var theme = ThemeLoader.Load("glass", root).Theme!;

                var problems = new List<string>();
                var glass = NetZapret.Gui.Themes.Build(theme, problems, new Appearance(true, true, 0));

                Assert.Empty(problems);
                Assert.NotNull(glass["GlassImage"]);
                Assert.Equal(0x33, ((SolidColorBrush)glass["RailFill"]).Color.A);

                var plain = NetZapret.Gui.Themes.Build(theme, [], new Appearance(false, true, 0));

                Assert.Null(plain["GlassImage"]);
                Assert.Equal(1.0, plain["BackdropDim"]);

                // Светлое стекло под светлый текст: уплотнение его только
                // белит, и тема обязана это назвать, а не молчать.
                File.WriteAllText(Path.Combine(root, "glass", "theme.json"), """
                    { "base": "dark", "colors": { "surface": "#40FFFFFF" },
                      "background": { "image": "bg.png", "dim": 0.92, "blur": 12 } }
                    """);

                var pale = new List<string>();
                NetZapret.Gui.Themes.Build(ThemeLoader.Load("glass", root).Theme!, pale, new Appearance(true, true, 0));

                Assert.Contains(pale, p => p.Contains("стекле"));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
