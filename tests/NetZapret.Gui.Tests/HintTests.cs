using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Xunit;

namespace NetZapret.Gui.Tests;

/// <summary>
/// Подсказка пустого поля начинается там же, где встанет первая буква.
/// </summary>
/// <remarks>
/// <para>
/// Иначе курсор пустого поля стоит не перед подсказкой, а внутри неё.
/// Владелец видел это 23.09 и снова 01.10 — после правки, которая подбирала
/// отступ подсказки расчётом на бумаге. Расчёт был неверен: WPF сам отдаёт
/// <c>Padding</c> поля внутренней прокрутке, и вместе с рамкой шаблона отступ
/// выходил двойным. Глазом в разметке этого не увидеть, поэтому проверяется
/// замером — положением строки ввода в собранном поле.
/// </para>
/// <para>
/// Поле пароля — отдельно: шаблон у него свой, а строка ввода та же.
/// Свой отступ — тоже отдельно: так заданы поля в первом запуске и в выборе
/// цели, и подсказка с отступом числом разошлась бы именно там.
/// </para>
/// </remarks>
public sealed class HintTests
{
    public static TheoryData<string, Func<Control>> Fields => new()
    {
        { "поле", () => new TextBox() },
        { "поле со своим отступом", () => new TextBox { Padding = new Thickness(10, 8, 10, 8) } },
        { "поле пароля", () => new PasswordBox() },
        { "поле пароля со своим отступом", () => new PasswordBox { Padding = new Thickness(10, 8, 10, 8) } },
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void The_hint_starts_where_the_first_letter_will(string name, Func<Control> make)
    {
        Sta.Run(() =>
        {
            var field = make();

            // Явно: поле вне окна неявный стиль не находит и остаётся
            // с шаблоном WPF, где подсказки нет вовсе.
            field.Style = (Style)Application.Current.FindResource(field.GetType());
            Hint.SetText(field, "Сервис, часть или домен");
            field.Width = 400;
            field.ApplyTemplate();
            field.Measure(new Size(400, double.PositiveInfinity));
            field.Arrange(new Rect(field.DesiredSize));
            field.UpdateLayout();

            var hint = (TextBlock)field.Template.FindName("HintText", field);
            var hintStart = hint.TranslatePoint(new Point(hint.Padding.Left, hint.Padding.Top), field);

            var line = Find(field, "TextBoxView")
                ?? throw new InvalidOperationException(name + ": строки ввода в поле нет");
            var lineStart = line.TranslatePoint(new Point(), field);

            Assert.Equal(lineStart.X, hintStart.X, 0.01);
            Assert.Equal(lineStart.Y, hintStart.Y, 0.01);

            // У обычного поля — ещё и самим курсором: строка ввода могла бы
            // однажды начинать текст не со своего края.
            if (field is TextBox box)
            {
                var caret = box.GetRectFromCharacterIndex(0);
                Assert.Equal(caret.X, hintStart.X, 0.01);
            }
        });
    }

    /// <summary>
    /// Строка ввода — внутренний тип WPF, поэтому по имени, а не по типу.
    /// </summary>
    private static UIElement? Find(DependencyObject node, string type)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);

            if (child is UIElement element && child.GetType().Name == type)
                return element;

            if (Find(child, type) is { } found)
                return found;
        }

        return null;
    }
}
