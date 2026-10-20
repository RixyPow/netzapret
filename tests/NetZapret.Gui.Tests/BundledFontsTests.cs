using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetZapret.Core.Themes;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Manrope едет внутри программы и находится по имени из настроек.
/// </summary>
/// <remarks>
/// Шрифт, которого WPF не нашёл, ошибки не даёт: окно молча рисуется
/// запасным. Поэтому проверяется само начертание — что оно наше, нужного
/// веса без подделки жирности и с кириллицей.
/// </remarks>
public sealed class BundledFontsTests
{
    [Theory]
    [InlineData("Manrope")]
    [InlineData("manrope")]
    [InlineData(" Manrope ")]
    public void ManropeIsFoundByName(string name) =>
        Assert.NotNull(BundledFonts.Find(name));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Segoe UI")]
    public void OtherNamesAreLeftToWindows(string? name) =>
        Assert.Null(BundledFonts.Find(name));

    [Fact]
    public void EveryWeightTheWindowUsesIsRealAndSpeaksRussian()
    {
        Sta.Run(() =>
        {
            var family = BundledFonts.Find("Manrope")!;

            // Окно пишет обычным, SemiBold и Bold; Medium и ExtraBold — на будущее.
            foreach (var weight in new[] { FontWeights.Normal, FontWeights.Medium, FontWeights.SemiBold, FontWeights.Bold, FontWeights.ExtraBold })
            {
                var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);

                Assert.True(typeface.TryGetGlyphTypeface(out var glyphs), $"{weight}: шрифт не нашёлся");
                Assert.Equal("Manrope", glyphs.FamilyNames[CultureInfo.GetCultureInfo("en-US")]);
                Assert.Equal(StyleSimulations.None, glyphs.StyleSimulations);
                Assert.Equal(weight, glyphs.Weight);

                foreach (var letter in "Подключено ёЁщЩ")
                {
                    if (letter != ' ')
                        Assert.True(glyphs.CharacterToGlyphMap.ContainsKey(letter), $"{weight}: нет «{letter}»");
                }
            }
        });
    }

    [Fact]
    public void ChosenManropeReachesTheTheme()
    {
        Sta.Run(() =>
        {
            // Тема из папки проекта, а не из установки: из тестов той нет.
            var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "NetZapret.sln")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            var theme = ThemeLoader.Load("dark", System.IO.Path.Combine(directory!.FullName, "themes")).Theme;
            Assert.NotNull(theme);

            var built = NetZapret.Gui.Themes.Build(theme!, [], new Appearance(false, false, 0, "Manrope"));

            foreach (var key in new[] { "UiFont", "MonoFont", "DisplayFont" })
            {
                var font = (FontFamily)built[key];
                Assert.Contains("#Manrope", font.Source);
                Assert.NotNull(font.BaseUri);
            }
        });
    }
}
