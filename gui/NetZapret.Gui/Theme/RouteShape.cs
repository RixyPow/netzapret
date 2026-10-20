using System.Windows;
using System.Windows.Media;

namespace NetZapret.Gui;

/// <summary>
/// Линии на фоне карточки состояния «Главной» (владелец 20.10: «параболы,
/// перегибы, разделения линий, пересечения»).
/// </summary>
/// <remarks>
/// <para>
/// Плашки карточки непрозрачные, и фон виден только в просвете между плашкой
/// «Работает» и кнопкой, в полосках над и под плашками и у краёв. Поэтому
/// линии — как дорожки на плате: идут полосками вдоль плашек, а всё
/// интересное делают в просвете — меняются полосками крест-накрест,
/// ныряют параболой и возвращаются, средняя раздваивается и уходит под кнопку.
/// </para>
/// <para>
/// Каждый отрезок — кубическая кривая с горизонтальными касательными на
/// концах: между полосками выходит S с перегибом посередине, а туда-обратно —
/// дуга с вершиной в средней точке, похожая на параболу.
/// </para>
/// </remarks>
public static class RouteShape
{
    /// <summary>Линия фона: сама кривая, насколько она заметна (0–1) и пунктир ли.</summary>
    public sealed record Line(PathGeometry Geometry, double Strength, bool Dashed);

    /// <summary>Линии, пути огоньков от края до края и точки развилок.</summary>
    public sealed record Layout(IReadOnlyList<Line> Lines, IReadOnlyList<PathGeometry> Tracks, IReadOnlyList<Point> Forks);

    /// <summary>Насколько линии выходят за края слоя: огонёк появляется из-за края, а не на нём.</summary>
    public const double Overhang = 40;

    /// <summary>
    /// Насколько далеко (долей просвета) от своего места уходят перегибы при
    /// движении. Края изгибов лежат в пределах <c>0,15 + Sway</c> просвета от его
    /// краёв — дальше линии идут полосками ровно.
    /// </summary>
    public const double Sway = 0.08;

    /// <summary>
    /// Раскладка для слоя <paramref name="width"/>×<paramref name="height"/>
    /// с просветом от <paramref name="gapLeft"/> до <paramref name="gapRight"/>
    /// в мгновение <paramref name="time"/> (секунды).
    /// </summary>
    /// <remarks>
    /// Линии «дышат» (владелец 20.10: «сделай так, чтобы эти траектории тоже
    /// двигались немного»): перегибы креста ходят вдоль просвета, вершины
    /// парабол — вдоль и вглубь, развилка и концы веток — понемногу. Каждое —
    /// своей синусоидой с периодом 11–19 с и своим сдвигом фазы, чтобы линии
    /// не качались хором. Дорожки в полосках вдоль плашек стоят: сдвинься они —
    /// линия заехала бы под плашку и пропала.
    /// </remarks>
    /// <param name="strip">Высота полоски над и под плашками — в ней лежат «дорожки».</param>
    public static Layout Build(double width, double height, double gapLeft, double gapRight, double strip, double time = 0)
    {
        double gap = Math.Max(gapRight - gapLeft, 1);
        double X(double u) => gapLeft + u * gap;

        // Синусоида с периодом в секундах и сдвигом фазы.
        double S(double period, double phase) => Math.Sin(2 * Math.PI * time / period + phase);

        // Две дорожки в полоске сверху и две снизу: ближняя к краю и ближняя к плашке.
        double top1 = strip * 0.35, top2 = strip * 0.72;
        double bottom1 = height - top1, bottom2 = height - top2;
        double middle = height / 2;

        double left = -Overhang, right = width + Overhang;

        Point P(double x, double y) => new(x, y);

        // Крест: верхняя уходит вниз, нижняя вверх, перегибы встречаются посреди
        // просвета — и место встречи гуляет, потому что концы изгибов ходят врозь.
        var down = new[]
        {
            P(left, top1), P(X(-0.15 + Sway * S(13, 0)), top1),
            P(X(1.15 + Sway * S(17, 1.3)), bottom2), P(right, bottom2),
        };
        var up = new[]
        {
            P(left, bottom1), P(X(-0.15 + Sway * S(15, 2.1)), bottom1),
            P(X(1.15 + Sway * S(11, 0.4)), top2), P(right, top2),
        };

        // Параболы: сверху ныряет до середины и возвращается, снизу — навстречу ей, пунктиром.
        var dip = new[]
        {
            P(left, top2), P(X(-0.1 + Sway * S(19, 0.7)), top2),
            P(X(0.32 + 0.1 * S(14, 2.5)), height * (0.44 + 0.08 * S(12, 0.9))),
            P(X(0.78 + 0.06 * S(16, 1.7)), top1), P(right, top1),
        };
        var rise = new[]
        {
            P(left, bottom2), P(X(0.22 + 0.06 * S(18, 3.0)), bottom2),
            P(X(0.68 + 0.1 * S(13, 4.2)), height * (0.56 - 0.08 * S(15, 2.8))),
            P(X(1.1 + Sway * S(12, 5.1)), bottom1), P(right, bottom1),
        };

        // Развилка: ствол по середине выходит из-под плашки и делится надвое под кнопку.
        var fork = P(X(0.24 + 0.06 * S(17, 3.6)), middle + height * 0.05 * S(11, 1.1));
        var trunk = new[] { P(left, middle), fork };
        var upper = new[] { fork, P(X(1.15), height * (0.3 + 0.06 * S(14, 0.2))), P(right, height * 0.3) };
        var lower = new[] { fork, P(X(1.15), height * (0.7 + 0.06 * S(19, 4.4))), P(right, height * 0.7) };

        var lines = new List<Line>
        {
            new(Trace(down), 0.42, false),
            new(Trace(up), 0.42, false),
            new(Trace(dip), 0.3, false),
            new(Trace(rise), 0.34, true),
            new(Trace(trunk), 0.36, false),
            new(Trace(upper), 0.3, false),
            new(Trace(lower), 0.3, false),
        };

        // Пути огоньков уходят за правый край ещё на RunOut ширины: там огонёк
        // «отдыхает» до следующего выхода, и на карточке их сразу не все.
        Point[] Run(Point[] points) => [.. points, P(right + width * RunOut, points[^1].Y)];

        var tracks = new List<PathGeometry>
        {
            Trace(Run(down)),
            Trace(Run(up)),
            Trace(Run(dip)),
            Trace(Run(rise)),
            Trace(Run([.. trunk, .. upper[1..]])),
            Trace(Run([.. trunk, .. lower[1..]])),
        };

        return new Layout(lines, tracks, [fork]);
    }

    /// <summary>Сколько ширин слоя путь огонька тянется за правым краем.</summary>
    public const double RunOut = 0.6;

    /// <summary>Длина пути — для времени огонька при постоянной скорости.</summary>
    public static double Length(PathGeometry geometry)
    {
        double length = 0;

        foreach (var figure in geometry.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute).Figures)
        {
            var at = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                IEnumerable<Point> points = segment switch
                {
                    PolyLineSegment poly => poly.Points,
                    LineSegment line => [line.Point],
                    _ => [],
                };

                foreach (var point in points)
                {
                    length += (point - at).Length;
                    at = point;
                }
            }
        }

        return length;
    }

    /// <summary>
    /// Кривая через точки: между соседними — кубическая с горизонтальными касательными.
    /// </summary>
    public static PathGeometry Trace(IReadOnlyList<Point> points)
    {
        // Без IsFilled = false: спрямление (GetFlattenedPathGeometry — длина пути,
        // ход огонька) такие фигуры выбрасывает, и путь выходил пустым. Заливки
        // у линий нет и так — Fill у Path не задан.
        var figure = new PathFigure { StartPoint = points[0] };

        for (int i = 1; i < points.Count; i++)
        {
            var (from, to) = (points[i - 1], points[i]);
            double half = (to.X - from.X) / 2;

            figure.Segments.Add(new BezierSegment(
                new Point(from.X + half, from.Y), new Point(to.X - half, to.Y), to, isStroked: true));
        }

        var geometry = new PathGeometry { Figures = { figure } };
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Тот же цвет с непрозрачностью <paramref name="opacity"/> (0–1).</summary>
    public static Color Fade(Color colour, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), colour.R, colour.G, colour.B);
}
