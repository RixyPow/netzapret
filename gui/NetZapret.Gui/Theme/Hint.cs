using System.Windows;

namespace NetZapret.Gui;

/// <summary>
/// Подсказка внутри пустого поля ввода.
/// </summary>
/// <remarks>
/// <para>
/// Рисуется шаблоном поля (Theme/Controls.xaml), а не надписью поверх него.
/// Прежде каждая подсказка была отдельным TextBlock с отступом 13,
/// подобранным на глаз, а текст поля начинается с 14-го пикселя: рамка 1,
/// отступ 11 и ещё 2 собственных у строки ввода. Курсор вставал на первую
/// букву подсказки — владелец 23.09 увидел это почти в каждом поле.
/// </para>
/// <para>
/// В шаблоне подсказка отсчитывается от того же места, что и текст, и никакой
/// разметке подбирать отступ уже не нужно. Прячется она тоже там — пока
/// в поле что-то есть, — так что код разделов больше её не касается.
/// </para>
/// </remarks>
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);
}
