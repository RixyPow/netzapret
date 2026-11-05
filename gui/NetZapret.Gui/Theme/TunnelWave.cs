using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NetZapret.Gui;

/// <summary>
/// Анимация карточки состояния «Главной»: волны туннельного эффекта сквозь
/// барьер, «веером» — девять вложенных копий горба (владелец 05.11).
/// </summary>
/// <remarks>
/// <para>
/// Заменила кольцо с шестиугольными волнами и огоньки на линиях (19–22.10):
/// они не говорили ничего, кроме «что-то движется». Здесь движение и есть
/// состояние: сколько волны проходит сквозь барьер (<see cref="TunnelShape"/>),
/// какого она цвета, мерцает ли барьер (запуск), частота и рост волн — от
/// трафика (<see cref="Traffic"/>).
/// </para>
/// <para>
/// Рисуется в <see cref="OnRender"/> ломаными по три точки, без эффектов:
/// свечение — широкая бледная линия под верхней, а не DropShadowEffect, —
/// текст «Работает» лежит поверх и под эффектом потерял бы ClearType.
/// Такт — анимацией свойства 30 раз в секунду, как было у огоньков: таймер
/// Windows шагает по 15,6 мс и дал бы рывки, а CompositionTarget.Rendering
/// будит окно каждый кадр, даже когда двигать нечего. Только пока элемент
/// на экране и анимации включены (<see cref="Motion.Enabled"/>); иначе — один
/// неподвижный кадр.
/// </para>
/// </remarks>
public sealed class TunnelWave : FrameworkElement
{
    private const int FrameRate = 30;

    /// <summary>Шаг ломаной по горизонтали: на 1000 точек ширины — 333 вершины на линию.</summary>
    private const double Step = 3;

    /// <summary>Копий горба в «веере».</summary>
    private const int Layers = 9;

    /// <summary>Барьер — на этой доле ширины: левее хватает места надписи и разгону волны.</summary>
    private const double BarrierAt = 0.62;

    /// <summary>Откуда (долей ширины) волна в полную силу: левее — надпись «Работает».</summary>
    private const double TextZone = 0.38;

    /// <summary>За сколько секунд стена вырастает из линии при запуске.</summary>
    private const double WallRise = 0.8;

    private readonly List<TunnelShape.Packet> _packets = [];
    private readonly Random _random = new(5);

    private double _last;
    private double _time;
    private double _spawn;
    private double _pass;
    private double _passTarget;
    private double _traffic = 0.4;

    /// <summary>Сколько стены видно, 0–1: в покое её нет, при запуске растёт.</summary>
    private double _wallShown;

    /// <summary>Состояние уже показывалось: первое — без роста стены.</summary>
    private bool _known;

    private bool _want;
    private bool _moving;
    private bool _flicker;
    private bool _broken;
    private bool _idle = true;

    private Color _colour = Colors.Gray;
    private Color _wallColour = Colors.Gray;
    private Pen[] _layers = [];
    private Pen? _glow;
    private Pen? _echo;
    private Pen? _wall;
    private Brush? _wallFill;

    public TunnelWave()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = false;

        Loaded += (_, _) => Move(_want);
        Unloaded += (_, _) => Move(false, keepWish: true);
    }

    /// <summary>Сила трафика 0–1: частота, рост и скорость волн.</summary>
    public double Traffic
    {
        get => _traffic;
        set => _traffic = Math.Clamp(value, 0, 1);
    }

    /// <summary>Состояние волны.</summary>
    /// <param name="colour">Цвет состояния — тот же, что у точки внизу «Главной».</param>
    /// <param name="wall">Цвет барьера в покое (приглушённый текст темы).</param>
    /// <param name="pass">Доля, проходящая сквозь барьер, 0–1.</param>
    /// <param name="idle">Движки стоят: ровная линия, без волн и без барьера.</param>
    /// <param name="flicker">Запуск: барьер мерцает, проход то больше, то меньше.</param>
    /// <param name="broken">Движок упал: барьер цветом состояния, волны медленные.</param>
    public void Show(Color colour, Color wall, double pass, bool idle, bool flicker, bool broken)
    {
        if (colour != _colour || wall != _wallColour || _layers.Length == 0 || broken != _broken)
        {
            _colour = colour;
            _wallColour = wall;
            _broken = broken;
            BuildPens();
        }

        _passTarget = pass;
        _flicker = flicker;

        if (idle != _idle)
        {
            _idle = idle;
            _packets.Clear();
        }

        // Стена — только у работающего обхода: одна у ровной линии «Остановлено»
        // она стояла посреди карточки ни к чему (снимок владельца 05.11). Растёт
        // из линии, когда обход запускают. Первое состояние раздела — сразу как
        // есть: «Главная» пересоздаётся при каждом возврате, и стена росла бы
        // всякий раз, будто движки только что подняли.
        if (!_known || idle || !Motion.Enabled)
            _wallShown = idle ? 0 : 1;

        _known = true;

        // Неподвижный кадр — с готовым проходом, а не с тем, что был до смены.
        if (!Motion.Enabled || idle)
            _pass = pass;

        bool move = !idle && Motion.Enabled;
        _want = move;
        Move(move && IsLoaded);

        if (!_moving)
        {
            Seed();
            InvalidateVisual();
        }
    }

    private void BuildPens()
    {
        _layers = new Pen[Layers];

        for (int i = 1; i <= Layers; i++)
        {
            double alpha = i == Layers ? 0.95 : 0.10 + 0.075 * i;
            _layers[i - 1] = Frozen(new Pen(Brush(_colour, alpha), i == Layers ? 1.7 : 1)
            {
                LineJoin = PenLineJoin.Round,
            });
        }

        _glow = Frozen(new Pen(Brush(_colour, 0.10), 8) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        _echo = Frozen(new Pen(Brush(_colour, 0.40), 1.1) { DashStyle = new DashStyle([3, 5], 0) });

        var wall = _broken ? _colour : _wallColour;
        _wall = Frozen(new Pen(Brush(wall, 0.55), 1.2));
        _wallFill = Brush(wall, _broken ? 0.30 : 0.14);
    }

    /// <summary>
    /// Два пакета на карточке сразу: один подходит к барьеру, другой проходит сквозь.
    /// </summary>
    /// <remarks>
    /// Неподвижный кадр (анимации выключены) — это они. И с них же начинается
    /// движение: первый пакет, выпущенный с левого края, доходит до барьера
    /// секунды за четыре, и всё это время карточка стояла бы пустой линией —
    /// так же, как «Остановлено» (снимок 05.11).
    /// </remarks>
    private void Seed()
    {
        _packets.Clear();

        double w = ActualWidth, h = ActualHeight;

        if (_idle || w <= 0 || h <= 0)
            return;

        double barrier = w * BarrierAt;
        _packets.Add(Packet(barrier - w * 0.17, w, h));
        _packets.Add(Packet(barrier + 8, w, h) with { Height = Rise(h) * 0.8 });
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        if (!_moving || _packets.Count == 0)
            Seed();
    }

    /// <summary>Высота горба: от трафика, верхушка — чуть ниже надписи над волной.</summary>
    private double Rise(double h) => h * (0.20 + 0.12 * _traffic);

    private TunnelShape.Packet Packet(double x, double w, double h)
    {
        bool calm = _broken;
        double speed = w * (calm ? 0.06 : 0.10 + 0.10 * _traffic) * (0.9 + 0.2 * _random.NextDouble());
        double spread = 30 + 12 * _random.NextDouble();

        return new TunnelShape.Packet(x, Rise(h) * (calm ? 0.7 : 1), spread, speed);
    }

    // --- Такт ---------------------------------------------------------------------

    private static readonly DependencyProperty ClockProperty = DependencyProperty.Register(
        nameof(ClockProperty), typeof(double), typeof(TunnelWave),
        new PropertyMetadata(0.0, (d, e) => ((TunnelWave)d).OnClock((double)e.NewValue)));

    private void Move(bool on, bool keepWish = false)
    {
        if (!keepWish && !on)
            _want = false;

        if (on == _moving)
            return;

        _moving = on;

        if (!on)
        {
            BeginAnimation(ClockProperty, null);
            return;
        }

        if (_packets.Count == 0)
            Seed();

        // Час такта за час времени, повторы копятся (IsCumulative) — как у огоньков 20.10.
        const double hour = 3600;
        _last = 0;

        var clock = new DoubleAnimation(0, hour, TimeSpan.FromSeconds(hour))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            IsCumulative = true,
        };

        Timeline.SetDesiredFrameRate(clock, FrameRate);
        BeginAnimation(ClockProperty, clock);
    }

    private void OnClock(double time)
    {
        if (!_moving)
            return;

        double dt = Math.Clamp(time - _last, 0, 0.1);
        _last = time;
        _time += dt;

        Advance(dt);
        InvalidateVisual();
    }

    private void Advance(double dt)
    {
        double w = ActualWidth, h = ActualHeight;

        if (w <= 0 || h <= 0)
            return;

        // Запуск: проход дышит, а не стоит на одном — барьер ещё не «пробит».
        double target = _flicker ? _passTarget * (0.5 + 0.5 * Math.Sin(_time * 2.5)) : _passTarget;
        _pass += (target - _pass) * Math.Min(1, dt * 3);

        if (!_idle && _wallShown < 1)
            _wallShown = Math.Min(1, _wallShown + dt / WallRise);

        for (int i = 0; i < _packets.Count; i++)
            _packets[i] = _packets[i] with { X = _packets[i].X + _packets[i].Speed * dt };

        double barrier = w * BarrierAt;
        _packets.RemoveAll(p => TunnelShape.Gone(p, barrier, w));

        _spawn -= dt;

        if (_spawn > 0)
            return;

        _spawn = _broken ? 3.2 : Math.Max(0.9, 2.6 - 1.7 * _traffic);

        var packet = Packet(0, w, h);
        _packets.Add(packet with { X = -packet.Spread * 3 });
    }

    // --- Рисование ----------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;

        if (w <= 0 || h <= 0 || _layers.Length == 0)
            return;

        double barrier = w * BarrierAt;
        double baseline = h * 0.70;
        double lane = baseline + h * 0.13;

        if (!_idle)
            dc.DrawGeometry(null, _echo, Line(w, x => lane - TunnelShape.Echo(_packets, x, barrier, _pass) * 0.55 * TunnelShape.Fade(x, w, TextZone)));

        // Верхняя копия — со свечением под собой; остальные — ниже ростом, сходятся к линии.
        for (int i = 1; i <= Layers; i++)
        {
            double scale = (double)i / Layers;
            var line = Line(w, x => baseline - TunnelShape.Wave(_packets, x, barrier, _pass) * scale * TunnelShape.Fade(x, w, TextZone));

            if (i == Layers)
                dc.DrawGeometry(null, _glow, line);

            dc.DrawGeometry(null, _layers[i - 1], line);

            // В покое волн нет — одна ровная линия, а не девять друг на друге.
            if (_idle)
                break;
        }

        if (_wallShown <= 0)
            return;

        // Растёт из линии вверх и вниз разом, с замедлением к концу, и проявляется.
        double rise = 1 - Math.Pow(1 - _wallShown, 3);
        double top = baseline - (baseline - h * 0.12) * rise;
        double bottom = baseline + (h * 0.90 - baseline) * rise;
        double opacity = rise * (_flicker && _moving ? 0.45 + 0.4 * Math.Sin(_time * 5) : 1);

        dc.PushOpacity(opacity);
        dc.DrawRoundedRectangle(_wallFill, _wall, new Rect(barrier - 3, top, 6, Math.Max(bottom - top, 1)), 3, 3);
        dc.Pop();
    }

    private static StreamGeometry Line(double width, Func<double, double> y)
    {
        var geometry = new StreamGeometry();

        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, y(0)), false, false);

            for (double x = Step; x < width + Step; x += Step)
            {
                double at = Math.Min(x, width);
                context.LineTo(new Point(at, y(at)), true, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static SolidColorBrush Brush(Color colour, double alpha) =>
        Frozen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), colour.R, colour.G, colour.B)));

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
