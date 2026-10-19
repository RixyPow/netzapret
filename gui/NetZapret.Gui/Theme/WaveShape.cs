using System.Windows;
using System.Windows.Media;

namespace NetZapret.Gui;

/// <summary>
/// Кривая для волны на фоне карточки состояния «Главной» (владелец 19.10).
/// </summary>
public static class WaveShape
{
    /// <summary>
    /// Волна шириной в два цикла: сдвиг на <paramref name="width"/> возвращает её в начало.
    /// </summary>
    /// <remarks>
    /// Сумма двух синусов: основной — один период на ширину, и второй вдвое чаще
    /// и слабее, чтобы волна не выглядела учебной синусоидой. Оба периода делят
    /// ширину нацело — поэтому круг и бесшовный. Высоты — долями высоты слоя:
    /// <paramref name="level"/> — средняя линия, <paramref name="swell"/> и
    /// <paramref name="ripple"/> — размахи.
    /// </remarks>
    /// <param name="closed">Замкнуть вниз до края — для заливки под кривой.</param>
    public static Geometry Curve(double width, double height, double level, double swell, double ripple, double phase, bool closed)
    {
        const int steps = 240;

        Point At(double x) => new(x, height * (level
            - swell * Math.Sin(2 * Math.PI * x / width + phase)
            - ripple * Math.Sin(4 * Math.PI * x / width + 2 * phase)));

        var points = new List<Point>(steps + 2);

        for (int i = 1; i <= steps; i++)
            points.Add(At(2 * width * i / steps));

        if (closed)
        {
            points.Add(new Point(2 * width, height));
            points.Add(new Point(0, height));
        }

        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(At(0), isFilled: closed, isClosed: closed);
            context.PolyLineTo(points, isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>Тот же цвет с непрозрачностью <paramref name="opacity"/> (0–1).</summary>
    public static Color Fade(Color colour, double opacity) =>
        Color.FromArgb((byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255), colour.R, colour.G, colour.B);
}
