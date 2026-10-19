using System.Windows;
using System.Windows.Media;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Волна на фоне карточки состояния (19.10): кривая в два цикла, и сдвиг
/// на ширину возвращает её в начало — круг без шва.
/// </summary>
public sealed class WaveShapeTests
{
    [Fact]
    public void TheCurveSpansTwoCyclesAndStartsWhereItsSecondCycleStarts()
    {
        var line = PathGeometry.CreateFromGeometry(WaveShape.Curve(600, 120, 0.68, 0.13, 0.04, 0.7, closed: false));
        var points = line.Figures[0].Segments.OfType<PolyLineSegment>().SelectMany(s => s.Points)
            .Prepend(line.Figures[0].StartPoint).ToList();

        Assert.Equal(0, points[0].X, 3);
        Assert.Equal(1200, points[^1].X, 3);

        // Точка через цикл стоит на той же высоте: сдвиг на ширину незаметен.
        var atWidth = points.MinBy(p => Math.Abs(p.X - 600));
        Assert.Equal(points[0].Y, atWidth.Y, 1);
        Assert.Equal(points[0].Y, points[^1].Y, 1);
    }

    [Fact]
    public void TheFillReachesTheBottom()
    {
        var bounds = WaveShape.Curve(600, 120, 0.68, 0.13, 0.04, 0, closed: true).Bounds;

        Assert.Equal(120, bounds.Bottom, 3);
        Assert.Equal(1200, bounds.Width, 3);
    }

    [Fact]
    public void FadeKeepsTheColourAndSetsItsOpacity()
    {
        var faded = WaveShape.Fade(Color.FromRgb(10, 200, 30), 0.5);

        Assert.Equal(Color.FromArgb(128, 10, 200, 30), faded);
        Assert.Equal(0, WaveShape.Fade(Colors.White, -1).A);
    }
}
