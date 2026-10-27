using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Ход подбора маршрута (<see cref="SiteFixer"/>): строка на опробованный шаг,
/// рецепты — под общим заголовком, итог — плашкой сверху.
/// </summary>
/// <remarks>
/// Вынесен из «Ещё», когда подбор появился и в окне пути маршрута (27.10):
/// два одинаковых хода в двух местах разошлись бы с первой же правкой.
/// </remarks>
public partial class FixProgress : UserControl
{
    private readonly ObservableCollection<FixStepRow> _rows = [];

    /// <summary>Строка-заголовок рецептов: появляется с первым рецептом, итог пишется в неё.</summary>
    private FixStepRow? _recipesRow;

    /// <summary>Нажата кнопка на плашке итога.</summary>
    public event RoutedEventHandler? Action;

    public FixProgress()
    {
        InitializeComponent();
        Steps.ItemsSource = _rows;
    }

    /// <summary>Чистит прошлый подбор и показывает ход нового.</summary>
    public void Start()
    {
        _rows.Clear();
        _recipesRow = null;

        Visibility = Visibility.Visible;
        Plate.Visibility = Visibility.Collapsed;
        NowLine.Visibility = Visibility.Visible;
        NowText.Text = "Проверяю…";
    }

    /// <summary>Что делается сейчас — строкой над шагами.</summary>
    public void Now(string text) => NowText.Text = text;

    /// <summary>Строка на опробованный шаг; рецепты — под общим заголовком с отступом.</summary>
    public void Add(SiteFixStep step)
    {
        bool skipped = step.Detail.StartsWith("не пробовал", StringComparison.Ordinal);

        if (step.Try == RouteTry.Recipes && step.Recipe is not null)
        {
            if (_recipesRow is null)
            {
                _recipesRow = new FixStepRow
                {
                    Glyph = Glyphs.Recipes,
                    Tone = Brush("Muted"),
                    Title = "Рецепты десинка",
                    Weight = FontWeights.SemiBold,
                };

                _rows.Add(_recipesRow);
            }

            _rows.Add(new FixStepRow
            {
                Glyph = step.Works ? Glyphs.Ok : Glyphs.No,
                Tone = Brush(step.Works ? "Accent" : "Danger"),
                Title = step.Recipe,
                Detail = step.Detail,
                Indent = new Thickness(24, 0, 0, 0),
            });

            int tried = _rows.Count(r => r.Indent.Left > 0);
            int worked = _rows.Count(r => r.Indent.Left > 0 && r.Glyph == Glyphs.Ok);

            _recipesRow.Detail = worked > 0 ? $"открыл {tried}-й по счёту" : $"опробовано {tried}, ни один не открыл";
            _recipesRow.Glyph = worked > 0 ? Glyphs.Ok : Glyphs.Recipes;
            _recipesRow.Tone = Brush(worked > 0 ? "Accent" : "Muted");
            return;
        }

        _rows.Add(new FixStepRow
        {
            Glyph = skipped ? Glyphs.Warning : step.Works ? Glyphs.Ok : Glyphs.No,
            Tone = Brush(skipped ? "Warn" : step.Works ? "Accent" : "Danger"),
            Title = StepTitle(step),
            Detail = step.Detail,
            Weight = FontWeights.SemiBold,
        });
    }

    /// <summary>Подбор кончился: шаги, не пришедшие по ходу, дорисовываются, строка «сейчас» прячется.</summary>
    public void Finish(IReadOnlyList<SiteFixStep> steps)
    {
        NowLine.Visibility = Visibility.Collapsed;

        if (_rows.Count == 0)
        {
            foreach (var step in steps)
                Add(step);
        }
    }

    /// <summary>Итог — плашкой над ходом.</summary>
    /// <param name="action">Надпись кнопки на плашке; <c>null</c> — без кнопки.</param>
    public void Outcome(string glyph, string tone, string head, string? note, string? action = null)
    {
        var brush = Brush(tone);

        NowLine.Visibility = Visibility.Collapsed;
        PlateGlyph.Text = glyph;
        PlateGlyph.Foreground = brush;
        Plate.BorderBrush = brush;
        PlateHead.Text = head;
        PlateNote.Text = note ?? string.Empty;
        PlateNote.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
        PlateAction.Content = action;
        PlateAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        Plate.Visibility = Visibility.Visible;
        Visibility = Visibility.Visible;
    }

    /// <summary>Что пробовали — словами для строки.</summary>
    private static string StepTitle(SiteFixStep step) => step.Try switch
    {
        null => "Как есть" + (step.Path.IndexOf('(') is var at and >= 0 ? " · " + step.Path[(at + 1)..].TrimEnd(')') : string.Empty),
        RouteTry.Direct => "Напрямую, без десинка",
        RouteTry.Preset => "Десинк пресета",
        RouteTry.Recipes => "Рецепты десинка",
        RouteTry.Vpn => "Через VPN",
        _ => step.Path,
    };

    private Brush Brush(string key) => (Brush)FindResource(key);

    private void OnPlateAction(object sender, RoutedEventArgs e) => Action?.Invoke(this, e);

    /// <summary>Знаки строк подбора — Segoe Fluent Icons (MDL2 те же коды).</summary>
    public static class Glyphs
    {
        public const string Ok = "";
        public const string No = "";
        public const string Warning = "";
        public const string Recipes = "";
    }
}

/// <summary>Строка хода подбора: значок, что пробовали, чем кончилось.</summary>
public sealed class FixStepRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _glyph = string.Empty;
    private Brush _tone = Brushes.Transparent;
    private string _detail = string.Empty;

    public string Glyph
    {
        get => _glyph;
        set => Set(ref _glyph, value);
    }

    public Brush Tone
    {
        get => _tone;
        set => Set(ref _tone, value);
    }

    public required string Title { get; init; }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public Thickness Indent { get; init; }

    public FontWeight Weight { get; init; } = FontWeights.Normal;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
