using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NetZapret.Gui;

/// <summary>
/// Матовое стекло: карточка показывает под собой размытый фон темы.
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 23.09: фон чёткий, размыт он только под карточками
/// и меню. Размывать на лету при каждой перерисовке WPF не тянет, поэтому
/// размытая копия фона считается один раз при применении темы
/// (<see cref="Themes"/>), а каждая карточка берёт из неё кусок ровно под
/// собой: кисть с той же укладкой, что у фона, сдвинутая на место карточки
/// в окне. Двигается карточка — при прокрутке, смене размера окна —
/// сдвигается и кисть.
/// </para>
/// <para>
/// Фон стеклянных элементов задаётся стилем, а не прямо в разметке:
/// ссылку на ресурс, записанную прямо в элемент, сброс свойства потерял бы,
/// а стиль после сброса возвращается сам.
/// </para>
/// </remarks>
public static class Glass
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(Glass), new PropertyMetadata(false, OnIsOnChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(GlassState), typeof(Glass));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);

    /// <summary>Размытая копия фона; <c>null</c> — стекла нет, карточки обычные.</summary>
    public static ImageSource? Image { get; private set; }

    /// <summary>Укладка той копии — та же, что у фона.</summary>
    public static Stretch Stretch { get; private set; } = Stretch.UniformToFill;

    /// <summary>Слой, на котором лежит фон: от него считается место карточки.</summary>
    public static FrameworkElement? Root { get; set; }

    private static event Action? Changed;

    /// <summary>Новая тема — новое стекло или его отсутствие.</summary>
    public static void Set(ImageSource? image, Stretch stretch)
    {
        Image = image;
        Stretch = stretch;
        Changed?.Invoke();
    }

    private static void OnIsOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border)
            return;

        if ((bool)e.NewValue)
        {
            var state = new GlassState(border);
            border.SetValue(StateProperty, state);
            border.Loaded += state.Attach;
            border.Unloaded += state.Detach;

            if (border.IsLoaded)
                state.Attach(border, new RoutedEventArgs());
        }
        else if (border.GetValue(StateProperty) is GlassState state)
        {
            state.Detach(border, new RoutedEventArgs());
            border.Loaded -= state.Attach;
            border.Unloaded -= state.Detach;
            border.ClearValue(StateProperty);
        }
    }

    private sealed class GlassState(Border border)
    {
        private ImageBrush? _brush;
        private object? _own;
        private bool _attached;
        private Rect _last = Rect.Empty;

        private bool _tracking;

        public void Attach(object sender, RoutedEventArgs e)
        {
            if (_attached)
                return;

            _attached = true;
            Changed += Refresh;
            Refresh();
        }

        public void Detach(object sender, RoutedEventArgs e)
        {
            if (!_attached)
                return;

            _attached = false;
            Changed -= Refresh;
            Track(false);
            Restore();
        }

        /// <summary>
        /// Следить за перекладкой окна — только пока стекло есть.
        /// </summary>
        /// <remarks>
        /// LayoutUpdated зовётся на любую перекладку всего окна. Прежде
        /// каждая карточка подписывалась на него всегда, и в любой теме,
        /// даже без картинки, сотня карточек «Маршрутов» отвечала на каждое
        /// движение окна (владелец, 24.09: подвисания интерфейса).
        /// </remarks>
        private void Track(bool on)
        {
            if (on == _tracking)
                return;

            _tracking = on;

            if (on)
                border.LayoutUpdated += OnLayout;
            else
                border.LayoutUpdated -= OnLayout;
        }

        private void Refresh()
        {
            if (Image is null || Root is null)
            {
                Track(false);
                Restore();
                return;
            }

            Track(true);

            if (_brush is null)
                _own = border.ReadLocalValue(Border.BackgroundProperty);

            _brush = new ImageBrush(Image)
            {
                Stretch = Stretch,
                ViewportUnits = BrushMappingMode.Absolute,
                AlignmentX = AlignmentX.Center,
                AlignmentY = AlignmentY.Center,
            };

            _last = Rect.Empty;
            border.Background = _brush;
            OnLayout(null, EventArgs.Empty);
        }

        /// <summary>Сдвигает кисть так, чтобы кусок стекла совпал с фоном под карточкой.</summary>
        private void OnLayout(object? sender, EventArgs e)
        {
            if (_brush is null || Root is null || !border.IsVisible || !Root.IsAncestorOf(border))
                return;

            Point at;

            try
            {
                at = border.TransformToAncestor(Root).Transform(new Point(0, 0));
            }
            catch (InvalidOperationException)
            {
                return;
            }

            var rect = new Rect(-at.X, -at.Y, Root.ActualWidth, Root.ActualHeight);

            // LayoutUpdated зовётся на любую перекладку окна; кисть
            // трогаем, только когда карточка и правда сдвинулась.
            if (rect == _last)
                return;

            _last = rect;
            _brush.Viewport = rect;
        }

        private void Restore()
        {
            if (_brush is null)
                return;

            _brush = null;

            // Фон карточек и меню задан стилем, и сброс возвращает к нему;
            // собственную кисть элемента, если была, ставим обратно.
            if (_own is Brush own)
                border.Background = own;
            else
                border.ClearValue(Border.BackgroundProperty);
        }
    }
}
