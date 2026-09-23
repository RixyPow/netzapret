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

        Assert.Equal(["dark", "light", "grey", "tinted", "zzz"], all.Select(t => t.Id));
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
