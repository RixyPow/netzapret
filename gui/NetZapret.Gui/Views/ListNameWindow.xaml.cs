using System.Windows;
using System.Windows.Input;
using NetZapret.Core.Rules;

namespace NetZapret.Gui.Views;

/// <summary>
/// Спрашивает, заводить ли своему домену файл списка, и под каким названием.
/// </summary>
/// <remarks>
/// Отказ — это «Отмена» и крестик: ничего не создаётся, правило остаётся
/// одной строкой, как было.
/// </remarks>
public partial class ListNameWindow : Window
{
    /// <summary>Выбранное название, уже приведённое к имени файла; <c>null</c> — отказались.</summary>
    public string? Chosen { get; private set; }

    /// <param name="domain">Свой домен, для которого заводится список.</param>
    /// <param name="clashes">
    /// Списки, где этот домен уже есть. С ними правило будет спорить, и сказать
    /// об этом надо до создания, а не после: ради этого окно и заведено.
    /// </param>
    public ListNameWindow(string domain, IReadOnlyList<string> clashes)
    {
        InitializeComponent();

        Head.Text = $"Списка у «{domain}» нет";
        NameBox.Text = domain;

        if (clashes.Count > 0)
        {
            ClashCard.Visibility = Visibility.Visible;
            ClashText.Text = $"«{domain}» уже есть в {(clashes.Count == 1 ? "списке" : "списках")}: "
                + string.Join(", ", clashes.Take(4))
                + (clashes.Count > 4 ? $" и ещё {clashes.Count - 4}" : string.Empty)
                + ". Правила на одно имя спорят: ваш список проверяется раньше списков сервисов "
                + "и победит их, но в «Маршрутах» это будет видно как противоречие.";
        }

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        Problem.Visibility = Visibility.Collapsed;
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = OwnLists.Normalize(NameBox.Text);

        if (name is null)
        {
            Say("Название — буквы, цифры, точка, дефис или подчёркивание, до 64 знаков.");
            return;
        }

        if (OwnLists.Exists(name))
        {
            Say($"Список «{name}» уже есть. Выберите другое название.");
            return;
        }

        Chosen = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Say(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }
}
