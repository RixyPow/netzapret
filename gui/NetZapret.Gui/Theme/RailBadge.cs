using System.Windows;

namespace NetZapret.Gui;

/// <summary>
/// Точка у пункта бокового меню: там есть что-то новое.
/// </summary>
/// <remarks>
/// Рисуется шаблоном пункта (Theme/Controls.xaml). Заведена для обновлений:
/// нашлась новая версия — точка у «Ещё», пока её не поставили (решение
/// владельца 23.09).
/// </remarks>
public static class RailBadge
{
    public static readonly DependencyProperty ShownProperty = DependencyProperty.RegisterAttached(
        "Shown", typeof(bool), typeof(RailBadge), new PropertyMetadata(false));

    public static bool GetShown(DependencyObject element) => (bool)element.GetValue(ShownProperty);

    public static void SetShown(DependencyObject element, bool value) => element.SetValue(ShownProperty, value);
}
