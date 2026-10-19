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

    private readonly bool _pool;

    /// <param name="head">Заголовок окна.</param>
    /// <param name="reserved">
    /// Занятые имена. Программы — всегда: их папки только «Домены» и «Адреса»,
    /// и папка с тем же именем слилась бы с программой.
    /// </param>
    /// <param name="initial">Что стоит в поле сразу — у переименования прежнее имя.</param>
    /// <param name="action">Подпись кнопки.</param>
    /// <param name="pool">Имя пула серверов, а не папки маршрутов: у пула свои пояснения.</param>
    public FolderNameWindow(string head, IReadOnlyCollection<string> reserved, string? initial = null, string action = "Создать",
        bool pool = false)
    {
        InitializeComponent();
        _reserved = reserved;
        _pool = pool;

        Head.Text = head;
        NameBox.Text = initial ?? string.Empty;
        Confirm.Content = action;

        if (pool)
        {
            Title = "Пул серверов";
            Note.Text = "Пул — папка серверов из любых подписок. Маршрут «через VPN», направленный в пул, идёт только через его серверы.";
            NameLabel.Text = "Название пула";
            NameBox.SetValue(Hint.TextProperty, "например, Россия");
        }

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
            Problem.Text = _pool
                ? $"«{name}» уже занято: такой пул уже есть."
                : $"«{name}» уже занято: такая папка или программа уже есть.";
            Problem.Visibility = Visibility.Visible;
            return;
        }

        Chosen = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
