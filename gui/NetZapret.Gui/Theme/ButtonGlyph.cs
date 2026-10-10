using System.Windows;

namespace NetZapret.Gui;

/// <summary>
/// Значок слева от подписи кнопки и стрелка «откроет меню» справа.
/// </summary>
/// <remarks>
/// <para>
/// Рисует их шаблон кнопки (Theme/Controls.xaml, стиль Ghost и всё, что на нём
/// стоит), а не содержимое каждой кнопки: подпись при этом остаётся строкой,
/// и код, который её меняет («Начать» ↔ «Остановить», «Программа: chrome.exe»),
/// значка не теряет. Значок и стрелка — цветом подписи: отмеченный отбор
/// красится в акцент целиком.
/// </para>
/// <para>
/// По макету «Наблюдения» владельца 10.10 («сделай иконки как здесь»).
/// Знак значка — из IconFont; есть ли он в шрифте, проверять снимком.
/// </para>
/// </remarks>
public static class ButtonGlyph
{
    public static readonly DependencyProperty LeadProperty = DependencyProperty.RegisterAttached(
        "Lead", typeof(string), typeof(ButtonGlyph), new PropertyMetadata(string.Empty));

    public static string GetLead(DependencyObject element) => (string)element.GetValue(LeadProperty);

    public static void SetLead(DependencyObject element, string value) => element.SetValue(LeadProperty, value);

    /// <summary>Кнопка открывает меню — справа стрелка вниз.</summary>
    public static readonly DependencyProperty MenuProperty = DependencyProperty.RegisterAttached(
        "Menu", typeof(bool), typeof(ButtonGlyph), new PropertyMetadata(false));

    public static bool GetMenu(DependencyObject element) => (bool)element.GetValue(MenuProperty);

    public static void SetMenu(DependencyObject element, bool value) => element.SetValue(MenuProperty, value);
}
