using System.Windows;
using System.Windows.Media.Imaging;
using NetZapret.Core.Programs;

namespace NetZapret.Gui.Views;

/// <summary>Строка окна выбора: программа и её значок.</summary>
public sealed record ProgramItem(RunningProgram Program, BitmapImage? Icon)
{
    public string Name => Program.Name;

    public string Path => Program.Path;

    public string Letter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "·";

    public Visibility IconShown => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// Выбор программы из запущенных — как «Из списка процессов» в Happ.
/// </summary>
/// <remarks>
/// Список и значки собираются в фоне: процессов бывает три сотни, и
/// значок каждого достаётся из файла на диске. Окно открывается сразу
/// со строкой «Ищу…», список приходит следом — как в «Диагностике».
/// </remarks>
public partial class ProgramPickerWindow : Window
{
    private IReadOnlyList<ProgramItem> _all = [];

    /// <summary>Выбранная программа; <c>null</c> — отказались.</summary>
    public RunningProgram? Chosen { get; private set; }

    public ProgramPickerWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            Search.Focus();

            try
            {
                _all = await Task.Run(() => RunningPrograms.List()
                    .Select(program => new ProgramItem(program, ProgramIcons.Of(program.Path)))
                    .ToList());

                ShowList();
            }
            catch (Exception ex)
            {
                Status.Text = "Список программ не читается: " + ex.GetBaseException().Message;
            }
        };
    }

    private void OnSearch(object sender, System.Windows.Controls.TextChangedEventArgs e) => ShowList();

    private void ShowList()
    {
        var shown = _all.Where(item => RunningPrograms.Matches(item.Program, Search.Text)).ToList();

        List.ItemsSource = shown;

        Status.Text = _all.Count == 0
            ? "Ищу запущенные программы…"
            : shown.Count == 0
                ? "Ничего не подошло. Нужной программы нет среди запущенных — закройте окно и выберите её файлом."
                : string.IsNullOrWhiteSpace(Search.Text)
                    ? $"Запущено программ: {_all.Count}. Нажмите на нужную."
                    : $"Подходит: {shown.Count} из {_all.Count}.";
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProgramItem item })
        {
            Chosen = item.Program;
            DialogResult = true;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
