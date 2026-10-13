using System.Windows;
using System.Windows.Input;

namespace NetZapret.Gui.Views;

/// <summary>Спрашивает название папки: новой или переименованной.</summary>
public partial class FolderNameWindow : Window
{
    /// <summary>Длина названия: шапка папки — одна строка.</summary>
    public const int MaxLength = 40;

    /// <summary>Название; <c>null</c> — отказались.</summary>
    public string? Chosen { get; private set; }

    private readonly IReadOnlyCollection<string> _reserved;

    /// <param name="head">Заголовок окна.</param>
    /// <param name="reserved">
    /// Занятые имена. Программы — всегда: их папки только «Домены» и «Адреса»,
    /// и папка с тем же именем слилась бы с программой.
    /// </param>
    /// <param name="initial">Что стоит в поле сразу — у переименования прежнее имя.</param>
    /// <param name="action">Подпись кнопки.</param>
    public FolderNameWindow(string head, IReadOnlyCollection<string> reserved, string? initial = null, string action = "Создать")
    {
        InitializeComponent();
        _reserved = reserved;

        Head.Text = head;
        NameBox.Text = initial ?? string.Empty;
        Confirm.Content = action;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnKey(object sender, KeyEventArgs e) => Problem.Visibility = Visibility.Collapsed;

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();

        if (name.Length == 0 || name.Length > MaxLength)
        {
            Problem.Text = $"Название — от одного до {MaxLength} знаков.";
            Problem.Visibility = Visibility.Visible;
            return;
        }

        if (_reserved.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            Problem.Text = $"«{name}» уже занято: такая папка или программа уже есть.";
            Problem.Visibility = Visibility.Visible;
            return;
        }

        Chosen = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
