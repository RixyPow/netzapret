using System.Windows;

namespace NetZapret.Gui;

/// <summary>
/// Значок пункта бокового меню — знак из шрифта значков Windows (IconFont).
/// </summary>
/// <remarks>
/// <para>
/// Рисуется шаблоном пункта (Theme/Controls.xaml), рядом с подписью.
/// Владелец 30.09, по своему макету «Главной»: «иконки по типу книжки,
/// домика». Знак шрифта, а не картинка — по той же причине, что у IconGhost:
/// красится темой и не мылится при масштабе окна.
/// </para>
/// <para>
/// Коды подобраны по снимку всех кандидатов 30.09 и есть и в Segoe Fluent
/// Icons (Windows 11), и в Segoe MDL2 Assets (Windows 10). Знак, которого
/// в шрифте нет, выходит пустым квадратом — проверять снимком, а не по
/// названию из справочника.
/// </para>
/// </remarks>
public static class MenuIcon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(MenuIcon), new PropertyMetadata(string.Empty));

    public static string GetGlyph(DependencyObject element) => (string)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string value) => element.SetValue(GlyphProperty, value);
}
