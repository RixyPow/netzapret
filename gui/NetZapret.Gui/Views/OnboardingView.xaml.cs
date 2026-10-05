using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Themes;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Сценарий первого запуска: знакомство, оформление, подписка, пробный запуск, результат.
/// </summary>
/// <remarks>
/// <para>
/// До этой правки программа не вела нового человека вовсе — он попадал
/// на «Главную» с кнопкой запуска и остальным «найдите сами». Разбор
/// в <c>docs\design-brief.md</c>, раздел 6: человек не знает, что для VPN
/// нужна своя подписка, что после смены маршрута нужен перезапуск и что
/// проверка рецептов требует остановленных движков. Мастер закрывает ровно
/// это — называет вслух то, что раньше узнавали методом тыка.
/// </para>
/// <para>
/// 28.09 по просьбе владельца мастер вырос с трёх шагов до пяти: первым —
/// живая схема того, что программа делает (точки бегут двумя дорогами:
/// сквозь ТСПУ десинком и через туннель), вторым — выбор темы, последним —
/// телеграм-канал. Схема рисуется кодом, а не картинкой: она берёт цвета
/// темы и перекрашивается вместе с ней.
/// </para>
/// <para>
/// 05.10 (владелец) — шестой шаг, четвёртым: режим работы NZ с объяснением
/// каждого. После подписки — «Туннелю» нужен выход, — до пробного запуска,
/// который поднимает ровно выбранное.
/// </para>
/// <para>
/// Шага «что у вас не работает» здесь нет — он был первым в исходном
/// варианте и просил выбрать сервис из полусотни, ничего не решая.
/// Результат меряет сеть в целом, а не одно выбранное имя.
/// </para>
/// <para>
/// Показывается вместо «Главной» ровно один раз, пока в настройках не стоит
/// <see cref="AppSettings.OnboardingDone"/>. Не окно, а обычный раздел:
/// мастер должен позволять уйти в любой другой раздел в любой момент.
/// Повторно его можно открыть из «Ещё».
/// </para>
/// </remarks>
public partial class OnboardingView : UserControl
{
    private const int LastStep = 6;

    /// <summary>Мастер закрыт — завершением или пропуском.</summary>
    public event EventHandler? Completed;

    private int _step = 1;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    private DateTimeOffset? _startedAt;

    public OnboardingView()
    {
        InitializeComponent();

        _poll.Tick += (_, _) => UpdateTrial();

        Unloaded += (_, _) =>
        {
            _poll.Stop();
            StopHero();
        };

        for (int i = 0; i < LastStep; i++)
        {
            var segment = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, i < LastStep - 1 ? 6 : 0, 0) };
            Progress.Children.Add(segment);
        }

        Loaded += (_, _) => Show(_step);
    }

    private StackPanel Panel(int step) => step switch
    {
        1 => Step1,
        2 => Step2,
        3 => Step3,
        4 => StepMode,
        5 => Step4,
        _ => Step5,
    };

    private void Show(int step)
    {
        _step = step;

        for (int i = 1; i <= LastStep; i++)
            Panel(i).Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < LastStep; i++)
        {
            ((Border)Progress.Children[i]).SetResourceReference(
                Border.BackgroundProperty, i < step ? "Accent" : "Border");
        }

        StepLabel.Text = $"Шаг {step} из {LastStep}";

        // Подпись меняется по шагу: на последнем «пропустить» пропускать
        // уже нечего, и кнопка честно называется «Готово».
        HeaderSkip.Content = step == LastStep ? "Готово" : "Пропустить";

        Arrive(Panel(step));

        if (step == 1)
            StartHero();
        else
            StopHero();

        if (step == 2)
            ShowThemes();

        if (step == 4)
            ShowModeStep();

        if (step == 5)
            PrepareTrial();

        if (step == 6)
            _ = RunCheckAsync();
    }

    /// <summary>
    /// Пропускает текущий шаг, а не весь мастер.
    /// </summary>
    /// <remarks>
    /// Раньше эта кнопка закрывала мастер целиком с любого шага — человек,
    /// нажавший её случайно, терял пробный запуск и результат. Теперь она
    /// делает то же, что назвал бы следующий шаг сам: на подписке это
    /// «десинк без VPN», на пробном запуске — «не запускать сейчас».
    /// </remarks>
    private void OnHeaderSkip(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 3:
                SkipSubscription();
                break;

            case 5:
                _poll.Stop();
                Show(6);
                break;

            case LastStep:
                Finish();
                break;

            default:
                Show(_step + 1);
                break;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(Math.Max(1, _step - 1));
    }

    // --- Движение ------------------------------------------------------------

    /// <summary>Анимировать ли: выключатель в «Оформлении» и сама Windows.</summary>
    private static bool Moving => Motion.Enabled;

    /// <summary>Шаг въезжает справа и проявляется.</summary>
    private static void Arrive(FrameworkElement panel) => Motion.Arrive(panel, dx: 18, dy: 0, ms: 320);

    // --- Шаг 1: знакомство ---------------------------------------------------

    private void OnIntroNext(object sender, RoutedEventArgs e) => Show(2);

    /// <summary>
    /// Рисует схему и пускает по ней пакеты.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Три дороги — как три пути в программе (владелец 05.10, по своему
    /// макету: «интереснее и понятнее, но не нарушай логику»). Пакеты выходят
    /// из компьютера одинаковыми, «Маршруты» раскладывают их по правилам —
    /// у человека на машине, а не у провайдера, — и дальше каждый идёт
    /// своим путём сквозь ТСПУ. Десинк: пакет разрезан надвое, и ТСПУ
    /// пропускает его неузнанным. VPN: пакет завёрнут в туннель до сервера
    /// за границей, и ТСПУ видит туннель, а не сайт. Как есть: сайт
    /// не заблокирован, трогать нечего.
    /// </para>
    /// <para>
    /// Из макета не взято «Анализ трафика» посередине: в пакеты смотрит ТСПУ,
    /// а программа раскладывает их по правилам, — поэтому лупа стоит у ТСПУ.
    /// И десинк там вёл к «обычному сайту напрямую», а он нужен как раз
    /// заблокированному.
    /// </para>
    /// <para>
    /// Вспышки у «Маршрутов», у ТСПУ и у сайта приходятся ровно на проход
    /// пакета: период у них тот же, что у пакета, а начало сдвинуто на долю
    /// пути до этой точки. Цвета — ресурсами темы, не числами: смена темы
    /// на следующем шаге перекрасит схему, если вернуться.
    /// </para>
    /// </remarks>
    private void StartHero()
    {
        StopHero();

        // Холст 700 точек: мастер не шире 680 (OnboardingView.xaml), и холст
        // шире ужимался бы вместе с подписями — при 880 они выходили по 8 точек.
        const double pcX = 44, sortX = 150, wallX = 382, vpnX = 466, siteX = 536, mid = 135;
        double[] lane = [62, mid, 208];

        HeroLane[] lanes =
        [
            new("Accent", "Десинк", "разрезан — ТСПУ не узнаёт",
                "", "Под блокировкой", "открывается напрямую"),
            new("Text", "VPN", "в шифрованном туннеле",
                "", "Закрыт по стране", "видит адрес VPN"),
            new("Muted", "Как есть", "NetZapret их не трогает",
                "", "Обычные сайты", "банки, игры, Рунет"),
        ];

        // Ствол: от компьютера до «Маршрутов» пакеты идут вперемешку.
        Stroke(new Point(pcX + 32, mid), new Point(sortX - 17, mid), "Border", 2, dashed: true);

        for (int i = 0; i < lanes.Length; i++)
        {
            var y = lane[i];
            var color = lanes[i].Color;

            if (i == 1)
            {
                // Туннель — трубой до сервера VPN: ТСПУ видит трубу, но не то, что в ней.
                double from = sortX + 17, to = vpnX - 18;

                var fill = new Rectangle { Width = to - from, Height = 16, RadiusX = 8, RadiusY = 8, Opacity = 0.08 };
                fill.SetResourceReference(Shape.FillProperty, color);
                Place(fill, from, y - 8, z: 0);

                var shell = new Rectangle { Width = to - from, Height = 16, RadiusX = 8, RadiusY = 8, StrokeThickness = 1.2, Opacity = 0.45 };
                shell.SetResourceReference(Shape.StrokeProperty, color);
                Place(shell, from, y - 8, z: 0);

                Stroke(new Point(vpnX + 18, y), new Point(siteX - 25, y), color, 1.5, opacity: 0.45);
            }
            else
            {
                // Мягкое свечение — толстой полупрозрачной линией, а не эффектом:
                // эффект на дороге, по которой бегут пакеты, перерисовывался бы
                // с каждым кадром.
                Stroke(new Point(sortX + 17, y), new Point(siteX - 25, y), color, 8, opacity: 0.08);
                Stroke(new Point(sortX + 17, y), new Point(siteX - 25, y), color, 2, opacity: 0.6, dashed: i == 2);
            }

            Chip(lanes[i].Name, (sortX + 17 + wallX) / 2, y - 32, color);
            Label(lanes[i].Hint, (sortX + 17 + wallX) / 2, y + 11, "Muted", size: 11);

            Box(new Point(siteX, y), 46, 46, color, lanes[i].Glyph, 20);
            Label(lanes[i].Site, siteX + 31, y - 18, "Text", bold: true, left: true);
            Label(lanes[i].SiteHint, siteX + 31, y + 1, "Muted", size: 11, left: true);
        }

        Box(new Point(pcX, mid), 72, 60, "Border", "", 26, glyphColor: "Text");
        Label("Компьютер", pcX, mid + 33, "Text", bold: true);

        // «Маршруты» — раскладка по правилам; гнёзда окрашены цветом своей дороги.
        var sorter = new Border { Width = 34, Height = 198, CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(1.5) };
        sorter.SetResourceReference(Border.BackgroundProperty, "Surface");
        sorter.SetResourceReference(Border.BorderBrushProperty, "Border");
        Place(sorter, sortX - 17, 36);
        Chip("Маршруты", sortX, 8, "Muted");

        for (int i = 0; i < lanes.Length; i++)
            Place(Slot(lanes[i].Color, 0.35), sortX - 10, lane[i] - 10, z: 3);

        var wall = new Rectangle { Width = 10, Height = 198, RadiusX = 4, RadiusY = 4 };
        wall.SetResourceReference(Shape.FillProperty, "Danger");
        Place(wall, wallX - 5, 36);
        Chip("ТСПУ", wallX, 8, "Danger", glyph: "");

        Box(new Point(vpnX, lane[1]), 36, 36, lanes[1].Color, "", 14, round: true);
        Label("сервер VPN", vpnX, lane[1] + 22, "Muted", size: 11);

        if (!Moving)
        {
            // Без движения — по пакету на дороге, уже за «Маршрутами».
            Still(Packet("Text"), (pcX + sortX) / 2 + 10, mid);
            Still(Half(lanes[0].Color), wallX - 46, lane[0]);
            Still(Half(lanes[0].Color), wallX - 36, lane[0]);
            Still(Wrapped(lanes[1].Color), wallX - 40, lane[1]);
            Still(Packet(lanes[2].Color), wallX - 40, lane[2]);

            foreach (UIElement card in Features.Children)
                card.Opacity = 1;

            return;
        }

        for (int i = 0; i < lanes.Length; i++)
        {
            Point[] route = i == 1
                ? [new(pcX + 32, mid), new(siteX - 25, mid)]
                : [new(pcX + 32, mid), new(sortX, mid), new(sortX, lane[i]), new(siteX - 25, lane[i])];

            var trip = TimeSpan.FromSeconds(Length(route) / HeroSpeed);

            // Доли пути: вошёл в «Маршруты», вышел из них, дошёл до ТСПУ, до сервера VPN.
            double inside = FractionAtX(route, sortX);
            double sorted = FractionAtX(route, sortX + 17);
            double tspu = FractionAtX(route, wallX);
            double server = FractionAtX(route, vpnX);

            for (int k = 0; k < 2; k++)
            {
                var start = TimeSpan.FromSeconds(0.55 * i + k * trip.TotalSeconds / 2);

                // До «Маршрутов» все пакеты одинаковы — раскладка ещё не случилась.
                // Подмена вида — внутри «Маршрутов», за их телом, и её не видно.
                switch (i)
                {
                    case 0:
                        Runner(route, trip, start, Packet("Text"), (0, inside));
                        Runner(route, trip, start, Half(lanes[0].Color), (inside, 1));
                        Runner(route, trip, start + TimeSpan.FromMilliseconds(70), Half(lanes[0].Color), (inside, 1));
                        break;

                    case 1:
                        Runner(route, trip, start, Packet("Text"), (0, inside), (server, 1));
                        Runner(route, trip, start, Wrapped(lanes[1].Color), (inside, server));
                        break;

                    default:
                        Runner(route, trip, start, Packet("Text"), (0, inside));
                        Runner(route, trip, start, Packet(lanes[2].Color), (inside, 1));
                        break;
                }

                var slot = Slot(lanes[i].Color, 0);
                Place(slot, sortX - 10, lane[i] - 10, z: 3);
                Flash(slot, trip, start + trip * sorted, 1);

                var pass = new Ellipse { Width = 30, Height = 30, Opacity = 0 };
                pass.SetResourceReference(Shape.FillProperty, lanes[i].Color);
                Place(pass, wallX - 15, lane[i] - 15, z: 3);
                Flash(pass, trip, start + trip * tspu, 0.55);

                var glow = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(17), Opacity = 0 };
                glow.SetResourceReference(Border.BackgroundProperty, lanes[i].Color);
                Place(glow, siteX - 30, lane[i] - 30, z: 1);
                Flash(glow, trip, start + trip * 0.96, 0.35);
            }
        }

        // Карточки под схемой проявляются по очереди.
        int n = 0;

        foreach (UIElement card in Features.Children)
        {
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                BeginTime = TimeSpan.FromMilliseconds(250 + 170 * n++),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    /// <summary>Скорость пакета, точек в секунду: у всех дорог одна, длина у них разная.</summary>
    private const double HeroSpeed = 160;

    /// <summary>Дорога схемы: цвет, подпись на ней и сайт в её конце.</summary>
    private sealed record HeroLane(string Color, string Name, string Hint, string Glyph, string Site, string SiteHint);

    /// <summary>
    /// Запущенные анимации схемы — чтобы остановить их, уходя с шага.
    /// </summary>
    /// <remarks>
    /// Убрать элементы из холста мало: часы анимаций идут и без него, и окно
    /// перерисовывалось бы каждый кадр, пока мастер стоит на другом шаге
    /// (тот же род ошибки, что с «Замером скорости» 30.09).
    /// </remarks>
    private readonly List<(IAnimatable Target, DependencyProperty Property)> _heroClocks = [];

    private void Animate(IAnimatable target, DependencyProperty property, AnimationTimeline animation)
    {
        target.BeginAnimation(property, animation);
        _heroClocks.Add((target, property));
    }

    private void StopHero()
    {
        foreach (var (target, property) in _heroClocks)
            target.BeginAnimation(property, null);

        _heroClocks.Clear();
        Hero.Children.Clear();
    }

    private void Stroke(Point from, Point to, string color, double thickness, double opacity = 1, bool dashed = false)
    {
        var line = new System.Windows.Shapes.Line
        {
            X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y,
            StrokeThickness = thickness, Opacity = opacity,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };

        if (dashed)
            line.StrokeDashArray = [3, 3];

        line.SetResourceReference(Shape.StrokeProperty, color);
        Place(line, 0, 0, z: 0);
    }

    /// <summary>Узел схемы: плитка со значком, как плитки окна.</summary>
    private void Box(Point at, double width, double height, string color, string glyph, double size, string? glyphColor = null, bool round = false)
    {
        var icon = new TextBlock
        {
            Text = glyph, FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, glyphColor ?? color);

        var box = new Border
        {
            Width = width, Height = height, BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(round ? width / 2 : 13), Child = icon,
        };
        box.SetResourceReference(Border.BackgroundProperty, "Surface");
        box.SetResourceReference(Border.BorderBrushProperty, color);

        Place(box, at.X - width / 2, at.Y - height / 2);
    }

    private void Chip(string text, double centerX, double top, string color, string? glyph = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        if (glyph is not null)
        {
            var icon = new TextBlock { Text = glyph, FontSize = 11, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon.SetResourceReference(TextBlock.ForegroundProperty, color);
            row.Children.Add(icon);
        }

        var word = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        word.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        word.SetResourceReference(TextBlock.ForegroundProperty, color);
        row.Children.Add(word);

        var chip = new Border
        {
            CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.2),
            Padding = new Thickness(9, 2, 9, 3), Child = row,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "Surface");
        chip.SetResourceReference(Border.BorderBrushProperty, color);
        chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        Place(chip, centerX - chip.DesiredSize.Width / 2, top, z: 3);
    }

    private static Border Slot(string color, double opacity)
    {
        var slot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(6), Opacity = opacity };
        slot.SetResourceReference(Border.BackgroundProperty, color);
        return slot;
    }

    /// <summary>Пакет целиком.</summary>
    private static FrameworkElement Packet(string color)
    {
        var packet = new Rectangle { Width = 12, Height = 12, RadiusX = 3, RadiusY = 3 };
        packet.SetResourceReference(Shape.FillProperty, color);
        return packet;
    }

    /// <summary>Половина разрезанного пакета — их идёт две, одна за другой.</summary>
    private static FrameworkElement Half(string color)
    {
        var half = new Rectangle { Width = 6, Height = 12, RadiusX = 2, RadiusY = 2 };
        half.SetResourceReference(Shape.FillProperty, color);
        return half;
    }

    /// <summary>Пакет в туннеле: тот же пакет, завёрнутый в оболочку.</summary>
    private static FrameworkElement Wrapped(string color)
    {
        var core = new Rectangle { Width = 6, Height = 6, RadiusX = 1.5, RadiusY = 1.5 };
        core.SetResourceReference(Shape.FillProperty, color);

        var shell = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(2), Child = core,
        };
        shell.SetResourceReference(Border.BorderBrushProperty, color);
        shell.SetResourceReference(Border.BackgroundProperty, "Surface");
        return shell;
    }

    private void Still(FrameworkElement packet, double x, double y) =>
        Place(packet, x - packet.Width / 2, y - packet.Height / 2, z: 1);

    /// <summary>
    /// Пакет на дороге; виден только на отрезках <paramref name="shown"/> (доли пути).
    /// </summary>
    /// <remarks>
    /// Вид пакета меняется по дороге — одинаковый до «Маршрутов», разрезанный
    /// или завёрнутый после, — и каждый вид бежит своим элементом тем же
    /// путём и в такт, а виден лишь на своём отрезке.
    /// </remarks>
    private void Runner(Point[] route, TimeSpan trip, TimeSpan start, FrameworkElement packet, params (double From, double To)[] shown)
    {
        var road = new PathGeometry([new PathFigure(route[0], [new PolyLineSegment(route.Skip(1), true)], false)]);

        var move = new TranslateTransform();
        packet.RenderTransform = move;
        packet.Opacity = 0;
        Place(packet, -packet.Width / 2, -packet.Height / 2, z: 1);

        Animate(move, TranslateTransform.XProperty, new DoubleAnimationUsingPath
        {
            PathGeometry = road, Source = PathAnimationSource.X, Duration = trip,
            BeginTime = start, RepeatBehavior = RepeatBehavior.Forever,
        });

        Animate(move, TranslateTransform.YProperty, new DoubleAnimationUsingPath
        {
            PathGeometry = road, Source = PathAnimationSource.Y, Duration = trip,
            BeginTime = start, RepeatBehavior = RepeatBehavior.Forever,
        });

        // Проявляется в начале пути и гаснет у сайта: иначе пакет
        // висел бы в углу, пока не пришло его время.
        var seen = new DoubleAnimationUsingKeyFrames { Duration = trip, BeginTime = start, RepeatBehavior = RepeatBehavior.Forever };
        seen.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(0)));

        foreach (var (from, to) in shown)
        {
            seen.KeyFrames.Add(from <= 0
                ? new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.04))
                : new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(from)));

            if (to >= 1)
            {
                seen.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.95)));
                seen.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            }
            else
            {
                seen.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromPercent(to)));
            }
        }

        Animate(packet, OpacityProperty, seen);
    }

    /// <summary>Вспышка раз в период, начиная с <paramref name="at"/>, — в такт пакету.</summary>
    private void Flash(UIElement target, TimeSpan trip, TimeSpan at, double peak)
    {
        var flash = new DoubleAnimationUsingKeyFrames { Duration = trip, BeginTime = at, RepeatBehavior = RepeatBehavior.Forever };
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(peak, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(90))));
        flash.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(650))));

        Animate(target, OpacityProperty, flash);
    }

    private static double Length(Point[] route)
    {
        double length = 0;

        for (int i = 1; i < route.Length; i++)
            length += (route[i] - route[i - 1]).Length;

        return length;
    }

    /// <summary>Доля пути, на которой пакет впервые доходит до <paramref name="x"/>.</summary>
    private static double FractionAtX(Point[] route, double x)
    {
        double total = Length(route), walked = 0;

        for (int i = 1; i < route.Length; i++)
        {
            Point a = route[i - 1], b = route[i];

            if (a.X >= x)
                return walked / total;

            var segment = (b - a).Length;

            if (b.X >= x)
                return (walked + segment * (x - a.X) / (b.X - a.X)) / total;

            walked += segment;
        }

        return 1;
    }

    private void Label(string text, double x, double top, string color, double size = 12, bool bold = false, bool left = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, color);
        label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(label, left ? x : x - label.DesiredSize.Width / 2, top, z: 3);
    }

    /// <summary>
    /// На холст; <paramref name="z"/> — слой: дороги, пакеты, узлы, подписи и вспышки.
    /// </summary>
    /// <remarks>
    /// Пакеты ниже узлов намеренно: смена вида пакета случается внутри
    /// «Маршрутов» и сервера VPN, и тело узла её прячет.
    /// </remarks>
    private void Place(UIElement element, double left, double top, int z = 2)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        Canvas.SetZIndex(element, z);
        Hero.Children.Add(element);
    }

    // --- Шаг 2: оформление ---------------------------------------------------

    private void ShowThemes()
    {
        try
        {
            var current = Themes.Current;

            ThemeList.ItemsSource = ThemeLoader.LoadAll()
                .Select(load => ThemeTile.From(load, load.Id == current, key => (Brush)FindResource(key)))
                .ToList();
        }
        catch (Exception ex)
        {
            ThemeStatus.Text = "Темы не читаются: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Та же смена темы, что в «Оформлении»: сразу и с записью в настройки.</summary>
    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        try
        {
            var result = Themes.Apply(id);

            if (result.Ok)
            {
                (AppSettings.Load(AppSettings.DefaultPath) with { Theme = id }).Save(AppSettings.DefaultPath);
                ThemeStatus.Text = string.Empty;
            }
            else
            {
                ThemeStatus.Text = $"Тема «{id}» не применена: {string.Join("; ", result.Problems.Take(3))}.";
            }

            ShowThemes();
        }
        catch (Exception ex)
        {
            ThemeStatus.Text = "Не удалось сменить тему: " + ex.GetBaseException().Message;
        }
    }

    private void OnThemeNext(object sender, RoutedEventArgs e) => Show(3);

    // --- Шаг 3: подписка на VPN ----------------------------------------------

    private void OnNoSubscription(object sender, RoutedEventArgs e) => SkipSubscription();

    /// <summary>
    /// Без подписки режим переключается на «только десинк».
    /// </summary>
    /// <remarks>
    /// Режим по умолчанию — «Выборочно», и он требует туннель для всего,
    /// что в правилах помечено <c>proxy</c>. Без подписки сборка конфига
    /// отказывает целиком фразой «Подписка не задана», и пробный запуск
    /// не поднял бы вообще ничего — ни десинка, который как раз работает
    /// без всякой подписки.
    /// </remarks>
    private void SkipSubscription()
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl) && settings.PresetName is not null)
            {
                (settings with { Mode = OperatingMode.DesyncOnly }).Save(AppSettings.DefaultPath);
            }
        }
        catch (Exception)
        {
            // Режим — удобство пробного запуска, а не условие мастера.
        }

        Show(4);
    }

    /// <summary>
    /// Проверяет ссылку и подключает подписку — тот же путь, что в разделе VPN.
    /// </summary>
    /// <remarks>
    /// Ссылка на подписку равносильна паролю: поле — <see cref="PasswordBox"/>,
    /// значение нигде не печатается и не остаётся в журнале, а после
    /// использования очищается.
    /// </remarks>
    private async void OnAddSubscription(object sender, RoutedEventArgs e)
    {
        var raw = SubLink.Password.Trim();

        if (raw.Length == 0)
        {
            Step3Status.Text = "Ссылку никто не вставил — нажмите «Нет подписки», если её нет.";
            return;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            Step3Status.Text = "Это не похоже на ссылку.";
            return;
        }

        parsed = SubscriptionClient.Unwrap(parsed);

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            Step3Status.Text = "Это не похоже на ссылку подписки: нужна http, https "
                + "либо обёртка happ, clash или sn.";

            return;
        }

        Step3Next.IsEnabled = false;
        Step3Status.Text = "Загружаю список серверов…";

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(parsed, CancellationToken.None);

            var book = SubscriptionBook.Load();

            var entry = new SubscriptionEntry { Name = book.FreeName(), Url = parsed.ToString() };
            book.Entries.Add(entry);
            book.Save();

            SubscriptionBook.MakeActive(entry);

            SubLink.Clear();

            int usable = info.Servers.Count(s => s.IsUsableOutbound);
            Step3Status.Text = $"Подписка подключена: серверов {usable}.";

            Show(4);
        }
        catch (Exception ex)
        {
            Step3Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }
        finally
        {
            Step3Next.IsEnabled = true;
        }
    }

    // --- Шаг 4: режим работы -------------------------------------------------

    /// <summary>
    /// Три режима с объяснением; выбран тот, что стоит сейчас.
    /// </summary>
    /// <remarks>
    /// «Туннель» без подписки и WARP недоступен (<see cref="WorkModes.CanChoose"/>):
    /// поднимать нечем, и выбор вёл бы к пробному запуску, который ничего
    /// не поднимет. Стоял он, а выхода нет — отмечается «Десинк».
    /// </remarks>
    private void ShowModeStep()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        PickDesyncName.Text = WorkModes.Name(WorkMode.Desync);
        PickTunnelName.Text = WorkModes.Name(WorkMode.Tunnel);
        PickRouteName.Text = WorkModes.Name(WorkMode.Route);

        bool tunnel = WorkModes.CanChoose(settings, WorkMode.Tunnel);
        PickTunnel.IsEnabled = tunnel;
        PickTunnelNote.Visibility = tunnel ? Visibility.Collapsed : Visibility.Visible;

        var current = WorkModes.Of(settings.Engines) is { } mode && WorkModes.CanChoose(settings, mode)
            ? mode
            : WorkMode.Desync;

        PickDesync.IsChecked = current == WorkMode.Desync;
        PickTunnel.IsChecked = current == WorkMode.Tunnel;
        PickRoute.IsChecked = current == WorkMode.Route;
    }

    private void OnModeNext(object sender, RoutedEventArgs e)
    {
        var picked = new[] { PickDesync, PickTunnel, PickRoute }
            .FirstOrDefault(p => p.IsChecked == true)?.Tag as string;

        if (Enum.TryParse<WorkMode>(picked, out var mode))
        {
            try
            {
                var settings = AppSettings.Load(AppSettings.DefaultPath);

                if (WorkModes.CanChoose(settings, mode))
                    WorkModes.Choose(settings, mode).Save(AppSettings.DefaultPath);
            }
            catch (Exception)
            {
                // Не записалось — мастер идёт дальше со старым режимом; он виден на «Главной».
            }
        }

        Show(5);
    }

    // --- Шаг 5: пробный запуск ---------------------------------------------

    private void PrepareTrial()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        var planned = new List<string>();

        if (settings.NeedsDesync)
            planned.Add("десинк — " + settings.DescribePreset());

        if (settings.NeedsProxy)
            planned.Add("VPN");

        Step4Detail.Text = planned.Count == 0
            ? "Запускать пока нечего: ни пресет, ни подписка не заданы. Можно вернуться шагом назад "
              + "или просто посмотреть результат — там же будет сказано, что чинить."
            : "Поднимутся: " + string.Join(" и ", planned) + ". "
              + "Пара секунд на десинк, до полуминуты на туннель.";

        Step4Start.IsEnabled = planned.Count > 0;
        Step4Status.Text = "Ничего ещё не запускалось.";
    }

    /// <summary>
    /// Поднимает движки тем же путём, что кнопка «Запустить» на «Главной».
    /// </summary>
    /// <remarks>
    /// Только по нажатию, не само при входе на шаг: решать, когда обрывать
    /// текущий трафик ради обхода, — дело человека, даже во время мастера.
    /// </remarks>
    private async void OnStep4Start(object sender, RoutedEventArgs e)
    {
        Step4Start.IsEnabled = false;
        Step4Status.Text = "Собираю конфиг…";

        var outcome = await EngineControl.StartAsync("мастер первого запуска", CancellationToken.None);

        if (!outcome.Ok)
        {
            Step4Status.Text = outcome.Message;
            Step4Start.IsEnabled = true;

            return;
        }

        _startedAt = DateTimeOffset.Now;
        _poll.Start();
        UpdateTrial();
    }

    private void UpdateTrial()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        if (!running)
        {
            var seconds = _startedAt is { } since ? (int)(DateTimeOffset.Now - since).TotalSeconds : 0;
            Step4Status.Text = $"Поднимается… {seconds} с";

            return;
        }

        _poll.Stop();

        bool healthy = state!.Services.All(s => s.Health == ServiceHealth.Healthy);

        Step4Status.Text = healthy
            ? "Движки работают."
            : "Движки запущены, но не все службы в порядке — подробности на «Главной». "
              + "Можно идти дальше: проверка на следующем шаге покажет, помогло ли.";
    }

    private void OnStep4Next(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(6);
    }

    // --- Шаг 6: результат ----------------------------------------------------

    private void OnRecheck(object sender, RoutedEventArgs e) => _ = RunCheckAsync();

    /// <summary>
    /// Меряет саму сеть, а не одно выбранное имя.
    /// </summary>
    /// <remarks>
    /// Тот же замер, что открывает полную «Проверку блокировок»: TLS, HTTP
    /// и DoH решают вердикт, ICMP и IPv6 — свойство сети, а не след
    /// вмешательства, и на него не влияют.
    /// </remarks>
    private async Task RunCheckAsync()
    {
        ResultTitle.Text = "Проверяю сеть…";
        ResultBody.Text = string.Empty;
        ResultVerdict.Visibility = Visibility.Collapsed;

        try
        {
            var baseline = await BlockCheck.MeasureBaselineAsync(CancellationToken.None);
            ShowResult(baseline);
        }
        catch (Exception ex)
        {
            ResultTitle.Text = "Проверка сорвалась";
            ResultBody.Text = ex.GetBaseException().Message;
        }
    }

    private void ShowResult(NetworkBaseline baseline)
    {
        bool ok = baseline.Tls && baseline.Http;

        ResultTitle.Text = ok ? "Сеть отвечает" : "Сеть отвечает не полностью";

        ResultVerdict.Text = string.Join(" · ", new[]
        {
            $"TLS {(baseline.Tls ? "доступен" : "недоступен")}",
            $"HTTP {(baseline.Http ? "доступен" : "недоступен")}",
            $"DoH {(baseline.Doh ? "доступен" : "недоступен")}",
        });

        ResultVerdict.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Accent" : "Danger");
        ResultVerdict.Visibility = Visibility.Visible;

        ResultBody.Text = ok
            ? "Обход настроен и сеть под ним отвечает. Что именно теперь открывается — "
              + "«Проверка блокировок» покажет разом на сотне с лишним имён. Если что-то "
              + "конкретное всё ещё не работает — «Маршруты» переключают способ для него отдельно."
            : "TLS или HTTP не отвечают даже так — движки могли не подняться. Загляните "
              + "на «Главную»: там видно, что именно не запустилось, и можно попробовать снова.";
    }

    private void OnTelegram(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = About.Telegram, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ResultBody.Text = "Не удалось открыть канал: " + ex.GetBaseException().Message;
        }
    }

    // --- Завершение -----------------------------------------------------------

    private void OnFinish(object sender, RoutedEventArgs e) => Finish();

    /// <summary>
    /// Отмечает мастер закрытым и уступает место «Главной».
    /// </summary>
    /// <remarks>
    /// Ставится и при пропуске: «пропустить» тоже решение человека,
    /// и показывать мастер второй раз значило бы не уважать его.
    /// </remarks>
    private void Finish()
    {
        _poll.Stop();

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            (settings with { OnboardingDone = true }).Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не записалось — мастер покажется снова при следующем запуске.
        }

        Completed?.Invoke(this, EventArgs.Empty);
    }
}
