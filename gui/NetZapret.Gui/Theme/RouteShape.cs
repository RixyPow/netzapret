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
    /// Раскладка для слоя <paramref name="width"/>×<paramref name="height"/>
    /// с просветом от <paramref name="gapLeft"/> до <paramref name="gapRight"/>.
    /// </summary>
    /// <param name="strip">Высота полоски над и под плашками — в ней лежат «дорожки».</param>
    public static Layout Build(double width, double height, double gapLeft, double gapRight, double strip)
    {
        double gap = Math.Max(gapRight - gapLeft, 1);
        double X(double u) => gapLeft + u * gap;

        // Две дорожки в полоске сверху и две снизу: ближняя к краю и ближняя к плашке.
        double top1 = strip * 0.35, top2 = strip * 0.72;
        double bottom1 = height - top1, bottom2 = height - top2;
        double middle = height / 2;

        double left = -Overhang, right = width + Overhang;

        Point P(double x, double y) => new(x, y);

        // Крест: верхняя уходит вниз, нижняя вверх, перегибы встречаются посреди просвета.
        var down = new[] { P(left, top1), P(X(-0.15), top1), P(X(1.15), bottom2), P(right, bottom2) };
        var up = new[] { P(left, bottom1), P(X(-0.15), bottom1), P(X(1.15), top2), P(right, top2) };

        // Параболы: сверху ныряет до середины и возвращается, снизу — навстречу ей, пунктиром.
        var dip = new[] { P(left, top2), P(X(-0.1), top2), P(X(0.32), height * 0.44), P(X(0.78), top1), P(right, top1) };
        var rise = new[] { P(left, bottom2), P(X(0.22), bottom2), P(X(0.68), height * 0.56), P(X(1.1), bottom1), P(right, bottom1) };

        // Развилка: ствол по середине выходит из-под плашки и делится надвое под кнопку.
        var fork = P(X(0.24), middle);
        var trunk = new[] { P(left, middle), fork };
        var upper = new[] { fork, P(X(1.15), height * 0.3), P(right, height * 0.3) };
        var lower = new[] { fork, P(X(1.15), height * 0.7), P(right, height * 0.7) };

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
