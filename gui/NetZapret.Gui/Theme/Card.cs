using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetZapret.Gui;

/// <summary>
/// Карточка: Border, который в светлых темах рисует под собой тень.
/// </summary>
/// <remarks>
/// <para>
/// Светлые темы проигрывали тёмным (владелец 27.10: «как-то они сильно
/// проигрывают на фоне тёмных тем»): белая карточка на почти белом фоне
/// держалась на одной серой рамке, и окно выглядело чертежом. На тёмном
/// глубину даёт сама подложка — карточка светлее фона, — а на светлом её
/// дают только тени.
/// </para>
/// <para>
/// Не DropShadowEffect: эффект рисует карточку целиком в промежуточный
/// слой — текст в ней теряет ClearType, а сотня карточек «Маршрутов»
/// пересчитывала бы размытие на каждой прокрутке. Здесь тень — несколько
/// полупрозрачных скруглённых прямоугольников вокруг карточки, нарисованных
/// раз при раскладке: размытие по Гауссу на ширине тени приближено
/// линейным спадом, на глаз в 1–2 % прозрачности разницы нет.
/// </para>
/// <para>
/// Внутри карточки тень вырезана: полупрозрачная карточка и стекло
/// иначе темнели бы от неё. И за свои края карточка мышь не ловит —
/// тень в щели между карточками не должна перехватывать щелчок соседа.
/// </para>
/// </remarks>
public class Card : Border
{
    /// <summary>Ключ тени в словаре темы; нет тени — <c>null</c> (тёмные темы).</summary>
    public const string LiftKey = "CardLift";

    public static readonly DependencyProperty LiftProperty = DependencyProperty.Register(
        nameof(Lift), typeof(Lift), typeof(Card),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private Geometry? _outside;
    private Size _outsideFor;

    public Card() => SetResourceReference(LiftProperty, LiftKey);

    public Lift? Lift
    {
        get => (Lift?)GetValue(LiftProperty);
        set => SetValue(LiftProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Тень первой: рамка карточки ложится поверх её сглаженного края.
        if (Lift is { } lift && RenderSize is { Width: > 0, Height: > 0 } size)
        {
            double radius = CornerRadius.TopLeft;

            if (_outside is null || _outsideFor != size)
            {
                _outside = Outside(size, radius, lift.Reach);
                _outsideFor = size;
            }

            dc.PushClip(_outside);
            lift.Draw(dc, size, radius);
            dc.Pop();
        }

        base.OnRender(dc);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        new Rect(RenderSize).Contains(hitTestParameters.HitPoint) ? base.HitTestCore(hitTestParameters) : null;

    /// <summary>Всё вокруг карточки на длину тени, без самой карточки.</summary>
    private static Geometry Outside(Size size, double radius, double reach)
    {
        var card = new Rect(size);
        var around = card;
        around.Inflate(reach, reach);

        var geometry = new GeometryGroup
        {
            FillRule = FillRule.EvenOdd,
            Children = { new RectangleGeometry(around), new RectangleGeometry(card, radius, radius) },
        };

        geometry.Freeze();
        return geometry;
    }
}

/// <summary>
/// Тень карточки — слои как у CSS box-shadow: сдвиг вниз, размытие,
/// расширение и плотность.
/// </summary>
/// <remarks>
/// Каждый слой — девять кусков: углы радиальным градиентом, стороны
/// линейным, середина сплошной. Первая проба 27.10 рисовала слой кольцами
/// по 4 точки — на снимке под карточкой встали полосы. Градиент спадает
/// без ступеней, а кисти от места не зависят (заданы долями куска) и одни
/// на все карточки того же скругления.
/// </remarks>
public sealed class Lift
{
    /// <summary>
    /// Спад по Гауссу на ширине размытия: от внешнего края (0) к внутреннему (1).
    /// </summary>
    /// <remarks>
    /// По CSS размытие b — это σ = b/2, и спад занимает ±2σ вокруг края. Значения —
    /// функция нормального распределения в −2σ, −σ, 0, σ, 2σ, сведённая к 0…1.
    /// Линейный спад заканчивался заметной кромкой.
    /// </remarks>
    private static readonly (double At, double Value)[] Falloff = [(0, 0), (0.25, 0.14), (0.5, 0.5), (0.75, 0.86), (1, 1)];

    private readonly Color _color;
    private readonly (double Y, double Blur, double Spread, double Alpha)[] _layers;
    private readonly Dictionary<(int Layer, double Radius), Brush[]> _brushes = [];

    /// <param name="color">Цвет тени — текст темы: у сиреневой и кремовой
    /// чёрная тень выглядит грязной, а их собственный тёмный — родной.</param>
    /// <param name="layers">Слои: сдвиг вниз, размытие, расширение, плотность (0–1).</param>
    public Lift(Color color, params (double Y, double Blur, double Spread, double Alpha)[] layers)
    {
        _color = color;
        _layers = layers;
        Reach = layers.Length == 0 ? 0 : layers.Max(l => Math.Abs(l.Y) + l.Blur + Math.Max(0, l.Spread)) + 1;
    }

    /// <summary>Насколько тень выходит за карточку.</summary>
    public double Reach { get; }

    /// <summary>
    /// Тень у светлого фона, у тёмного — нет: на тёмном она не видна
    /// и только мутит промежутки между карточками.
    /// </summary>
    public static Lift? For(Color text, double backdropLuminance) =>
        backdropLuminance < 0.4
            ? null
            : new Lift(text, (1, 2, 0, 0.07), (6, 14, -6, 0.10));

    public void Draw(DrawingContext dc, Size size, double radius)
    {
        for (int i = 0; i < _layers.Length; i++)
        {
            var (y, blur, spread, _) = _layers[i];
            var shape = new Rect(-spread, y - spread, size.Width + 2 * spread, size.Height + 2 * spread);

            if (shape.Width <= 0 || shape.Height <= 0)
                continue;

            // Скругление — по половине размера ключом кисти: у узкой плашки
            // радиус упирается в её высоту.
            double r = Math.Round(Math.Clamp(radius + spread, 0, Math.Min(shape.Width, shape.Height) / 2) * 2) / 2;
            double b = Math.Max(0.5, blur);
            double e = r + b;

            if (!_brushes.TryGetValue((i, r), out var brush))
                _brushes[(i, r)] = brush = Pieces(_layers[i].Alpha, r, b);

            double left = shape.Left + r, right = shape.Right - r, top = shape.Top + r, bottom = shape.Bottom - r;

            dc.DrawRectangle(brush[0], null, new Rect(left - e, top - e, e, e));
            dc.DrawRectangle(brush[1], null, new Rect(right, top - e, e, e));
            dc.DrawRectangle(brush[2], null, new Rect(left - e, bottom, e, e));
            dc.DrawRectangle(brush[3], null, new Rect(right, bottom, e, e));

            if (right > left)
            {
                dc.DrawRectangle(brush[4], null, new Rect(left, top - e, right - left, e));
                dc.DrawRectangle(brush[5], null, new Rect(left, bottom, right - left, e));
            }

            if (bottom > top)
            {
                dc.DrawRectangle(brush[6], null, new Rect(left - e, top, e, bottom - top));
                dc.DrawRectangle(brush[7], null, new Rect(right, top, e, bottom - top));
            }

            if (right > left && bottom > top)
                dc.DrawRectangle(brush[8], null, new Rect(left, top, right - left, bottom - top));
        }
    }

    /// <summary>Углы (TL, TR, BL, BR), стороны (верх, низ, лево, право) и середина.</summary>
    private Brush[] Pieces(double alpha, double r, double b)
    {
        double e = r + b;

        Color At(double value) => Color.FromArgb((byte)Math.Round(alpha * value * 255), _color.R, _color.G, _color.B);

        // Сторона: градиент ровно на ширину спада, дальше — плотная.
        Brush Side(Point outer, Point inner)
        {
            var stops = new GradientStopCollection(Falloff.Select(f => new GradientStop(At(f.Value), f.At)));
            return Frozen(new LinearGradientBrush(stops, outer, inner));
        }

        // Угол: расстояние от центра скругления s ↦ от края d = s − r.
        Brush Corner(Point center)
        {
            var stops = new GradientStopCollection();

            for (int k = 0; k <= 8; k++)
            {
                double s = e * k / 8;
                stops.Add(new GradientStop(At(Spread((b - (s - r)) / (2 * b))), k / 8.0));
            }

            return Frozen(new RadialGradientBrush(stops) { Center = center, GradientOrigin = center, RadiusX = 1, RadiusY = 1 });
        }

        double band = 2 * b / e;

        return
        [
            Corner(new Point(1, 1)), Corner(new Point(0, 1)), Corner(new Point(1, 0)), Corner(new Point(0, 0)),
            Side(new Point(0, 0), new Point(0, band)), Side(new Point(0, 1), new Point(0, 1 - band)),
            Side(new Point(0, 0), new Point(band, 0)), Side(new Point(1, 0), new Point(1 - band, 0)),
            Frozen(new SolidColorBrush(At(1))),
        ];
    }

    private static double Spread(double t)
    {
        if (t <= 0)
            return 0;

        for (int i = 1; i < Falloff.Length; i++)
        {
            if (t <= Falloff[i].At)
            {
                var (a0, v0) = Falloff[i - 1];
                var (a1, v1) = Falloff[i];
                return v0 + (v1 - v0) * (t - a0) / (a1 - a0);
            }
        }

        return 1;
    }

    private static T Frozen<T>(T brush) where T : Freezable
    {
        brush.Freeze();
        return brush;
    }
}
