using System.Windows;

namespace NetZapret.Gui;

/// <summary>
/// Меню слева свёрнуто до значков — свойство наследуется от полосы меню вниз,
/// и по нему переключаются шаблоны пунктов (<c>RailItem</c>) и подписи групп (<c>RailGroup</c>).
/// </summary>
/// <remarks>
/// Наследуемым, а не своим у каждого пункта: пунктов тринадцать, и выставлять
/// каждому по отдельности значило бы забыть новый (11.11).
/// </remarks>
public static class RailCompact
{
    public static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(RailCompact),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsOn(DependencyObject element) => (bool)element.GetValue(IsOnProperty);

    public static void SetIsOn(DependencyObject element, bool value) => element.SetValue(IsOnProperty, value);
}
