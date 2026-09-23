using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NetZapret.Core;
using NetZapret.Core.Themes;

namespace NetZapret.Gui.Views;

/// <summary>Строка цвета в редакторе темы.</summary>
public sealed record ColorRow(string Slot, string Label, string Note, ThemeColor Color)
{
    public Brush Swatch => new SolidColorBrush(System.Windows.Media.Color.FromArgb(Color.A, Color.R, Color.G, Color.B));

    public string Hex => Color.ToString();
}

/// <summary>
/// Редактор темы: цвета, фон, шрифт заголовков — и запись в themes\.
/// </summary>
/// <remarks>
/// Начинается с цветов темы, выбранной сейчас: так проще всего получить
/// «такую же, только с фиолетовой кнопкой». Правимую встроенную тему
/// ThemeWriter не перезаписывает — она уходит новой.
/// </remarks>
public partial class ThemeEditorWindow : Window
{
    /// <summary>Подписи слотов словами — как их видит человек, а не как они зовутся в файле.</summary>
    private static readonly (string Slot, string Label, string Note)[] Slots =
    [
        (ThemeSlots.Backdrop, "Фон окна", "под всем, если нет картинки"),
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

    private const string ThemeFont = "как в основе";

    private readonly Dictionary<string, ThemeColor> _colors;
    private readonly string? _editing;
    private string? _image;

    /// <summary>Тема, которую записали и применили; <c>null</c> — отменили.</summary>
    public string? SavedId { get; private set; }

    /// <param name="from">Тема, с которой начинаем.</param>
    /// <param name="editing">Имя папки правимой темы; <c>null</c> — новая.</param>
    public ThemeEditorWindow(Theme from, string? editing)
    {
        InitializeComponent();

        _editing = editing;
        _colors = ThemeSlots.All.ToDictionary(s => s, s => from[s]);
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

        var fonts = new List<string> { ThemeFont };
        fonts.AddRange(Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase));
        DisplayChoice.ItemsSource = fonts;
        DisplayChoice.SelectedItem = fonts.Contains(from.Fonts.Display) ? from.Fonts.Display : ThemeFont;

        ShowColors();
        ShowImage();
    }

    private static int Nearest(double[] steps, double value) =>
        steps.Select((s, i) => (Distance: Math.Abs(s - value), Index: i)).MinBy(x => x.Distance).Index;

    private void ShowColors() =>
        ColorRows.ItemsSource = Slots.Select(s => new ColorRow(s.Slot, s.Label, s.Note, _colors[s.Slot])).ToList();

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

        ShowColors();
        Problems.Visibility = Visibility.Collapsed;
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
    }

    private void OnClearImage(object sender, RoutedEventArgs e)
    {
        _image = null;
        ShowImage();
    }

    private void Say(string text)
    {
        Problems.Text = text;
        Problems.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Записывает тему и применяет её; не прошедшую проверку — называет.
    /// </summary>
    /// <remarks>
    /// Записывается и не прошедшая: работа человека не должна пропадать
    /// из-за одной нечитаемой пары. Она остаётся в списке приглушённой,
    /// и её можно открыть и поправить.
    /// </remarks>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        var name = NameField.Text.Trim();

        if (name.Length == 0)
        {
            Say("Нужно название.");
            return;
        }

        var colors = new Dictionary<string, ThemeColor>(_colors);

        // Плотность карточек — прозрачность цвета карточек; у остального
        // её нет: текст полупрозрачным не бывает.
        if (_image is not null && AlphaChoice.SelectedIndex >= 0)
            colors[ThemeSlots.Surface] = colors[ThemeSlots.Surface] with { A = (byte)Math.Round(Alphas[AlphaChoice.SelectedIndex] * 255) };
        else
            colors[ThemeSlots.Surface] = colors[ThemeSlots.Surface] with { A = 255 };

        var draft = new ThemeDraft
        {
            Name = name,
            Colors = colors,
            DisplayFont = DisplayChoice.SelectedItem is string font && font != ThemeFont ? font : null,
            BackgroundSource = _image,
            Fit = (BackgroundFit)Math.Max(0, FitChoice.SelectedIndex),
            Dim = Dims[Math.Max(0, DimChoice.SelectedIndex)],
            Blur = Blurs[Math.Max(0, BlurChoice.SelectedIndex)],
        };

        try
        {
            var id = ThemeWriter.Save(draft, _editing);
            var result = Themes.Apply(id);

            if (!result.Ok)
            {
                Say("Сохранено, но не применено: " + string.Join("; ", result.Problems.Take(3))
                    + (result.Problems.Count > 3 ? $" и ещё {result.Problems.Count - 3}." : "."));
                return;
            }

            (AppSettings.Load(AppSettings.DefaultPath) with { Theme = id }).Save(AppSettings.DefaultPath);

            SavedId = id;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            Say("Не удалось сохранить: " + ex.GetBaseException().Message);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
