using System.Windows;
using System.Windows.Media;

namespace NetZapret.Gui;

/// <summary>
/// Шестиугольник и узлы в его вершинах — для волн вокруг кольца состояния «Главной» (владелец 20.10).
/// </summary>
/// <remarks>
/// Вершиной вверх: так фигура уже по ширине (√3·R против 2R) и дальше
/// от слов справа от кольца.
/// </remarks>
public static class PulseShape
{
    /// <summary>Вершины шестиугольника с центром <paramref name="centre"/> и радиусом описанной окружности <paramref name="radius"/>.</summary>
    public static Point[] Corners(Point centre, double radius)
    {
        var corners = new Point[6];

        for (int i = 0; i < 6; i++)
        {
            double angle = Math.PI / 3 * i - Math.PI / 2;
            corners[i] = new Point(centre.X + radius * Math.Cos(angle), centre.Y + radius * Math.Sin(angle));
        }

        return corners;
    }

    /// <summary>Контур шестиугольника, без заливки.</summary>
    public static Geometry Hexagon(Point centre, double radius)
    {
        var corners = Corners(centre, radius);
        var outline = new StreamGeometry();

        using (var context = outline.Open())
        {
            context.BeginFigure(corners[0], isFilled: false, isClosed: true);
            context.PolyLineTo(corners[1..], isStroked: true, isSmoothJoin: true);
        }

        outline.Freeze();
        return outline;
    }

    /// <summary>Узлы радиусом <paramref name="dot"/> в вершинах.</summary>
    public static Geometry Nodes(Point centre, double radius, double dot)
    {
        var group = new GeometryGroup();

        foreach (var corner in Corners(centre, radius))
            group.Children.Add(new EllipseGeometry(corner, dot, dot));

        group.Freeze();
        return group;
    }
}
