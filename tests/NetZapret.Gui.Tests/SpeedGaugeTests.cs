using System.Windows;
using NetZapret.Gui.Views;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Шкала замера скорости (30.09): неравномерная, как у Speedtest.
/// </summary>
public sealed class SpeedGaugeTests
{
    /// <summary>Отметки стоят через равные доли дуги.</summary>
    [Fact]
    public void MarksAreEvenlySpaced()
    {
        for (int i = 0; i < SpeedGauge.Marks.Count; i++)
            Assert.Equal((double)i / (SpeedGauge.Marks.Count - 1), SpeedGauge.Fraction(SpeedGauge.Marks[i]), 9);
    }

    /// <summary>
    /// Ради чего шкала неравномерная: 2 и 20 Мбит/с должны различаться
    /// на глаз, а на равномерной до гигабита между ними 2 % дуги.
    /// </summary>
    [Fact]
    public void SlowSpeedsAreToldApart()
    {
        Assert.True(SpeedGauge.Fraction(20) - SpeedGauge.Fraction(2) > 0.2);
    }

    [Fact]
    public void BetweenMarksTheScaleIsLinear()
    {
        // 75 — середина между 50 и 100, третьей и четвёртой отметками из семи промежутков.
        Assert.Equal(3.5 / 7, SpeedGauge.Fraction(75), 9);
    }

    [Fact]
    public void TheScaleHasEnds()
    {
        Assert.Equal(0, SpeedGauge.Fraction(0));
        Assert.Equal(0, SpeedGauge.Fraction(-5));
        Assert.Equal(0, SpeedGauge.Fraction(double.NaN));
        Assert.Equal(1, SpeedGauge.Fraction(5000));
    }

    [Fact]
    public void TheNeedleSweepsThreeQuarters()
    {
        Assert.Equal(-135, SpeedGauge.Angle(0));
        Assert.Equal(135, SpeedGauge.Angle(1000));
        Assert.Equal(0, SpeedGauge.Angle(75), 9);
    }

    /// <summary>Ноль градусов — вверх, по часовой: так считаются и дуга, и подписи.</summary>
    [Fact]
    public void AnglesRunClockwiseFromTheTop()
    {
        var centre = new Point(100, 100);

        Assert.Equal(new Point(100, 50), Round(SpeedGauge.At(centre, 50, 0)));
        Assert.Equal(new Point(150, 100), Round(SpeedGauge.At(centre, 50, 90)));
        Assert.Equal(new Point(50, 100), Round(SpeedGauge.At(centre, 50, -90)));

        static Point Round(Point p) => new(Math.Round(p.X, 6), Math.Round(p.Y, 6));
    }
}
