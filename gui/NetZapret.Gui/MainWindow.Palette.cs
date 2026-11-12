using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Gui.Views;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>Строка палитры: что сделать, в каком разделе и что для этого сделать окну.</summary>
internal sealed record PaletteRow(string Text, string Where, string Glyph, Geometry? Shape, Action Act);

/// <summary>
/// Поиск «Что нужно сделать?» (Ctrl+K) и меню, свёрнутое до значков.
/// </summary>
/// <remarks>
/// 11.11, по образцу стенда разработчика: поиск по задачам разделов
/// (<see cref="TaskCatalog"/>, сопоставление слов — <see cref="TaskSearch"/>),
/// а вписанный сайт — сразу двумя делами: проверить его и показать его маршрут.
/// </remarks>
public partial class MainWindow
{
    /// <summary>Ширина меню: обычная и свёрнутого до значков.</summary>
    private const double RailWide = 236;

    private const double RailNarrow = 66;

    private void OnPaletteOpen(object sender, RoutedEventArgs e) => OpenPalette();

    /// <summary>
    /// Открывает палитру строкой ввода поверх строки поиска в меню.
    /// </summary>
    /// <remarks>
    /// Координаты — в сетке окна (Root), там же, где лежит слой палитры:
    /// масштаб окна из «Оформления» они переживают сами.
    /// </remarks>
    private void OpenPalette()
    {
        // Мастер первого запуска — во всё окно, меню нет, и искать в нём нечего.
        if (RailBorder.Visibility != Visibility.Visible)
            return;

        var at = SearchButton.TranslatePoint(new Point(0, 0), Root);

        PaletteBox.Width = Math.Max(320, Math.Min(560, Root.ActualWidth - at.X - 24));
        PaletteBox.Margin = new Thickness(at.X, at.Y, 0, 0);
        PaletteList.MaxHeight = Math.Max(160, Root.ActualHeight - at.Y - 100);

        PaletteInput.Text = string.Empty;
        ShowPalette(string.Empty);
        PaletteLayer.Visibility = Visibility.Visible;

        Dispatcher.BeginInvoke(() =>
        {
            PaletteInput.Focus();
            Keyboard.Focus(PaletteInput);
        }, DispatcherPriority.Input);
    }

    private void ClosePalette()
    {
        PaletteLayer.Visibility = Visibility.Collapsed;
        PaletteInput.Text = string.Empty;
    }

    /// <summary>Строки палитры для запроса: дела с сайтом — первыми, затем задачи разделов.</summary>
    private void ShowPalette(string query)
    {
        var rows = new List<PaletteRow>();

        // Сайт в запросе — слово с точкой, хоть «youtube.com», хоть «проверить youtube.com».
        // Остальные слова ищут задачи как обычно.
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var siteWord = words.FirstOrDefault(w => w.Contains('.'));

        if (siteWord is not null && SiteFixer.HostOf(siteWord) is { } host)
        {
            rows.Add(Row($"Проверить {host}", "check", () => CheckHost(host)));
            rows.Add(Row($"Показать маршрут {host}", "routes", () => RevealRoute(host)));
            query = string.Join(' ', words.Where(w => w != siteWord));
        }

        if (siteWord is null || query.Trim().Length > 0)
        {
            foreach (var task in TaskSearch.Find(TaskCatalog.All, t => t.Text, t => $"{SectionTitle(t.Section)} {t.Keywords}", query))
                rows.Add(Row(task.Text, task.Section, () => Open(task.Section)));
        }

        PaletteList.ItemsSource = rows;
        PaletteList.SelectedIndex = rows.Count > 0 ? 0 : -1;
        PaletteList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        PaletteEmpty.Visibility = rows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        if (rows.Count > 0)
            PaletteList.ScrollIntoView(rows[0]);
    }

    /// <summary>Строка палитры со значком и названием раздела — теми же, что в меню.</summary>
    private PaletteRow Row(string text, string section, Action act)
    {
        var item = RailItem(section);

        return new PaletteRow(
            text,
            SectionTitle(section),
            item is null ? string.Empty : MenuIcon.GetGlyph(item) ?? string.Empty,
            item is null ? null : MenuIcon.GetShape(item),
            act);
    }

    private RadioButton? RailItem(string section) =>
        Rail.Children.OfType<RadioButton>().FirstOrDefault(r => r.Tag as string == section);

    private string SectionTitle(string section) => RailItem(section)?.Content as string ?? section;

    private void Pick(PaletteRow? row)
    {
        if (row is null)
            return;

        ClosePalette();
        row.Act();
    }

    /// <summary>
    /// Проверить сайт в «Проверке блокировок» — тем же путём, что Enter в её поиске.
    /// </summary>
    /// <remarks>
    /// Раздел на экране — сразу ему; иначе заявкой: раздел создаётся заново при
    /// переходе, и проверку он начнёт, когда встанет (<see cref="CheckView.PendingHost"/>).
    /// </remarks>
    private void CheckHost(string host)
    {
        if (Section.Visibility == Visibility.Visible && Section.Content is CheckView shown)
        {
            shown.CheckHost(host);
            return;
        }

        CheckView.PendingHost = host;
        Open("check");
    }

    /// <summary>Показать в «Маршрутах» часть, чьё правило решает это имя, — как щелчок по имени в проверке.</summary>
    private void RevealRoute(string host)
    {
        if (RoutesHost.Visibility == Visibility.Visible)
        {
            RoutesView.Shared(RoutesHost).RevealNow(host);
            return;
        }

        RoutesView.PendingReveal = host;
        Open("routes");
    }

    private void OnPaletteText(object sender, TextChangedEventArgs e)
    {
        if (PaletteLayer.Visibility == Visibility.Visible)
            ShowPalette(PaletteInput.Text);
    }

    /// <summary>Стрелки ходят по списку, не уводя ввод из строки; Enter — выбранное.</summary>
    private void OnPaletteKey(object sender, KeyEventArgs e)
    {
        int count = PaletteList.Items.Count;

        switch (e.Key)
        {
            case Key.Down when count > 0:
                PaletteList.SelectedIndex = Math.Min(PaletteList.SelectedIndex + 1, count - 1);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Up when count > 0:
                PaletteList.SelectedIndex = Math.Max(PaletteList.SelectedIndex - 1, 0);
                PaletteList.ScrollIntoView(PaletteList.SelectedItem);
                e.Handled = true;
                break;

            case Key.Enter:
                Pick(PaletteList.SelectedItem as PaletteRow);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Мышь ведёт выбор за собой — подсвечена всегда одна строка, та, что выберет Enter.</summary>
    private void OnPaletteHover(object sender, MouseEventArgs e)
    {
        if (sender is ListBoxItem item)
            item.IsSelected = true;
    }

    private void OnPaletteClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(PaletteList, e.OriginalSource as DependencyObject) is ListBoxItem item)
            Pick(item.DataContext as PaletteRow);
    }

    /// <summary>Щелчок мимо карточки закрывает палитру.</summary>
    private void OnPaletteBackdrop(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == PaletteLayer)
            ClosePalette();
    }

    /// <summary>Ctrl+K — открыть и закрыть палитру откуда угодно; Esc — закрыть.</summary>
    private void OnWindowKey(object sender, KeyEventArgs e)
    {
        bool open = PaletteLayer.Visibility == Visibility.Visible;

        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;

            if (open)
                ClosePalette();
            else
                OpenPalette();
        }
        else if (e.Key == Key.Escape && open)
        {
            e.Handled = true;
            ClosePalette();
        }
    }

    /// <summary>
    /// Эмблема сворачивает и разворачивает меню (13.11, владелец; 12.11 она
    /// вела на «Главную», та — первым пунктом меню).
    /// </summary>
    private void OnLogoClick(object sender, MouseButtonEventArgs e) => SetRail(!RailCompact.GetIsOn(RailBorder));

    private void OnRailToggle(object sender, RoutedEventArgs e) => SetRail(!RailCompact.GetIsOn(RailBorder));

    /// <summary>Свернуть или развернуть меню — с движением — и запомнить.</summary>
    private void SetRail(bool compact)
    {
        ApplyRail(compact, animate: true);

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { RailCompact = compact }).Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не записалось — меню свёрнуто до конца работы окна,
            // а при следующем запуске встанет как было. Ради этого не мешать.
        }
    }

    /// <summary>Сворачивание и разворот меню — длительность движения.</summary>
    private static readonly Duration RailMotion = new(TimeSpan.FromMilliseconds(220));

    /// <summary>
    /// Меню во всю ширину или только значками.
    /// </summary>
    /// <remarks>
    /// <para>
    /// 13.11 (владелец, два снимка поверх друг друга): при сворачивании ничего
    /// не сдвигается — ни значки, ни эмблема, ни поиск. Всё в меню стоит на одной
    /// оси (x 33): подложки пунктов и строка поиска с 8, значки с 24, эмблема
    /// с 13; свёрнутое меню — 66, ровно вдвое этой оси (было 58 — владелец
    /// попросил шире). Сворачивание только отрезает полосу
    /// справа и прячет подписи — до 13.11 свёрнутое меню было 72 и ставило
    /// значки посередине, и они съезжали на 7 точек вправо, эмблема — влево.
    /// </para>
    /// <para>
    /// Движение — шириной полосы (220 мс): разворот сразу показывает подписи,
    /// и полоса открывает их, как шторка; сворачивание прячет подписи в конце.
    /// Без анимаций (Motion.Enabled) — сразу.
    /// </para>
    /// </remarks>
    private void ApplyRail(bool compact, bool animate = false)
    {
        double target = compact ? RailNarrow : RailWide;
        bool moving = animate && Motion.Enabled && RailBorder.IsLoaded && Math.Abs(RailBorder.ActualWidth - target) > 1;

        RailBorder.ClipToBounds = true;

        if (!compact || !moving)
            ShowRailParts(compact);

        if (!moving)
        {
            RailBorder.BeginAnimation(WidthProperty, null);
            RailBorder.Width = target;
            return;
        }

        var motion = new System.Windows.Media.Animation.DoubleAnimation(RailBorder.ActualWidth, target, RailMotion)
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };

        motion.Completed += (_, _) =>
        {
            RailBorder.BeginAnimation(WidthProperty, null);
            RailBorder.Width = target;

            if (compact)
                ShowRailParts(true);
        };

        RailBorder.BeginAnimation(WidthProperty, motion);
    }

    /// <summary>Подписи и то, что в свёрнутом меню не помещается; места значков не трогает.</summary>
    private void ShowRailParts(bool compact)
    {
        RailCompact.SetIsOn(RailBorder, compact);

        LogoText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        RailCollapse.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        LogoMark.ToolTip = compact ? "Развернуть меню" : "Свернуть меню";

        // Отступ эмблемы до имени — только при имени: в свёрнутом меню колонке
        // эмблемы не хватало места, и раскладка срезала ей правые углы (13.11).
        LogoMark.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 13, 0);

        SearchLabel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchChip.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchButton.Margin = compact ? new Thickness(8, 0, 8, 10) : new Thickness(8, 0, 10, 10);

        // Лупе в свёрнутой строке поля 16 по краям оставляют 14,6 на знак в 14,4,
        // и раскладка срезала ей край (12,8 из 14,4 при 125 %, 13.11). Правое
        // поле она может занять — лупа стоит слева и с места не сдвигается.
        SearchContent.Margin = compact ? new Thickness(0, 0, -12, 0) : new Thickness(0);

        foreach (var item in Rail.Children.OfType<RadioButton>())
            item.ToolTip = compact ? item.Content : null;
    }
}
