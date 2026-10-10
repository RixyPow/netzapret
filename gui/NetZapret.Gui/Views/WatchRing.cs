using System.Windows;
using System.Windows.Media;

namespace NetZapret.Gui.Views;

/// <summary>
/// Кольцо долей «Куда» в «Наблюдении»: туннель, десинк, напрямую, локально, движок.
/// </summary>
/// <remarks>
/// Рисуется само (<see cref="OnRender"/>) и только по <see cref="Show"/>:
/// раздел зовёт его раз в секунду, а не на каждое соединение. Кольцо
/// вместо полосы — по слову владельца 10.10 («правая панель пустоватая,
/// я бы сделал всё равно кольцо»).
/// </remarks>
public sealed class WatchRing : FrameworkElement
{
    /// <summary>Толщина кольца.</summary>
    private const double Thickness = 16;

    /// <summary>Зазор между долями, в градусах: соседние цвета не сливаются.</summary>
    private const double Gap = 2.5;

    private IReadOnlyList<(double Value, Brush Brush)> _parts = [];
    private Brush _empty = Brushes.Gray;

    /// <summary>Доли по порядку и кисть пустого кольца.</summary>
    public void Show(IReadOnlyList<(double Value, Brush Brush)> parts, Brush empty)
    {
        _parts = parts;
        _empty = empty;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double size = Math.Min(ActualWidth, ActualHeight);

        if (size <= Thickness * 2)
            return;

        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double radius = (size - Thickness) / 2;

        var empty = new Pen(_empty, Thickness);
        drawingContext.DrawEllipse(null, empty, centre, radius, radius);

        double total = _parts.Sum(p => p.Value);

        if (total <= 0)
            return;

        int shown = _parts.Count(p => p.Value > 0);
        double start = -90;

        foreach (var (value, brush) in _parts)
        {
            if (value <= 0)
                continue;

            double sweep = 360 * value / total;
            double gap = shown > 1 ? Math.Min(Gap, sweep / 3) : 0;

            DrawArc(drawingContext, new Pen(brush, Thickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat },
                centre, radius, start + gap / 2, sweep - gap);

            start += sweep;
        }
    }

    private static void DrawArc(DrawingContext context, Pen pen, Point centre, double radius, double from, double sweep)
    {
        if (sweep >= 359.9)
        {
            context.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }

        static Point At(Point c, double r, double degrees)
        {
            double a = degrees * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }

        var geometry = new StreamGeometry();

        using (var g = geometry.Open())
        {
            g.BeginFigure(At(centre, radius, from), isFilled: false, isClosed: false);
            g.ArcTo(At(centre, radius, from + sweep), new Size(radius, radius), 0,
                isLargeArc: sweep > 180, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();
        context.DrawGeometry(null, pen, geometry);
    }
}
