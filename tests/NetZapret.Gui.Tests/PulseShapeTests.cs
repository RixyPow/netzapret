using System.Windows;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Шестиугольник волн вокруг кольца «Главной» (20.10): вершиной вверх
/// и не шире √3·R — так волна дальше от слов справа от кольца.
/// </summary>
public sealed class PulseShapeTests
{
    [Fact]
    public void TheHexagonStandsOnItsCornerAndKeepsItsRadius()
    {
        var centre = new Point(50, 50);
        var corners = PulseShape.Corners(centre, 37);

        Assert.Equal(6, corners.Length);
        Assert.Equal(50, corners[0].X, 6);
        Assert.Equal(13, corners[0].Y, 6);

        foreach (var corner in corners)
            Assert.Equal(37, (corner - centre).Length, 6);

        var bounds = PulseShape.Hexagon(centre, 37).Bounds;

        Assert.Equal(74, bounds.Height, 3);
        Assert.Equal(37 * Math.Sqrt(3), bounds.Width, 3);
    }

    [Fact]
    public void NodesSitOnTheCorners()
    {
        var bounds = PulseShape.Nodes(new Point(50, 50), 37, 2).Bounds;

        Assert.Equal(13 - 2, bounds.Top, 3);
        Assert.Equal(87 + 2, bounds.Bottom, 3);
    }
}
