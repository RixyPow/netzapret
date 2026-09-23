using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NetZapret.Core;
using NetZapret.Core.Themes;

namespace NetZapret.Gui.Views;

/// <summary>Строка цвета в редакторе темы.</summary>
public sealed record ColorRow(string Slot, string Label, string Note, ThemeColor Color, string? Warning)
{
    public Brush Swatch => new SolidColorBrush(System.Windows.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));

    public string Hex => Color.ToString();

    public Visibility WarningShown => Warning is null ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Редактор темы: цвета, фон, шрифт — видно сразу, записывается в themes\.
/// </summary>
/// <remarks>
/// <para>
/// Начинается с цветов темы, выбранной сейчас: так проще всего получить
/// «такую же, только с фиолетовой кнопкой». Правимую встроенную тему
/// ThemeWriter не перезаписывает — она уходит новой.
/// </para>
/// <para>
/// Владелец, 24.09: сменил «Фон окна» на красный, сохранил — и тема «вообще
/// сдохла»: редактор молча перезаписал рабочую Nirvana нечитаемой,
/// и программа откатилась на встроенную. Отсюда три правила. Каждая правка
/// видна на окне сразу, пока тема читается. Нечитаемый цвет отмечен в своей
/// строке, и «Поправить нечитаемое» подстраивает его светлоту. Рабочая тема
/// нечитаемой не перезаписывается — такая сохраняется копией.
/// </para>
/// </remarks>
public partial class ThemeEditorWindow : Window
{
    /// <summary>Подписи слотов словами — как их видит человек, а не как они зовутся в файле.</summary>
    private static readonly (string Slot, string Label, string Note)[] Slots =
    [
        (ThemeSlots.Backdrop, "Фон окна", "под всем; им же затемняется картинка"),
        (ThemeSlots.Surface, "Карточки", "строки, группы, меню"),
        (ThemeSlots.Raised, "Кнопки и поля", "то, что нажимают и во что пишут"),
        (ThemeSlots.Border, "Рамки", "обводки и разделители"),
        (ThemeSlots.Text, "Текст", "основной"),
        (ThemeSlots.Muted, "Пояснения и меню", "второй план"),
        (ThemeSlots.Faint, "Третий план", "необязательные подписи"),
        (ThemeSlots.AccentFill, "Главная кнопка", "заливка; надпись на ней подберётся сама"),
        (ThemeSlots.OnAccent, "Надпись на главной кнопке", "чёрная или белая — что читается"),
        (ThemeSlots.Accent, "«Работает»", "зелёный по смыслу"),
        (ThemeSlots.Danger, "«Закрыто»", "красный по смыслу"),
        (ThemeSlots.Warn, "«Внимание»", "жёлтый по смыслу"),
        (ThemeSlots.WarnFill, "Точки «внимание»", "мелкие метки"),
        (ThemeSlots.AccentDim, "Выделение в поле", "фон выделенного текста"),
    ];

    private static readonly double[] Dims = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9];
    private static readonly double[] Blurs = [0, 12, 24, 40];
    private static readonly double[] Alphas = [1, 0.9, 0.8, 0.7, 0.6];

    private const string BaseFont = "как в основе";

    private readonly Theme _from;
    private readonly Dictionary<string, ThemeColor> _colors;
    private readonly string? _editing;
    private readonly string _original;
    private readonly bool _editingWasFine;
    private string? _image;
    private bool _ready;
    private bool _saved;

    /// <summary>Тема, которую записали и применили; <c>null</c> — отменили.</summary>
    public string? SavedId { get; private set; }

    /// <summary>Сохранена копией, потому что правка не читалась.</summary>
    public bool SavedAsCopy { get; private set; }

    /// <param name="from">Тема, с которой начинаем.</param>
    /// <param name="editing">Имя папки правимой темы; <c>null</c> — новая.</param>
    public ThemeEditorWindow(Theme from, string? editing)
    {
        InitializeComponent();

        _from = from;
        _editing = editing;
        _original = Themes.Current;
        _editingWasFine = editing is not null && ThemeLoader.Load(editing).Ok;
        _colors = ThemeSlots.All.ToDictionary(s => s, s => from[s] with { A = s == ThemeSlots.Surface ? from[s].A : (byte)255 });
        _image = from.Background?.Image;

        Heading.Text = editing is null ? "Своя тема" : $"Правка темы «{from.Name}»";
        Title = Heading.Text;
        NameField.Text = editing is null ? from.Name + " — моя" : from.Name;

        DimChoice.ItemsSource = Dims.Select(d => $"{d * 100:0} %").ToList();
        AlphaChoice.ItemsSource = Alphas.Select(a => a >= 1 ? "непрозрачные" : $"{a * 100:0} %").ToList();

        var background = from.Background;
        FitChoice.SelectedIndex = (int)(background?.Fit ?? BackgroundFit.Cover);
        DimChoice.SelectedIndex = Nearest(Dims, background?.Dim ?? 0.6);
        BlurChoice.SelectedIndex = Nearest(Blurs, background?.Blur ?? 24);
        AlphaChoice.SelectedIndex = Nearest(Alphas, from[ThemeSlots.Surface].A / 255.0);

        var fonts = new List<MoreView.FontItem> { new(BaseFont, new FontFamily(from.Fonts.Ui)) };
        fonts.AddRange(Fonts.SystemFontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f.Source))
            .GroupBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new MoreView.FontItem(g.Key, g.First()))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase));

        FontChoice.ItemsSource = fonts;
        FontChoice.SelectedItem = fonts.FirstOrDefault(f => f.Name == from.Fonts.Display && f.Name != BaseFont) ?? fonts[0];

        ShowImage();

        _ready = true;
        Changed();

        Closing += OnClosing;
    }

    private static int Nearest(double[] steps, double value) =>
        steps.Select((s, i) => (Distance: Math.Abs(s - value), Index: i)).MinBy(x => x.Distance).Index;

    private string? ChosenFont => FontChoice.SelectedItem is MoreView.FontItem { Name: var name } && name != BaseFont ? name : null;

    /// <summary>Цвета с плотностью карточек из выбора — как они запишутся.</summary>
    private Dictionary<string, ThemeColor> Draft()
    {
        var colors = new Dictionary<string, ThemeColor>(_colors);

        // Плотность — только у карточек и только с картинкой: у сплошного
        // фона прозрачная карточка просто темнее, и смысла в этом нет.
        colors[ThemeSlots.Surface] = _image is not null && AlphaChoice.SelectedIndex >= 0
            ? colors[ThemeSlots.Surface] with { A = (byte)Math.Round(Alphas[AlphaChoice.SelectedIndex] * 255) }
            : colors[ThemeSlots.Surface] with { A = 255 };

        return colors;
    }

    /// <summary>Тема в памяти — такая, какой она запишется.</summary>
    private Theme Preview() => new()
    {
        Id = _editing ?? "preview",
        Name = NameField.Text.Trim(),
        Folder = _from.Folder,
        Colors = Draft(),
        Fonts = ChosenFont is { } font
            ? new ThemeFonts { Ui = font, Display = font, Mono = _from.Fonts.Mono }
            : _from.Fonts,
        Background = _image is null
            ? null
            : new ThemeBackground
            {
                Image = _image,
                Fit = (BackgroundFit)Math.Max(0, FitChoice.SelectedIndex),
                Dim = Dims[Math.Max(0, DimChoice.SelectedIndex)],
                Blur = Blurs[Math.Max(0, BlurChoice.SelectedIndex)],
            },
    };

    /// <summary>
    /// Любая правка: отметить нечитаемое в строках и показать тему на окне.
    /// </summary>
    private void Changed()
    {
        if (!_ready)
            return;

        var draft = Draft();
        var failing = ThemeFixer.Failing(draft);

        ColorRows.ItemsSource = Slots
            .Select(s => new ColorRow(s.Slot, s.Label, s.Note, draft[s.Slot], failing.GetValueOrDefault(s.Slot)))
            .ToList();

        FixButton.Visibility = failing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var problems = Themes.Preview(Preview());

        Say(problems.Count == 0
            ? string.Empty
            : "На окне — последняя читаемая версия. Не читается: "
              + string.Join("; ", problems.Take(2)).Replace("не читается: ", string.Empty)
              + (problems.Count > 2 ? $" и ещё {problems.Count - 2}." : "."));

        // Картинка тоже может не читаться — тогда поправить цвета мало,
        // и «Поправить» стоит показать всё равно: светлее текст — читаемее.
        if (problems.Count > 0)
            FixButton.Visibility = Visibility.Visible;
    }

    private void OnChanged(object sender, SelectionChangedEventArgs e) => Changed();

    private void ShowImage()
    {
        ImageName.Text = _image is null ? "нет — фон сплошной, цветом «Фон окна»" : Path.GetFileName(_image);
        ClearImage.IsEnabled = _image is not null;
        ImageSettings.Visibility = _image is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnColor(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot })
            return;

        var label = Slots.First(s => s.Slot == slot).Label;
        var picker = new ColorPickerWindow(_colors[slot], label) { Owner = this };

        if (picker.ShowDialog() != true)
            return;

        _colors[slot] = picker.Chosen with { A = _colors[slot].A };

        // Подпись на главной кнопке — следом за заливкой: иначе первая же
        // светлая заливка с белой подписью не прошла бы проверку.
        if (slot == ThemeSlots.AccentFill)
            _colors[ThemeSlots.OnAccent] = ColorMath.ReadableOn(_colors[slot]);

        Changed();
    }

    /// <summary>
    /// Подстраивает светлоту нечитаемых цветов, не трогая выбранное.
    /// </summary>
    /// <remarks>
    /// Если не читается текст на картинке, а не на цветах, — поднимает
    /// затемнение по ступени, пока не прочтётся: выбранную картинку
    /// менять нельзя, а затемнение и есть то, чем тема читается на ней.
    /// </remarks>
    private void OnFix(object sender, RoutedEventArgs e)
    {
        foreach (var (slot, color) in ThemeFixer.Fix(_colors))
            _colors[slot] = color with { A = _colors[slot].A };

        if (_image is not null)
        {
            _ready = false;

            while (Themes.Preview(Preview()).Count > 0 && DimChoice.SelectedIndex < Dims.Length - 1)
                DimChoice.SelectedIndex++;

            _ready = true;
        }

        Changed();
    }

    private void OnPickImage(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Картинка фона",
            Filter = "Картинки (PNG, JPG)|*.png;*.jpg;*.jpeg",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (new FileInfo(dialog.FileName).Length > ThemeLoader.MaxImageBytes)
        {
            Say($"Картинка больше {ThemeLoader.MaxImageBytes / 1024 / 1024} МБ — возьмите поменьше.");
            return;
        }

        _image = dialog.FileName;
        ShowImage();
        Changed();
    }

    private void OnClearImage(object sender, RoutedEventArgs e)
    {
        _image = null;
        ShowImage();
        Changed();
    }

    private void Say(string text)
    {
        Problems.Text = text;
        Problems.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Записывает тему и применяет её.
    /// </summary>
    /// <remarks>
    /// Нечитаемая записывается только по согласию и никогда поверх рабочей:
    /// правка рабочей темы, которая не читается, уходит копией. Так было
    /// с Nirvana 24.09 — перезаписанная красным фоном, она перестала
    /// применяться вовсе.
    /// </remarks>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameField.Text.Trim();

        if (name.Length == 0)
        {
            Say("Нужно название.");
            return;
        }

        var problems = Themes.Preview(Preview());
        var target = _editing;

        if (problems.Count > 0)
        {
            var answer = MessageBox.Show(
                "Тема не читается и не применится:\n\n"
                + string.Join("\n", problems.Take(4).Select(p => "• " + p.Replace("не читается: ", string.Empty)))
                + "\n\nСохранить её всё равно, чтобы доделать потом?"
                + (_editingWasFine ? " Рабочая тема не пострадает: правка ляжет копией." : string.Empty),
                "NetZapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;

            if (_editingWasFine)
            {
                target = null;
                SavedAsCopy = true;
            }
        }

        var draft = new ThemeDraft
        {
            Name = SavedAsCopy ? name + " — черновик" : name,
            Colors = Draft(),
            Font = ChosenFont,
            BackgroundSource = _image,
            Fit = (BackgroundFit)Math.Max(0, FitChoice.SelectedIndex),
            Dim = Dims[Math.Max(0, DimChoice.SelectedIndex)],
            Blur = Blurs[Math.Max(0, BlurChoice.SelectedIndex)],
        };

        try
        {
            var id = ThemeWriter.Save(draft, target);

            // Нечитаемая не применяется: остаётся то, что было до редактора.
            var result = problems.Count == 0 ? Themes.Apply(id) : Themes.Apply(_original);

            if (problems.Count == 0 && result.Ok)
                (AppSettings.Load(AppSettings.DefaultPath) with { Theme = id }).Save(AppSettings.DefaultPath);

            SavedId = id;
            _saved = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Say("Не удалось сохранить: " + ex.GetBaseException().Message);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Закрыли без сохранения — вернуть тему, что была до редактора.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_saved)
            Themes.Apply(_original);
    }
}
