using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Themes;

namespace NetZapret.Gui.Views;

/// <summary>
/// Оформление: тема, фон, шрифт, размер, меню в трее.
/// </summary>
/// <remarks>
/// Вынесено из «Ещё» своим разделом 24.09 по просьбе владельца. Код
/// перенесён как был; своего здесь только сборка раздела при заходе.
/// </remarks>
public partial class AppearanceView : UserControl
{
    public AppearanceView()
    {
        InitializeComponent();

        Loaded += (_, _) => ShowTheme(AppSettings.Load(AppSettings.DefaultPath));
    }

    /// <summary>
    /// Плитки тем: встроенные первыми, затем из папки themes\.
    /// </summary>
    private void ShowTheme(AppSettings settings)
    {
        var loads = ThemeLoader.LoadAll();
        var current = Themes.Current;

        ThemeList.ItemsSource = loads
            .Select(load => ThemeTile.From(load, load.Id == current, key => (Brush)FindResource(key)))
            .ToList();

        var bad = loads.Where(l => !l.Ok).ToList();

        ThemeHint.Text = @"Своя тема — папка в themes\ рядом с программой: цвета, шрифты и фон. "
            + @"Формат описан в themes\README.md."
            + (bad.Count == 0
                ? string.Empty
                : $" Не применяются: {string.Join(", ", bad.Select(b => b.Theme?.Name ?? b.Id))} — "
                  + "наведите на плитку, чтобы увидеть почему.");

        // Настройка просила одну тему, а применилась другая — сказать сразу,
        // иначе человек решит, что выбор не сохранился.
        if (settings.Theme is { Length: > 0 } wanted && wanted != current)
            Status.Text = $"Тема «{wanted}» не применилась — стоит встроенная. Причина — в подсказке её плитки.";

        ShowLook(settings, loads.FirstOrDefault(l => l.Id == current)?.Theme);

        // У встроенной «Изменить» создаёт новую — подпись говорит это заранее.
        EditThemeButton.Content = ThemeLoader.Shipped.Contains(current) ? "Изменить копию" : "Изменить";
        ShowFonts(settings);
        ShowTray(settings);
    }

    /// <summary>Ступени плотности подложки меню трея, в процентах.</summary>
    private static readonly int[] TrayDensities = [100, 90, 80, 70, 60, 50, 40, 30];

    private void ShowTray(AppSettings settings)
    {
        _showingLook = true;

        try
        {
            TrayBlurSwitch.IsChecked = settings.TrayBlur;

            TrayDensityChoice.ItemsSource = TrayDensities
                .Select(d => d == 100 ? "сплошная" : $"{d} %")
                .ToList();

            TrayDensityChoice.SelectedIndex = TrayDensities
                .Select((d, i) => (Distance: Math.Abs(d - settings.TrayDensity), Index: i))
                .MinBy(x => x.Distance).Index;
        }
        finally
        {
            _showingLook = false;
        }
    }

    // Тему не пересобираем: меню читает настройки само при каждом открытии.
    private void OnTrayBlur(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        SaveTray(settings with { TrayBlur = !settings.TrayBlur });
    }

    private void OnTrayDensity(object sender, SelectionChangedEventArgs e)
    {
        if (_showingLook || TrayDensityChoice.SelectedIndex is not (>= 0 and var i) || i >= TrayDensities.Length)
            return;

        SaveTray(AppSettings.Load(AppSettings.DefaultPath) with { TrayDensity = TrayDensities[i] });
    }

    private void SaveTray(AppSettings settings)
    {
        try
        {
            settings.Save(AppSettings.DefaultPath);
            ShowTray(settings);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сохранить: " + ex.GetBaseException().Message;
        }
    }

    private bool _showingLook;

    /// <summary>Настройки оформления — только если у темы есть картинка фона.</summary>
    private void ShowLook(AppSettings settings, Theme? theme)
    {
        bool hasBackground = theme?.Background is not null;

        LookPanel.Visibility = hasBackground ? Visibility.Visible : Visibility.Collapsed;
        LookNone.Visibility = hasBackground ? Visibility.Collapsed : Visibility.Visible;

        BackgroundSwitch.IsChecked = settings.ThemeBackgroundShown;
        BlurSwitch.IsChecked = settings.ThemeBlur;

        // Размытию и затемнению без картинки делать нечего — видны,
        // но приглушены, чтобы было понятно, от чего они зависят.
        BlurRow.IsEnabled = settings.ThemeBackgroundShown;
        DimRow.IsEnabled = settings.ThemeBackgroundShown;
        DimRow.Opacity = settings.ThemeBackgroundShown ? 1 : 0.5;

        _showingLook = true;
        DimChoice.SelectedIndex = Math.Clamp(settings.ThemeDimSteps, 0, 2);
        _showingLook = false;
    }

    private const string ThemeFont = "как в теме";

    /// <summary>Шрифт в списке: имя и само начертание — выбирают глазами.</summary>
    public sealed record FontItem(string Name, FontFamily Family)
    {
        public override string ToString() => Name;
    }

    /// <summary>Установленные шрифты — один раз на запуск: перечень не меняется, а читается долго.</summary>
    private static readonly Lazy<IReadOnlyList<FontItem>> SystemFonts = new(() =>
        Fonts.SystemFontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f.Source))
            .GroupBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FontItem(g.Key, g.First()))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    /// <summary>Списки шрифтов и размеров — с выбранным сейчас.</summary>
    private void ShowFonts(AppSettings settings)
    {
        _showingLook = true;

        try
        {
            var fonts = new List<FontItem> { new(ThemeFont, (FontFamily)FindResource("UiFont")) };
            fonts.AddRange(SystemFonts.Value);

            UiFontChoice.ItemsSource = fonts;
            UiFontChoice.SelectedItem = fonts.FirstOrDefault(f =>
                string.Equals(f.Name, settings.UiFont, StringComparison.OrdinalIgnoreCase)) ?? fonts[0];

            var scales = Appearance.Scales.Select(s => $"{s * 100:0} %").ToList();
            ScaleChoice.ItemsSource = scales;

            int nearest = Appearance.Scales
                .Select((s, i) => (Distance: Math.Abs(s - settings.UiScale), Index: i))
                .MinBy(x => x.Distance).Index;

            ScaleChoice.SelectedIndex = nearest;
        }
        finally
        {
            _showingLook = false;
        }
    }

    private void OnFontChoice(object sender, SelectionChangedEventArgs e)
    {
        if (_showingLook)
            return;

        static string? Pick(ComboBox box) =>
            box.SelectedItem is FontItem { Name: var name } && name != ThemeFont ? name : null;

        var scale = ScaleChoice.SelectedIndex is int i && i >= 0 && i < Appearance.Scales.Count
            ? Appearance.Scales[i]
            : 1.0;

        ApplyLook(AppSettings.Load(AppSettings.DefaultPath) with
        {
            UiFont = Pick(UiFontChoice),
            UiScale = scale,
        });
    }

    private void OnLook(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ApplyLook(key == "blur"
            ? settings with { ThemeBlur = !settings.ThemeBlur }
            : settings with { ThemeBackgroundShown = !settings.ThemeBackgroundShown });
    }

    private void OnDim(object sender, SelectionChangedEventArgs e)
    {
        if (_showingLook || DimChoice.SelectedIndex < 0)
            return;

        ApplyLook(AppSettings.Load(AppSettings.DefaultPath) with { ThemeDimSteps = DimChoice.SelectedIndex });
    }

    /// <summary>Записывает и сразу применяет: тема пересобирается с новыми настройками.</summary>
    private void ApplyLook(AppSettings settings)
    {
        try
        {
            settings.Save(AppSettings.DefaultPath);

            var result = Themes.Apply(Themes.Current, settings);

            Status.Text = result.Ok
                ? string.Empty
                : "Тема не применилась с этими настройками: " + string.Join("; ", result.Problems.Take(2));

            ShowTheme(settings);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось применить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Применяет тему сразу, без перезапуска.
    /// </summary>
    /// <remarks>
    /// Подменяется один словарь ресурсов, а стили опираются только на его
    /// ключи. Записывается в настройки только применившаяся тема: иначе
    /// следующий запуск молча открылся бы встроенной, а в настройках
    /// значилась бы та, что не прошла проверку.
    /// </remarks>
    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        try
        {
            var result = Themes.Apply(id);

            if (result.Ok)
            {
                (AppSettings.Load(AppSettings.DefaultPath) with { Theme = id })
                    .Save(AppSettings.DefaultPath);

                Status.Text = string.Empty;
            }
            else
            {
                Status.Text = $"Тема «{id}» не применена: {string.Join("; ", result.Problems.Take(3))}"
                    + (result.Problems.Count > 3 ? $" и ещё {result.Problems.Count - 3}." : ".");
            }

            ShowTheme(AppSettings.Load(AppSettings.DefaultPath));
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сменить тему: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Тема, выбранная сейчас, — с чего начинает редактор.</summary>
    private static Theme? CurrentTheme()
    {
        var load = ThemeLoader.Load(Themes.Current);

        return load.Theme ?? ThemeLoader.Load(Themes.DefaultId).Theme;
    }

    private void OnCreateTheme(object sender, RoutedEventArgs e) => OpenEditor(editing: false);

    private void OnEditTheme(object sender, RoutedEventArgs e) => OpenEditor(editing: true);

    /// <summary>
    /// Редактор темы. «Изменить» у встроенной ведёт себя как «Создать»:
    /// встроенные не перезаписываются, и сказать это лучше заранее.
    /// </summary>
    private void OpenEditor(bool editing)
    {
        if (CurrentTheme() is not { } theme)
        {
            Status.Text = "Не нашлось темы, с которой начать: папки themes нет рядом с программой.";
            return;
        }

        bool shipped = ThemeLoader.Shipped.Contains(theme.Id);

        var window = new ThemeEditorWindow(theme, editing && !shipped ? theme.Id : null)
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() == true && window.SavedId is { } id)
        {
            Status.Text = window.SavedAsCopy
                ? $"Правка не читается — сохранена черновиком в themes\\{id}, рабочая тема не тронута."
                : editing && shipped
                    ? $"Встроенная тема не меняется — правка сохранена новой: themes\\{id}."
                    : $"Сохранено в themes\\{id} и применено.";
        }

        ShowTheme(AppSettings.Load(AppSettings.DefaultPath));
    }

    private void OnExportTheme(object sender, RoutedEventArgs e)
    {
        var id = Themes.Current;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Выгрузить тему",
            FileName = $"netzapret-{id}.zip",
            Filter = "Тема NetZapret (*.zip)|*.zip",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            ThemeWriter.Export(id, dialog.FileName);
            Status.Text = $"Тема выгружена: {dialog.FileName}. Её можно передать — загружается кнопкой «Загрузить из файла…».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось выгрузить: " + ex.GetBaseException().Message;
        }
    }

    private void OnImportTheme(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Загрузить тему",
            Filter = "Тема NetZapret (*.zip)|*.zip",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var id = ThemeWriter.Import(dialog.FileName);
            var load = ThemeLoader.Load(id);

            Status.Text = load.Ok
                ? $"Загружена «{load.Theme!.Name}» — выберите её плиткой."
                : $"Загружена в themes\\{id}, но не применится: {string.Join("; ", load.Problems.Take(2))}.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }

        ShowTheme(AppSettings.Load(AppSettings.DefaultPath));
    }

    private void OnThemesFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ThemeLoader.DefaultRoot);
            Process.Start(new ProcessStartInfo { FileName = ThemeLoader.DefaultRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть папку тем: " + ex.GetBaseException().Message;
        }
    }

}
