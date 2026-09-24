using System.Windows;
using System.Windows.Controls;

namespace NetZapret.Gui;

/// <summary>
/// Шаблон подписи кнопки — только для текста.
/// </summary>
/// <remarks>
/// <para>
/// Подпись кнопки рисуется своим шаблоном, чтобы брать цвет кнопки,
/// а не общий цвет текста (fc45186). Шаблон, заданный ContentPresenter'у
/// напрямую, применяется к любому содержимому — и значок пина в «Маршрутах»,
/// Path, превратился в строку «System.Windows.Shapes.Path» (снимок
/// владельца 24.09). Моя ошибка: в fc45186 я написал, что разметку внутри
/// кнопки шаблон не трогает, и не проверил.
/// </para>
/// <para>
/// Отсюда выбор: строка — шаблон с цветом кнопки; что угодно другое —
/// без шаблона, то есть как есть.
/// </para>
/// </remarks>
public sealed class CaptionSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is string ? Text : null;
}
