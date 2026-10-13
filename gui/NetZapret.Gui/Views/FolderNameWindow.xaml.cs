using System.Windows;
using System.Windows.Input;

namespace NetZapret.Gui.Views;

/// <summary>Спрашивает название новой папки, в которую ляжет маршрут.</summary>
public partial class FolderNameWindow : Window
{
    /// <summary>Длина названия: шапка папки — одна строка.</summary>
    public const int MaxLength = 40;

    /// <summary>Название; <c>null</c> — отказались.</summary>
    public string? Chosen { get; private set; }

    /// <param name="route">Маршрут, который перекладывается.</param>
    public FolderNameWindow(string route)
    {
        InitializeComponent();

        Head.Text = $"Новая папка для «{route}»";

        Loaded += (_, _) => NameBox.Focus();
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

        Chosen = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
