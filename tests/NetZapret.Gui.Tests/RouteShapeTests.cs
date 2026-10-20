using System.Windows;
using System.Windows.Media;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Линии на фоне карточки состояния (20.10): видны они только в полосках
/// над и под плашками и в просвете между ними — там и должны лежать.
/// </summary>
public sealed class RouteShapeTests
{
    private const double Width = 876, Height = 127, GapLeft = 290, GapRight = 554, Strip = 11;

    private static IEnumerable<Point> Points(PathGeometry geometry) =>
        geometry.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute).Figures
            .SelectMany(f => f.Segments.OfType<PolyLineSegment>().SelectMany(s => s.Points).Prepend(f.StartPoint));

    [Fact]
    public void OutsideTheGapTheCrossingLinesKeepToTheStrips()
    {
        var layout = RouteShape.Build(Width, Height, GapLeft, GapRight, Strip);
        double bend = (GapRight - GapLeft) * 0.15;

        // Первые четыре — крест и параболы; ствол с ветками живёт посередине и виден только в просвете.
        foreach (var line in layout.Lines.Take(4))
        {
            foreach (var point in Points(line.Geometry).Where(p => p.X < GapLeft - bend - 1 || p.X > GapRight + bend + 1))
                Assert.True(point.Y < Strip || point.Y > Height - Strip, $"({point.X:F0}; {point.Y:F1}) под плашкой");
        }
    }

    [Fact]
    public void LinesCrossInsideTheGap()
    {
        var layout = RouteShape.Build(Width, Height, GapLeft, GapRight, Strip);

        // Крест: у левого края просвета линия «сверху вниз» выше «снизу вверх», у правого — ниже.
        double At(PathGeometry geometry, double x) => Points(geometry).MinBy(p => Math.Abs(p.X - x)).Y;

        var (down, up) = (layout.Lines[0].Geometry, layout.Lines[1].Geometry);

        Assert.True(At(down, GapLeft) < At(up, GapLeft));
        Assert.True(At(down, GapRight) > At(up, GapRight));
        Assert.InRange(layout.Forks.Single().X, GapLeft, GapRight);
    }

    [Fact]
    public void CometsComeFromBehindTheLeftEdgeAndRestBeyondTheRight()
    {
        var layout = RouteShape.Build(Width, Height, GapLeft, GapRight, Strip);

        foreach (var track in layout.Tracks)
        {
            var points = Points(track).ToList();

            Assert.Equal(-RouteShape.Overhang, points[0].X, 3);
            Assert.Equal(Width + RouteShape.Overhang + Width * RouteShape.RunOut, points[^1].X, 3);
        }
    }

    [Fact]
    public void LengthOfAStraightTrackIsItsSpan()
    {
        var straight = RouteShape.Trace([new Point(0, 10), new Point(300, 10)]);

        Assert.Equal(300, RouteShape.Length(straight), 1);
    }

    [Fact]
    public void FadeKeepsTheColourAndSetsItsOpacity()
    {
        var faded = RouteShape.Fade(Color.FromRgb(10, 200, 30), 0.5);

        Assert.Equal(Color.FromArgb(128, 10, 200, 30), faded);
        Assert.Equal(0, RouteShape.Fade(Colors.White, -1).A);
    }
}
