using System.Windows;
using System.Windows.Input;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Спрашивает название пресета — для копии или переименования (<see cref="PresetFiles"/>).
/// </summary>
/// <remarks>Отказ — «Отмена» и крестик: файлы не трогаются.</remarks>
public partial class PresetNameWindow : Window
{
    private readonly string _directory;
    private readonly string? _except;

    /// <summary>Введённое название, без пробелов по краям; <c>null</c> — отказались.</summary>
    public string? Chosen { get; private set; }

    /// <param name="except">Файл, которому имя можно оставить — у переименования свой.</param>
    public PresetNameWindow(string head, string note, string confirm, string suggested, string directory, string? except = null)
    {
        InitializeComponent();

        _directory = directory;
        _except = except;

        Head.Text = head;
        Note.Text = note;
        Confirm.Content = confirm;
        NameBox.Text = suggested;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnKey(object sender, KeyEventArgs e) => Problem.Visibility = Visibility.Collapsed;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (PresetFiles.Problem(NameBox.Text, _directory, _except) is { } problem)
        {
            Problem.Text = problem;
            Problem.Visibility = Visibility.Visible;
            return;
        }

        Chosen = NameBox.Text.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
