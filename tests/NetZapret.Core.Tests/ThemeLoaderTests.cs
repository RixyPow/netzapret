using System.Text.RegularExpressions;
using NetZapret.Core.Themes;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Темы из папки: договор, строгость к чужому файлу и читаемость своих.
/// </summary>
public sealed class ThemeLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-themes-{Guid.NewGuid():N}");

    public ThemeLoaderTests()
    {
        Directory.CreateDirectory(_root);

        // Основы берутся из репозитория — те самые файлы, что уедут в поставку.
        if (Repository() is { } repo)
        {
            foreach (var id in ThemeLoader.Shipped)
                Copy(Path.Combine(repo, "themes", id), Path.Combine(_root, id));
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ThemeLoad Write(string id, string json, params (string Name, int Bytes)[] files)
    {
        var folder = Path.Combine(_root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ThemeLoader.FileName), json);

        foreach (var (name, bytes) in files)
            File.WriteAllBytes(Path.Combine(folder, name), new byte[bytes]);

        return ThemeLoader.Load(id, _root);
    }

    /// <summary>Свои темы проходят ту же проверку, что потребуем с художников.</summary>
    [Fact]
    public void BuiltInThemesLoadAndReadWell()
    {
        foreach (var id in ThemeLoader.Shipped)
        {
            var load = ThemeLoader.Load(id, _root);

            Assert.True(load.Ok, $"{id}: {string.Join("; ", load.Problems)}");
            Assert.Equal(ThemeSlots.All.Count, load.Theme!.Colors.Count);
        }
    }

    /// <summary>
    /// Файлы тем и XAML-палитры — одни значения. XAML остаётся запасной
    /// палитрой на случай, если папки тем нет, и разойтись они не должны.
    /// </summary>
    [Fact]
    public void ThemeFilesMatchXamlPalettes()
    {
        if (Repository() is not { } repo)
            return;

        foreach (var (id, xaml) in new[] { ("dark", "Palette.xaml"), ("light", "Light.xaml") })
        {
            var text = File.ReadAllText(Path.Combine(repo, "gui", "NetZapret.Gui", "Theme", xaml));
            var theme = ThemeLoader.Load(id, _root).Theme!;

            foreach (var slot in ThemeSlots.All)
            {
                var key = ThemeSlots.ResourceKey(slot) + "Color";
                var match = Regex.Match(text, $"<Color x:Key=\"{key}\">#FF([0-9A-Fa-f]{{6}})</Color>");

                Assert.True(match.Success, $"{xaml}: нет {key}");
                Assert.Equal("#" + match.Groups[1].Value.ToUpperInvariant(), theme[slot].ToString());
            }
        }
    }

    [Fact]
    public void AllThemesAreListedBuiltInFirst()
    {
        Write("zzz", """{ "name": "Альфа", "base": "dark" }""");

        var all = ThemeLoader.LoadAll(_root);

        Assert.Equal(["dark", "light", "grey", "tinted", "violet", "ocean", "coffee", "rose", "cream", "sloyka1", "blissfield", "zzz"], all.Select(t => t.Id));
    }

    /// <summary>Основа даёт недостающее: три своих цвета, остальное — от тёмной.</summary>
    [Fact]
    public void ABaseFillsTheRest()
    {
        var load = Write("nirvana", """
            {
              "name": "Nirvana",
              "base": "dark",
              "colors": { "backdrop": "#000000", "accent": "#F2D335", "accent-fill": "#F2D335" },
              "fonts": { "display": "Bahnschrift Condensed" }
            }
            """);

        Assert.True(load.Ok, string.Join("; ", load.Problems));

        var theme = load.Theme!;
        Assert.Equal("#F2D335", theme[ThemeSlots.Accent].ToString());
        Assert.Equal("#161B22", theme[ThemeSlots.Surface].ToString());
        Assert.Equal("Bahnschrift Condensed", theme.Fonts.Display);
        Assert.Equal("Bahnschrift, Segoe UI", theme.Fonts.Ui);
    }

    /// <summary>Опечатка в имени слота — ошибка, а не молча пропущенный цвет.</summary>
    [Fact]
    public void AnUnknownSlotIsAProblem()
    {
        var load = Write("typo", """{ "base": "dark", "colors": { "acent": "#FF0000" } }""");

        Assert.False(load.Ok);
        Assert.Contains(load.Problems, p => p.Contains("acent"));
    }

    [Fact]
    public void WithoutBaseEverySlotIsRequired()
    {
        var load = Write("bare", """{ "colors": { "text": "#FFFFFF" } }""");

        Assert.Null(load.Theme);
        Assert.Contains(load.Problems, p => p.Contains("backdrop"));
    }

    /// <summary>Файлы — только внутри папки темы: ни ссылок, ни путей, ни «..».</summary>
    [Theory]
    [InlineData("https://example.com/bg.jpg")]
    [InlineData(@"C:\Windows\bg.jpg")]
    [InlineData("../dark/bg.jpg")]
    [InlineData("bg.gif")]
    [InlineData("missing.png")]
    public void ABackgroundOutsideTheFolderIsRefused(string image)
    {
        var load = Write("escape", $$"""{ "base": "dark", "background": { "image": {{System.Text.Json.JsonSerializer.Serialize(image)}} } }""");

        Assert.False(load.Ok);
        Assert.Null(load.Theme!.Background);
    }

    [Fact]
    public void ABackgroundInsideTheFolderIsTaken()
    {
        var load = Write("bg", """
            { "base": "dark", "background": { "image": "wall.jpg", "fit": "tile", "dim": 0.7, "blur": 30 } }
            """, ("wall.jpg", 16));

        Assert.True(load.Ok, string.Join("; ", load.Problems));

        var background = load.Theme!.Background!;
        Assert.Equal(BackgroundFit.Tile, background.Fit);
        Assert.Equal(0.7, background.Dim);
        Assert.Equal(30, background.Blur);
        Assert.EndsWith("wall.jpg", background.Image);
        Assert.False(background.IsVideo);
    }

    /// <summary>
    /// Фоном может быть видео mp4 (07.10, «именно как фон эта гифка должна
    /// быть»), а GIF — нет: фоном он съел бы сотни мегабайт памяти.
    /// </summary>
    [Fact]
    public void AVideoBackgroundIsTakenAndAGifIsNot()
    {
        var video = Write("video", """
            { "base": "dark", "background": { "image": "fish.mp4", "dim": 0.7 } }
            """, ("fish.mp4", 16));

        Assert.True(video.Ok, string.Join("; ", video.Problems));
        Assert.True(video.Theme!.Background!.IsVideo);
        Assert.EndsWith("fish.mp4", video.Theme.Background.Image);

        var gif = Write("gif", """
            { "base": "dark", "background": { "image": "fish.gif" } }
            """, ("fish.gif", 16));

        Assert.False(gif.Ok);
        Assert.Contains(gif.Problems, p => p.Contains("fish.gif"));
    }

    /// <summary>
    /// Размер кадра читается из заголовка mp4: у звуковой дорожки он нулевой,
    /// у видео — настоящий; коробка mdat перед moov не мешает.
    /// </summary>
    [Fact]
    public void TheFrameSizeIsReadFromTheMp4Header()
    {
        static byte[] Box(string type, params byte[][] content)
        {
            var body = content.SelectMany(c => c).ToArray();
            var size = 8 + body.Length;
            return [(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size,
                .. System.Text.Encoding.ASCII.GetBytes(type), .. body];
        }

        static byte[] Tkhd(int width, int height)
        {
            // Версия 0: ширина и высота — с 76-го байта тела, в формате 16.16.
            var body = new byte[84];
            body[76] = (byte)(width >> 8);
            body[77] = (byte)width;
            body[80] = (byte)(height >> 8);
            body[81] = (byte)height;
            return Box("tkhd", body);
        }

        var file = Box("ftyp", new byte[8])
            .Concat(Box("mdat", new byte[100]))
            .Concat(Box("moov", Box("mvhd", new byte[100]), Box("trak", Tkhd(0, 0)), Box("trak", Tkhd(132, 74))))
            .ToArray();

        Assert.Equal((132, 74), VideoFile.FrameSize(new MemoryStream(file)));
        Assert.Null(VideoFile.FrameSize(new MemoryStream(new byte[40])));
    }

    /// <summary>
    /// Чёрные полосы по бокам кадра находятся и обрезаются, а тёмное
    /// изображение целиком полосой не считается (08.10, fish.mp4).
    /// </summary>
    [Fact]
    public void BlackBarsAreFoundButADarkFrameIsKept()
    {
        static byte[] Frame(int width, int height, Func<int, int, byte> value)
        {
            var pixels = new byte[width * height * 4];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width + x) * 4;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = value(x, y);
                    pixels[i + 3] = 255;
                }
            }

            return pixels;
        }

        // 100×50: полосы по 12 точек слева и справа, белое поле с серой рыбкой.
        var bars = Frame(100, 50, (x, y) => x < 12 || x >= 88 ? (byte)0 : x is > 40 and < 60 && y is > 20 and < 30 ? (byte)90 : (byte)250);
        var (left, top, width, height) = Letterbox.Content(bars, 100, 50, 400);

        Assert.Equal(13, left);
        Assert.Equal(0, top);
        Assert.Equal(74, width);
        Assert.Equal(50, height);

        // Ночной кадр — тёмный целиком, но не полоса: остаётся как есть.
        var night = Frame(100, 50, (x, y) => (byte)((x + y) % 10 == 0 ? 120 : 10));
        Assert.Equal((0, 0, 100, 50), Letterbox.Content(night, 100, 50, 400));

        // Совсем чёрный — тоже не режется в ноль.
        Assert.Equal((0, 0, 100, 50), Letterbox.Content(Frame(100, 50, (_, _) => 0), 100, 50, 400));
    }

    /// <summary>Нечитаемая тема не проходит: пара названа с числом.</summary>
    [Fact]
    public void AnUnreadableThemeNamesThePair()
    {
        var load = Write("unreadable", """{ "base": "dark", "colors": { "text": "#20252B" } }""");

        Assert.False(load.Ok);
        Assert.Contains(load.Problems, p => p.Contains("text на backdrop"));
    }

    [Fact]
    public void TextMustBeOpaque()
    {
        var load = Write("glass", """{ "base": "dark", "colors": { "text": "#80FFFFFF", "surface": "#99161B22" } }""");

        Assert.Contains(load.Problems, p => p.Contains("«text» должен быть непрозрачным"));
        Assert.DoesNotContain(load.Problems, p => p.Contains("«surface»"));
    }

    /// <summary>
    /// С картинкой фона текст сверяется с её краями: светлая картинка под
    /// слабым затемнением съедает светлый текст.
    /// </summary>
    [Fact]
    public void TextIsCheckedAgainstTheBrightestPartOfTheBackground()
    {
        var dark = ThemeLoader.Load("dark", _root).Theme!;

        Assert.Empty(ThemeContrast.Check(dark, (0.0, 0.02)));
        Assert.Contains(ThemeContrast.Check(dark, (0.0, 0.8)), f => f.Background == "фон-картинка");
    }

    [Theory]
    [InlineData("accent-fill", "AccentFill")]
    [InlineData("backdrop", "Backdrop")]
    [InlineData("on-accent", "OnAccent")]
    public void SlotNamesMapToResourceKeys(string slot, string key) =>
        Assert.Equal(key, ThemeSlots.ResourceKey(slot));

    [Theory]
    [InlineData("#0D1117", 255, 0x0D, 0x11, 0x17)]
    [InlineData("#800D1117", 0x80, 0x0D, 0x11, 0x17)]
    public void ColorsParse(string text, int a, int r, int g, int b) =>
        Assert.Equal(new ThemeColor((byte)a, (byte)r, (byte)g, (byte)b), ThemeColor.Parse(text));

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
    }

    private static string? Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
