using NetZapret.Core.Themes;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Темы из редактора окна: запись, выгрузка файлом, пересчёт цвета.
/// </summary>
public sealed class ThemeWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-writer-{Guid.NewGuid():N}");

    public ThemeWriterTests() => Directory.CreateDirectory(_root);

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

    private static IReadOnlyDictionary<string, ThemeColor> Dark() => new Dictionary<string, ThemeColor>
    {
        ["backdrop"] = ThemeColor.Parse("#0D1117"),
        ["surface"] = ThemeColor.Parse("#99161B22"),
        ["raised"] = ThemeColor.Parse("#1C2128"),
        ["border"] = ThemeColor.Parse("#30363D"),
        ["text"] = ThemeColor.Parse("#E6EDF3"),
        ["muted"] = ThemeColor.Parse("#8B949E"),
        ["faint"] = ThemeColor.Parse("#6E7681"),
        ["accent"] = ThemeColor.Parse("#3FB950"),
        ["accent-dim"] = ThemeColor.Parse("#2EA043"),
        ["danger"] = ThemeColor.Parse("#F85149"),
        ["warn"] = ThemeColor.Parse("#D29922"),
        ["accent-fill"] = ThemeColor.Parse("#A01EEC"),
        ["on-accent"] = ThemeColor.Parse("#FFFFFF"),
        ["warn-fill"] = ThemeColor.Parse("#D29922"),
    };

    /// <summary>Записанная тема читается тем же загрузчиком — и такой же.</summary>
    [Fact]
    public void ASavedThemeLoadsBackTheSame()
    {
        var image = Path.Combine(_root, "wall.jpg");
        File.WriteAllBytes(image, new byte[32]);

        var id = ThemeWriter.Save(new ThemeDraft
        {
            Name = "Фиолетовая ночь",
            Colors = Dark(),
            Font = "Bahnschrift",
            BackgroundSource = image,
            Fit = BackgroundFit.Contain,
            Dim = 0.65,
            Blur = 30,
        }, root: _root);

        var load = ThemeLoader.Load(id, _root);

        Assert.True(load.Ok, string.Join("; ", load.Problems));

        var theme = load.Theme!;
        Assert.Equal("Фиолетовая ночь", theme.Name);
        Assert.Equal("#A01EEC", theme[ThemeSlots.AccentFill].ToString());
        Assert.Equal("#99161B22", theme[ThemeSlots.Surface].ToString());
        Assert.Equal("Bahnschrift", theme.Fonts.Display);
        Assert.Equal("Bahnschrift", theme.Fonts.Ui);
        Assert.Equal(BackgroundFit.Contain, theme.Background!.Fit);
        Assert.Equal(0.65, theme.Background.Dim);
        Assert.EndsWith("background.jpg", theme.Background.Image);
    }

    /// <summary>Встроенная тема не перезаписывается: правка уходит новой.</summary>
    [Fact]
    public void AShippedThemeIsNeverOverwritten()
    {
        var id = ThemeWriter.Save(new ThemeDraft { Name = "Тёмная", Colors = Dark() }, id: "dark", root: _root);

        Assert.NotEqual("dark", id);
        Assert.False(Directory.Exists(Path.Combine(_root, "dark")));
    }

    [Fact]
    public void TheSameNameGetsANewFolder()
    {
        var first = ThemeWriter.Save(new ThemeDraft { Name = "Моя", Colors = Dark() }, root: _root);
        var second = ThemeWriter.Save(new ThemeDraft { Name = "Моя", Colors = Dark() }, root: _root);

        Assert.NotEqual(first, second);
    }

    /// <summary>Выгруженная файлом тема загружается обратно — с картинкой.</summary>
    [Fact]
    public void AThemeTravelsAsAFile()
    {
        var image = Path.Combine(_root, "wall.png");
        File.WriteAllBytes(image, new byte[16]);

        var id = ThemeWriter.Save(new ThemeDraft { Name = "Дорожная", Colors = Dark(), BackgroundSource = image }, root: _root);

        var zip = Path.Combine(_root, "road.zip");
        ThemeWriter.Export(id, zip, _root);

        var other = Path.Combine(_root, "other");
        var imported = ThemeWriter.Import(zip, other);
        var load = ThemeLoader.Load(imported, other);

        Assert.True(load.Ok, string.Join("; ", load.Problems));
        Assert.Equal("Дорожная", load.Theme!.Name);
        Assert.NotNull(load.Theme.Background);
    }

    [Fact]
    public void AFileWithoutAThemeIsRefused()
    {
        var zip = Path.Combine(_root, "junk.zip");

        using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt");

        Assert.Throws<InvalidDataException>(() => ThemeWriter.Import(zip, Path.Combine(_root, "t")));
    }

    /// <summary>Цвет с того снимка владельца: #a01eec — H 278, S 84, L 52.</summary>
    [Fact]
    public void ColorModelsAgree()
    {
        var color = ThemeColor.Parse("#A01EEC");

        var (h, s, l) = ColorMath.ToHsl(color);
        Assert.Equal(278, Math.Round(h));
        Assert.Equal(84, Math.Round(s * 100));
        Assert.Equal(52, Math.Round(l * 100));

        Assert.Equal(color, ColorMath.FromHsl(h, s, l));

        var (hv, sv, v) = ColorMath.ToHsv(color);
        Assert.Equal(color, ColorMath.FromHsv(hv, sv, v));
    }

    /// <summary>
    /// Случай владельца 24.09: «Фон окна» красный #DC0000 — пояснения,
    /// «работает» и «закрыто» на нём не читались, и тема не применилась.
    /// «Поправить нечитаемое» сдвигает их светлоту, фон не трогает.
    /// </summary>
    [Fact]
    public void UnreadableColorsAreFixedAndTheChosenBackgroundStays()
    {
        var colors = new Dictionary<string, ThemeColor>(Dark()) { ["backdrop"] = ThemeColor.Parse("#DC0000") };

        Assert.Contains("muted", ThemeFixer.Failing(colors).Keys);

        var fixedColors = ThemeFixer.Fix(colors);

        Assert.Equal("#DC0000", fixedColors["backdrop"].ToString());
        Assert.Empty(ThemeFixer.Failing(fixedColors));
        Assert.Equal(colors["raised"], fixedColors["raised"]);
    }

    [Fact]
    public void TheButtonCaptionPicksWhatReads()
    {
        Assert.Equal("#FFFFFF", ColorMath.ReadableOn(ThemeColor.Parse("#1A7F37")).ToString());
        Assert.Equal("#0A0A0A", ColorMath.ReadableOn(ThemeColor.Parse("#F2D335")).ToString());
    }
}
