using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Кривая показаний под цифрой замера скорости (30.09).
/// </summary>
public sealed class SparklineTests
{
    [Fact]
    public void TheCurveSpansTheFrame()
    {
        var points = Sparkline.Points([0, 50, 100], 200, 34);

        Assert.Equal(3, points.Count);
        Assert.Equal(0, points[0].X);
        Assert.Equal(100, points[1].X);
        Assert.Equal(200, points[2].X);
    }

    /// <summary>Пик — у верхнего края, ноль — у нижнего; по пикселю под толщину линии.</summary>
    [Fact]
    public void ThePeakTouchesTheTop()
    {
        var points = Sparkline.Points([0, 100], 200, 34);

        Assert.Equal(33, points[0].Y);
        Assert.Equal(1, points[1].Y);
    }

    /// <summary>
    /// Задержке — воздух сверху: почти равные показания без него давали
    /// закрашенную плашку во всю высоту.
    /// </summary>
    [Fact]
    public void HeadroomLowersAFlatCurve()
    {
        var points = Sparkline.Points([25, 25, 25], 200, 34, headroom: 2);

        Assert.All(points, p => Assert.Equal(17, p.Y));
    }

    [Fact]
    public void OneReadingIsNotACurve()
    {
        Assert.Empty(Sparkline.Points([], 200, 34));
        Assert.Empty(Sparkline.Points([42], 200, 34));
        Assert.Empty(Sparkline.Points([1, 2], 0, 34));
    }

    /// <summary>Одни нули — линия по низу, а не деление на ноль.</summary>
    [Fact]
    public void ZerosLieOnTheBottom()
    {
        var points = Sparkline.Points([0, 0, 0], 200, 34);

        Assert.All(points, p => Assert.Equal(33, p.Y));
    }
}
