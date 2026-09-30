using System.Windows;

namespace NetZapret.Gui.Views;

/// <summary>
/// Шкала замера скорости: куда на дуге встаёт стрелка.
/// </summary>
/// <remarks>
/// <para>
/// Шкала неравномерная, как у Speedtest, с которого владелец просил взять вид
/// (30.09): отметки 0, 5, 10, 50, 100, 250, 500 и 1000 Мбит/с стоят через
/// равные промежутки. На равномерной шкале до гигабита туннель в 20 Мбит/с
/// и туннель в 2 Мбит/с — одно и то же положение стрелки у нуля, а различать
/// надо как раз их.
/// </para>
/// <para>
/// Дуга — три четверти круга: от −135° до +135°, ноль градусов — вверх.
/// </para>
/// </remarks>
public static class SpeedGauge
{
    /// <summary>Отметки шкалы, Мбит/с, через равные доли дуги.</summary>
    public static readonly IReadOnlyList<double> Marks = [0, 5, 10, 50, 100, 250, 500, 1000];

    public const double StartAngle = -135;

    public const double Sweep = 270;

    /// <summary>Доля шкалы, 0–1; за последней отметкой — единица.</summary>
    public static double Fraction(double mbps)
    {
        if (double.IsNaN(mbps) || mbps <= 0)
            return 0;

        for (int i = 1; i < Marks.Count; i++)
        {
            if (mbps <= Marks[i])
            {
                double within = (mbps - Marks[i - 1]) / (Marks[i] - Marks[i - 1]);
                return (i - 1 + within) / (Marks.Count - 1);
            }
        }

        return 1;
    }

    /// <summary>Угол стрелки в градусах: 0 — вверх, по часовой.</summary>
    public static double Angle(double mbps) => StartAngle + Sweep * Fraction(mbps);

    /// <summary>Точка на окружности под этим углом.</summary>
    public static Point At(Point centre, double radius, double angle)
    {
        double radians = angle * Math.PI / 180;

        return new Point(centre.X + radius * Math.Sin(radians), centre.Y - radius * Math.Cos(radians));
    }
}
