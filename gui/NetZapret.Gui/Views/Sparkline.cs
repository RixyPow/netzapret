using System.Windows;
using System.Windows.Media;

namespace NetZapret.Gui.Views;

/// <summary>
/// Кривая показаний под цифрой замера: как шла скорость, а не только чем кончилась.
/// </summary>
/// <remarks>
/// Макет владельца, 30.09: «графики и блоки хорошо выглядят». Итог «156 Мбит/с»
/// не отличает ровный поток от пилы между 20 и 300 — кривая отличает.
/// Рисуется сама (<see cref="OnRender"/>), без Polyline: точек полсотни,
/// приходят они семь раз в секунду, и пересобирать под них дерево элементов незачем.
/// </remarks>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <summary>
    /// Во сколько раз верх рамки выше наибольшего показания.
    /// </summary>
    /// <remarks>
    /// У скорости — единица: кривая упирается в свой пик. У задержки показания
    /// почти равны, и при единице выходила не кривая, а закрашенная плашка
    /// во всю высоту (снимок 30.09) — ей нужен воздух сверху.
    /// </remarks>
    public double Headroom { get; set; } = 1;

    private IReadOnlyList<double> _values = [];

    public void Show(IReadOnlyList<double> values)
    {
        _values = values;
        InvalidateVisual();
    }

    /// <summary>
    /// Точки кривой в рамке: слева направо, наибольшее показание — у верхнего края.
    /// </summary>
    /// <remarks>
    /// Меньше двух показаний — пусто: одна точка не кривая.
    /// </remarks>
    public static IReadOnlyList<Point> Points(
        IReadOnlyList<double> values, double width, double height, double headroom = 1)
    {
        if (values.Count < 2 || width <= 0 || height <= 0)
            return [];

        double top = values.Max() * Math.Max(1, headroom);
        var points = new List<Point>(values.Count);

        for (int i = 0; i < values.Count; i++)
        {
            double share = top > 0 ? Math.Max(0, values[i]) / top : 0;

            // Пиксель сверху и снизу — под толщину линии.
            points.Add(new Point(width * i / (values.Count - 1), height - 1 - share * (height - 2)));
        }

        return points;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (Stroke is not { } stroke)
            return;

        var points = Points(_values, ActualWidth, ActualHeight, Headroom);

        if (points.Count == 0)
        {
            // Замера ещё не было — черта на месте кривой, чтобы блок не зиял.
            var rest = new Pen(stroke, 1) { DashStyle = new DashStyle([2, 4], 0) };

            drawingContext.PushOpacity(0.35);
            drawingContext.DrawLine(rest, new Point(0, ActualHeight - 1), new Point(ActualWidth, ActualHeight - 1));
            drawingContext.Pop();

            return;
        }

        var line = new StreamGeometry();

        using (var context = line.Open())
        {
            context.BeginFigure(points[0], isFilled: false, isClosed: false);
            context.PolyLineTo(points.Skip(1).ToList(), isStroked: true, isSmoothJoin: true);
        }

        var area = new StreamGeometry();

        using (var context = area.Open())
        {
            context.BeginFigure(new Point(points[0].X, ActualHeight), isFilled: true, isClosed: true);
            context.PolyLineTo(points.ToList(), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(points[^1].X, ActualHeight), isStroked: false, isSmoothJoin: false);
        }

        drawingContext.PushOpacity(0.16);
        drawingContext.DrawGeometry(stroke, null, area);
        drawingContext.Pop();

        drawingContext.DrawGeometry(null, new Pen(stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}
