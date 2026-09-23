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
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null, OnText));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>
    /// Пусто ли в поле пароля — для шаблона, которому больше не по чему судить.
    /// </summary>
    /// <remarks>
    /// У <see cref="System.Windows.Controls.PasswordBox"/> пароль нарочно
    /// не свойство зависимости: к нему не привязаться и по нему не сработает
    /// триггер. Поэтому пустоту отмечаем сами, по событию ввода. Нужно ради
    /// поля ссылки подписки — оно скрытое, потому что ссылка равна паролю.
    /// </remarks>
    public static readonly DependencyProperty EmptyProperty = DependencyProperty.RegisterAttached(
        "Empty", typeof(bool), typeof(Hint), new PropertyMetadata(true));

    public static bool GetEmpty(DependencyObject element) => (bool)element.GetValue(EmptyProperty);

    public static void SetEmpty(DependencyObject element, bool value) => element.SetValue(EmptyProperty, value);

    private static void OnText(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not System.Windows.Controls.PasswordBox box)
            return;

        box.PasswordChanged -= OnPasswordChanged;
        box.PasswordChanged += OnPasswordChanged;
        SetEmpty(box, box.Password.Length == 0);
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox box)
            SetEmpty(box, box.Password.Length == 0);
    }
}
