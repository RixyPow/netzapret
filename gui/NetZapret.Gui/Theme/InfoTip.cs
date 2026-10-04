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
/// </remarks>
public static class InfoTip
{
    public static readonly DependencyProperty OpensOnClickProperty = DependencyProperty.RegisterAttached(
        "OpensOnClick", typeof(bool), typeof(InfoTip), new PropertyMetadata(false, OnOpensOnClick));

    public static bool GetOpensOnClick(DependencyObject element) => (bool)element.GetValue(OpensOnClickProperty);

    public static void SetOpensOnClick(DependencyObject element, bool value) => element.SetValue(OpensOnClickProperty, value);

    private static void OnOpensOnClick(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
            return;

        element.PreviewMouseLeftButtonDown -= OnDown;
        element.PreviewMouseLeftButtonUp -= OnUp;

        if (e.NewValue is true)
        {
            element.PreviewMouseLeftButtonDown += OnDown;
            element.PreviewMouseLeftButtonUp += OnUp;
        }
    }

    // Нажатие съедается целиком, иначе его подберёт кнопка под значком.
    private static void OnDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { ToolTip: ToolTip tip } element)
            return;

        if (tip.IsOpen)
        {
            tip.IsOpen = false;
            return;
        }

        tip.PlacementTarget = element;
        tip.Placement = PlacementMode.Bottom;

        // Открытая щелчком закрывается щелчком мимо, а не через пять секунд.
        tip.StaysOpen = false;
        tip.IsOpen = true;

        element.MouseLeave -= OnLeave;
        element.MouseLeave += OnLeave;
    }

    private static void OnLeave(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: ToolTip tip } element)
        {
            element.MouseLeave -= OnLeave;
            tip.IsOpen = false;
        }
    }
}
