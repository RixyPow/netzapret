using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace NetZapret.Gui;

/// <summary>
/// Подсказка значка «i» открывается и щелчком, не только наведением.
/// </summary>
/// <remarks>
/// <para>
/// Жалоба 03.10 (Telegram, снимок с курсором-вопросом на значке): «значки
/// вопроса возле функций, которые не нажимаются и ничего не показывают».
/// Подсказка была только всплывающей: появлялась через секунду неподвижной
/// мыши (InitialShowDelay по умолчанию 1000 мс), а щелчок её не открывал —
/// и даже гасил. Значок выглядит кнопкой, и его нажимают.
/// </para>
/// <para>
/// Щелчок по значку дальше не идёт: «i» стоит внутри плиток-кнопок (game
/// filter на «Десинке», карточки движков), и прежде нажатие на него
/// переключало саму плитку.
/// </para>
/// <para>
/// Щелчком открывается своя подсказка, а не та, что висит на значке
/// (ToolTipService). До 04.10 открывалась та же самая, руками и
/// с <c>StaysOpen=false</c>, — и это роняло окно (владелец, при смене
/// режимов, gui.log 13:51:59 и 13:52:05): служба подсказок WPF, показывая
/// её затем по наведению, падала на <c>StaysOpen=false</c>
/// (NotSupportedException) посреди своей настройки — подсказка оставалась
/// у неё текущей, но без владельца, и следующий щелчок мыши, закрывая её,
/// падал в <c>GetBetweenShowDelay(null)</c> (ArgumentNullException). Своя
/// подсказка службе не видна, а наведённую на это время выключаем,
/// чтобы не вышло двух сразу.
/// </para>
/// </remarks>
public static class InfoTip
{
    public static readonly DependencyProperty OpensOnClickProperty = DependencyProperty.RegisterAttached(
        "OpensOnClick", typeof(bool), typeof(InfoTip), new PropertyMetadata(false, OnOpensOnClick));

    public static bool GetOpensOnClick(DependencyObject element) => (bool)element.GetValue(OpensOnClickProperty);

    public static void SetOpensOnClick(DependencyObject element, bool value) => element.SetValue(OpensOnClickProperty, value);

    /// <summary>Подсказка, открытая щелчком, — у каждого значка своя.</summary>
    private static readonly DependencyProperty ClickTipProperty = DependencyProperty.RegisterAttached(
        "ClickTip", typeof(ToolTip), typeof(InfoTip), new PropertyMetadata(null));

    /// <summary>Открытая щелчком подсказка значка; <c>null</c> — не открыта. Для тестов.</summary>
    internal static ToolTip? ClickTipOf(DependencyObject element) => (ToolTip?)element.GetValue(ClickTipProperty);

    private static void OnOpensOnClick(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.PreviewMouseLeftButtonDown -= OnDown;
        element.PreviewMouseLeftButtonUp -= OnUp;
        element.MouseLeave -= OnLeave;
        element.Unloaded -= OnLeave;

        if (e.NewValue is true)
        {
            element.PreviewMouseLeftButtonDown += OnDown;
            element.PreviewMouseLeftButtonUp += OnUp;
            element.MouseLeave += OnLeave;
            element.Unloaded += OnLeave;
        }
    }

    // Нажатие съедается целиком, иначе его подберёт кнопка под значком.
    private static void OnDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement element)
            return;

        if (element.GetValue(ClickTipProperty) is ToolTip { IsOpen: true })
        {
            Close(element);
            return;
        }

        // Текст — тот же, что у наведённой подсказки значка (стиль Info).
        if (element.ToolTip is not ToolTip { Content: TextBlock { Text: { Length: > 0 } text } })
            return;

        var tip = new ToolTip
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
            PlacementTarget = element,
            Placement = PlacementMode.Bottom,
        };

        ToolTipService.SetIsEnabled(element, false);
        element.SetValue(ClickTipProperty, tip);
        tip.IsOpen = true;
    }

    // Открытая щелчком живёт, пока мышь над значком: так же, как наведённая.
    private static void OnLeave(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
            Close(element);
    }

    private static void Close(FrameworkElement element)
    {
        if (element.GetValue(ClickTipProperty) is not ToolTip tip)
            return;

        element.ClearValue(ClickTipProperty);
        element.ClearValue(ToolTipService.IsEnabledProperty);
        tip.IsOpen = false;
    }
}
