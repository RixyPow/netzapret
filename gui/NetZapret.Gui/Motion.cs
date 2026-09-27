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
    /// Раздел появляется: карточки по очереди, сверху вниз.
    /// </summary>
    /// <remarks>
    /// Ищется первая колонка раздела — панель, где стоят его карточки, —
    /// обходом по разметке, а не по имени: разделов дюжина, и держать в каждом
    /// своё имя для анимации значило бы размазать её по всем. Очередь — первые
    /// двенадцать; дальше всё равно ниже края окна.
    /// </remarks>
    public static void Page(FrameworkElement page)
    {
        if (!Enabled)
            return;

        if (Column(page, 0) is not { } column)
        {
            Arrive(page, dy: 8);
            return;
        }

        int step = 0;

        foreach (UIElement child in column.Children)
        {
            if (child is not FrameworkElement { Visibility: Visibility.Visible } element)
                continue;

            Arrive(element, dy: 10, ms: 260, delay: Math.Min(step++, 12) * 35);
        }
    }

    private static Panel? Column(object node, int depth)
    {
        if (depth > 6)
            return null;

        if (node is StackPanel { Children.Count: >= 3 } stack)
            return stack;

        foreach (var child in LogicalTreeHelper.GetChildren((DependencyObject)node).OfType<DependencyObject>())
        {
            if (Column(child, depth + 1) is { } found)
                return found;
        }

        return null;
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
