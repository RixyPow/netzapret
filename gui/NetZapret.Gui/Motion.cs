using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using NetZapret.Core;

namespace NetZapret.Gui;

/// <summary>
/// Анимации окна — в одном месте и с одним выключателем.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 28.09: анимации мастера понравились, «давай добавим по всей
/// программе, с возможностью их выключить». Выключатель — в «Оформлении»
/// (<see cref="AppSettings.Animations"/>); выключенная анимация в самой
/// Windows тоже их гасит: тот, кто её выключил, сделал это не просто так.
/// </para>
/// <para>
/// Всё короткое, до трети секунды, и только на появление: движение, которое
/// заставляет ждать, раздражает с третьего раза. Исчезает сразу, кроме
/// уведомления — у него короткое угасание, чтобы не пропадало рывком.
/// </para>
/// </remarks>
public static class Motion
{
    private static bool? _setting;

    /// <summary>Анимировать ли сейчас.</summary>
    public static bool Enabled => (_setting ??= Read()) && SystemParameters.ClientAreaAnimation;

    /// <summary>Перечитать выключатель — после его смены в «Оформлении».</summary>
    public static void Refresh() => _setting = Read();

    private static bool Read()
    {
        try
        {
            return AppSettings.Load(AppSettings.DefaultPath).Animations;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Элемент проявляется и доезжает на место.</summary>
    public static void Arrive(FrameworkElement element, double dx = 0, double dy = 10, int ms = 280, int delay = 0)
    {
        if (!Enabled)
            return;

        var time = TimeSpan.FromMilliseconds(ms + delay);

        element.BeginAnimation(UIElement.OpacityProperty, Held(0, 1, delay, ms, time));

        // Свой сдвиг — только если элементу не назначен чужой: у полосы
        // запуска на «Главной» трансформ живёт своей анимацией, и затереть
        // его значило бы сломать её.
        if (element.RenderTransform is not (null or MatrixTransform { Matrix.IsIdentity: true } or TranslateTransform))
            return;

        // В теме со стеклом — без сдвига. Кусок размытого фона под карточкой
        // пересчитывается при перекладке окна (Glass), а сдвиг анимацией
        // её не вызывает: карточка ехала, стекло стояло, и после анимации
        // оставалось сдвинутым на десять точек. Владелец 28.09: «дёргано,
        // в VPN и десинке особенно» — тема с фоном, карточек много.
        if (Glass.Image is not null)
            return;

        var shift = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = shift;

        if (dx != 0)
            shift.BeginAnimation(TranslateTransform.XProperty, Held(dx, 0, delay, ms, time));

        if (dy != 0)
            shift.BeginAnimation(TranslateTransform.YProperty, Held(dy, 0, delay, ms, time));
    }

    /// <summary>
    /// Анимация со стартовым значением на всё время задержки.
    /// </summary>
    /// <remarks>
    /// Не <c>BeginTime</c>: до начала анимация не действует, и элемент стоял бы
    /// видимым, а потом мигнул бы в ноль. Здесь стартовое значение держится
    /// с первого кадра, а по окончании анимация снимается — дальше элемент
    /// живёт своим обычным значением, и ничто его не держит.
    /// </remarks>
    private static AnimationTimeline Held(double from, double to, int delay, int ms, TimeSpan total)
    {
        var frames = new DoubleAnimationUsingKeyFrames { Duration = total };
        frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));

        if (delay > 0)
            frames.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delay))));

        frames.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(total), Ease));
        frames.FillBehavior = FillBehavior.Stop;

        return frames;
    }

    /// <summary>
    /// Раздел проявляется целиком — одним коротким движением.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Прежде карточки проявлялись по очереди и со сдвигом, а начало ждало,
    /// пока раздел достроится. Владелец 28.09 дважды: «дёргано», и
    /// «диагностика долго является пустой». Замер: кадры при этом шли ровно
    /// (7 мс), так что дело не в частоте — а в том, что сдвиг вёл текст
    /// ступеньками, очередь из восьми карточек растягивала появление, а
    /// ожидание простоя держало раздел пустым, пока он считал (у «Диагностики»
    /// 0,4 с). Теперь — одно проявление всей страницы за 180 мс.
    /// </para>
    /// <para>
    /// Начинается на Loaded раздела: подписка встаёт после его собственной
    /// (она в конструкторе), и синхронная работа раздела к этому моменту
    /// уже сделана — анимация не тратит свои миллисекунды на чужую паузу.
    /// </para>
    /// </remarks>
    public static void Page(FrameworkElement page)
    {
        if (!Enabled)
            return;

        // Скрыт с создания: первый кадр рисуется раньше Loaded (приоритет
        // отрисовки выше), и без этого раздел успевал показаться целиком,
        // а проявление шло уже по видимому — от 0,92 к 1 (замер 28.09,
        // владелец: «анимации пропали»). Через пять секунд, если так и не
        // дождались, проявится сам.
        page.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 0, TimeSpan.FromSeconds(5)) { FillBehavior = FillBehavior.Stop });

        // Начало — на первом кадре после Loaded: к нему синхронная работа
        // раздела сделана. Отсчёт свой, по часам, а не анимацией WPF: её
        // часы начинали с времени до паузы раздела, и уже второй кадр
        // выходил на 0,83 (замер 28.09) — проявление снова выглядело
        // появлением. Здесь первый показанный кадр — ноль, дальше по
        // реальному времени, с мягким замедлением к концу.
        var clock = new Stopwatch();
        const double Length = 240;

        void Frame(object? sender, EventArgs e)
        {
            if (!clock.IsRunning)
            {
                page.BeginAnimation(UIElement.OpacityProperty, null);
                page.Opacity = 0;
                clock.Start();
                return;
            }

            double x = Math.Min(1, clock.Elapsed.TotalMilliseconds / Length);

            if (x >= 1)
            {
                CompositionTarget.Rendering -= Frame;
                page.ClearValue(UIElement.OpacityProperty);
                return;
            }

            page.Opacity = 1 - (1 - x) * (1 - x);
        }

        void Start(object sender, RoutedEventArgs e)
        {
            page.Loaded -= Start;
            CompositionTarget.Rendering += Frame;
        }

        page.Loaded += Start;
    }
    /// <summary>Угасает и прячется; без анимации — сразу.</summary>
    public static void Leave(FrameworkElement element, Action? after = null)
    {
        if (!Enabled)
        {
            element.Visibility = Visibility.Collapsed;
            after?.Invoke();
            return;
        }

        var fade = new DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop };

        fade.Completed += (_, _) =>
        {
            element.Visibility = Visibility.Collapsed;
            after?.Invoke();
        };

        element.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
