using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using NetZapret.Core.Updates;

namespace NetZapret.Gui.Views;

/// <summary>Что решил человек в окне обновления.</summary>
public enum UpdateChoice
{
    /// <summary>«Позже» или крестик: напомнить при следующем запуске.</summary>
    Later,

    /// <summary>«Пропустить версию»: о ней больше не напоминать.</summary>
    Skip,

    /// <summary>«Обновить».</summary>
    Install,
}

/// <summary>
/// «Доступно обновление» — по образцу окна Zapret GUI (владелец, 06.10).
/// </summary>
/// <remarks>
/// <para>
/// Само ничего не ставит: возвращает выбор, а установку ведёт карточка
/// «Обновление» на «Главной» (<see cref="UpdatePanel"/>) — там прогресс
/// закачки и ошибки, и путь установки остаётся один.
/// </para>
/// <para>
/// «Что нового» — за каждую версию между установленной и новой. Список
/// выпусков спрашивается у GitHub уже при открытом окне; пока он не пришёл
/// или не пришёл вовсе, показаны примечания одной найденной версии.
/// </para>
/// </remarks>
public partial class UpdateWindow : Window
{
    /// <summary>Канал проекта — тот же, что в README.</summary>
    public const string Telegram = "https://t.me/netzapret23";

    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private readonly ReleaseInfo _latest;
    private readonly CancellationTokenSource _work = new();

    /// <summary>
    /// Выпуск новее установленного; <c>false</c> — окно открыто кнопкой
    /// «Проверить» при последней версии.
    /// </summary>
    private readonly bool _newer;

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    /// <remarks>
    /// Открывается и при последней версии — кнопкой «Проверить» (владелец,
    /// 06.10: «там такой же чейнджлог, только кнопка обновить некликабельная»).
    /// Тогда в «Что нового» — примечания последнего выпуска, а «Обновить» погашена.
    /// «Пропустить версию» тогда нет, а «Позже» — «Закрыть» (владелец, 06.10, по
    /// снимку сборки 7): пропускать нечего, и напоминать не о чем.
    /// </remarks>
    public UpdateWindow(ReleaseInfo latest)
    {
        InitializeComponent();

        _latest = latest;
        _newer = UpdateCheck.IsNewer(latest.Version, UpdateCheck.Current);
        MaxHeight = SystemParameters.WorkArea.Height - 40;

        if (!_newer)
        {
            Title = "Обновлений нет";
            Heading.Text = "Установлена последняя версия";
            InstallButton.IsEnabled = false;
            InstallButton.ToolTip = "Ставить нечего: новее этой версии на GitHub нет.";
            SkipButton.Visibility = Visibility.Collapsed;
            LaterButton.Content = "Закрыть";
        }

        BadgeVersion.Text = "v" + latest.Version;
        BadgeDate.Text = latest.Published is { } published
            ? published.ToLocalTime().ToString("d MMMM yyyy", Russian)
            : string.Empty;
        BadgeDate.Visibility = BadgeDate.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        ShowSubtitle(_newer ? 1 : 0);
        ShowReleases([latest]);
        ShowDetails();

        Loaded += async (_, _) =>
        {
            var all = await UpdateCheck.ReleasesAsync(_work.Token);

            if (all.Count == 0 || !IsLoaded)
                return;

            if (_newer)
                ShowSubtitle(UpdateCheck.Between(all, UpdateCheck.Current, latest.Version).Count);

            // В ленте — все прошлые выпуски, листаются они (владелец 06.10:
            // «чтобы кликами на версии можно было читать чейнджлоги, и мотать
            // можно было как раз эти версии»). Новее найденной — нет: окно о ней.
            ShowReleases(all.Where(r => !UpdateCheck.IsNewer(r.Version, latest.Version)).ToList());
        };

        Closed += (_, _) => _work.Cancel();
    }

    /// <summary>Установка доступна — окно предлагает новее установленного.</summary>
    internal bool OffersInstall => InstallButton.IsEnabled;

    private void ShowSubtitle(int versions)
    {
        Subtitle.Text = !_newer
            ? string.Equals(_latest.Version, UpdateCheck.Current, StringComparison.OrdinalIgnoreCase)
                ? $"{UpdateCheck.Current} — последняя версия  ·  источник: GitHub"
                : $"Установлена {UpdateCheck.Current}  ·  последняя на GitHub — {_latest.Version}  ·  источник: GitHub"
            : $"{UpdateCheck.Current} → {_latest.Version}  ·  версий в обновлении: {Math.Max(versions, 1)}"
                + "  ·  источник: GitHub";
    }

    /// <summary>Лента версий и чейнджлог выбранной в ней — сперва найденной.</summary>
    private void ShowReleases(IReadOnlyList<ReleaseInfo> releases)
    {
        _releases = releases
            .OrderByDescending(r => Version.TryParse(r.Version, out var v) ? v : new Version(0, 0))
            .ToList();

        ShowTimeline(_releases);

        var start = _releases.FirstOrDefault(r => string.Equals(r.Version, _latest.Version, StringComparison.OrdinalIgnoreCase))
            ?? _releases.FirstOrDefault();

        if (start is not null)
            Select(start.Version);
    }

    /// <summary>Выпуски в ленте, новые сверху.</summary>
    private IReadOnlyList<ReleaseInfo> _releases = [];

    /// <summary>
    /// Чейнджлог одной версии — той, что выбрана в ленте.
    /// </summary>
    /// <remarks>
    /// Версия новее установленной — цветом темы, установленная помечена:
    /// видно, принесёт ли её обновление.
    /// </remarks>
    private void ShowNotes(ReleaseInfo release)
    {
        Notes.Children.Clear();

        var head = new TextBlock { Margin = new Thickness(0, 0, 0, 4) };

        var version = new Run("v" + release.Version) { FontSize = 22, FontWeight = FontWeights.SemiBold };
        version.SetResourceReference(
            TextElement.ForegroundProperty,
            UpdateCheck.IsNewer(release.Version, UpdateCheck.Current) ? "Accent" : "Text");
        head.Inlines.Add(version);

        var tail = release.Published is { } when
            ? "  ·  " + when.ToLocalTime().ToString("d MMMM yyyy", Russian)
            : string.Empty;

        if (string.Equals(release.Version, UpdateCheck.Current, StringComparison.OrdinalIgnoreCase))
            tail += "  ·  установлена";

        if (tail.Length > 0)
        {
            var date = new Run(tail);
            date.SetResourceReference(TextElement.ForegroundProperty, "Muted");
            head.Inlines.Add(date);
        }

        Notes.Children.Add(head);

        var blocks = ReleaseNotesText.Parse(release.Notes);

        if (blocks.Count == 0)
        {
            var none = new TextBlock { Text = "Примечаний к этой версии нет.", TextWrapping = TextWrapping.Wrap };
            none.SetResourceReference(StyleProperty, "Caption");
            Notes.Children.Add(none);
        }

        foreach (var block in blocks)
            Notes.Children.Add(Render(block));

        NotesScroll.ScrollToTop();
    }

    /// <summary>Пункты ленты версий по номеру.</summary>
    private readonly Dictionary<string, Border> _items = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Подсвеченная в ленте версия.</summary>
    private string? _shown;

    /// <summary>
    /// Лента версий справа: точка на линии, номер, пометка, дата и счёт пунктов.
    /// </summary>
    private void ShowTimeline(IReadOnlyList<ReleaseInfo> ordered)
    {
        Timeline.Children.Clear();
        _items.Clear();
        _dots.Clear();
        _shown = null;

        for (int i = 0; i < ordered.Count; i++)
        {
            var release = ordered[i];
            bool newer = UpdateCheck.IsNewer(release.Version, UpdateCheck.Current);
            bool installed = string.Equals(release.Version, UpdateCheck.Current, StringComparison.OrdinalIgnoreCase);
            bool found = string.Equals(release.Version, _latest.Version, StringComparison.OrdinalIgnoreCase) && _newer;

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            // Линия — двумя кусками: сверху до точки и от точки вниз. У первой
            // нет верхнего, у последней — нижнего, и лента не торчит за края.
            //
            // Без округления по пикселям: при масштабе Windows 125 % (у владельца
            // окно в 1000 точек — ~1220 пикселей) округление ставило точку,
            // обводку и линию разной ширины с разницей до пикселя, и кружок
            // съезжал с линии (снимок 07.10). Неокруглённые фигуры сглаживаются,
            // зато центры у всех одни.
            var rail = new Grid { UseLayoutRounding = false, SnapsToDevicePixels = false };

            if (i > 0)
                rail.Children.Add(Line(top: true));

            if (i < ordered.Count - 1)
                rail.Children.Add(Line(top: false));

            // Круглые версии (0.12.0, 0.13.0) — крупным кружком, вехами
            // (владелец 06.10); новая и установленная — ещё и в обводке.
            bool round = IsRound(release.Version);
            bool marked = found || installed;
            double size = round ? 14 : 8;

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, Center - size / 2, 0, 0),
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, newer || installed ? "Accent" : "Muted");

            if (marked)
            {
                double ring = size + 10;

                var halo = new System.Windows.Shapes.Ellipse
                {
                    Width = ring,
                    Height = ring,
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, Center - ring / 2, 0, 0),
                    StrokeThickness = 1.5,
                };
                halo.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Accent");
                halo.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Surface");
                rail.Children.Add(halo);
            }

            rail.Children.Add(dot);
            row.Children.Add(rail);

            var text = new StackPanel { Margin = new Thickness(4, 4, 8, 10) };

            var top = new StackPanel { Orientation = Orientation.Horizontal };

            var number = new TextBlock { Text = "v" + release.Version, FontSize = round ? 16 : 14, FontWeight = FontWeights.SemiBold };
            number.SetResourceReference(TextBlock.ForegroundProperty, newer ? "Accent" : "Text");
            top.Children.Add(number);

            if (found || installed)
                top.Children.Add(Pill(found ? "Новая" : "Установлена", accent: found));

            text.Children.Add(top);

            if (release.Published is { } when)
            {
                var date = new TextBlock { Text = when.ToLocalTime().ToString("d MMMM yyyy", Russian), Margin = new Thickness(0, 2, 0, 0) };
                date.SetResourceReference(StyleProperty, "Caption");
                text.Children.Add(date);
            }

            var (added, fixedCount) = ReleaseNotesText.Count(release.Notes);
            var counts = Summary(added, fixedCount);

            if (counts.Length > 0)
            {
                var tally = new TextBlock { Text = counts, Margin = new Thickness(0, 2, 0, 0), FontSize = 11.5 };
                tally.SetResourceReference(TextBlock.ForegroundProperty, "Faint");
                text.Children.Add(tally);
            }

            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            // Рамка есть у всех пунктов, у невыбранных прозрачная. Прежде она
            // появлялась только у выбранного, и его содержимое съезжало на точку
            // вбок — линия ленты на нём косила (снимок владельца 06.10).
            var item = new Border
            {
                Child = row,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Читать чейнджлог этой версии",
            };

            var version = release.Version;
            _dots[version] = (dot, newer || installed ? "Accent" : "Muted", size);
            item.MouseLeftButtonUp += (_, _) => Select(version);
            item.MouseEnter += (_, _) => { if (_shown != version) item.SetResourceReference(Border.BackgroundProperty, "Raised"); };
            item.MouseLeave += (_, _) => { if (_shown != version) item.Background = Brushes.Transparent; };

            _items[version] = item;
            Timeline.Children.Add(item);
        }
    }

    /// <summary>Середина точки от верха пункта — по ней ставятся точка, обводка и линия.</summary>
    private const double Center = 15;

    /// <summary>Круглая версия — x.y.0: веха, крупный кружок в ленте.</summary>
    internal static bool IsRound(string version)
    {
        var parts = version.Trim().TrimStart('v', 'V').Split('.');

        return parts.Length >= 3 && parts[2] == "0" && parts.Skip(3).All(p => p == "0");
    }

    private static Border Line(bool top)
    {
        var line = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            Height = top ? Center : double.NaN,
            Margin = top ? new Thickness(0) : new Thickness(0, Center, 0, 0),
        };
        line.SetResourceReference(Border.BackgroundProperty, "Border");
        return line;
    }

    private static Border Pill(string text, bool accent)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "OnAccent" : "Text");

        var pill = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        pill.SetResourceReference(Border.BackgroundProperty, accent ? "AccentFill" : "Raised");
        return pill;
    }

    /// <summary>«3 новых · 2 исправления» — что внутри версии, без чтения её целиком.</summary>
    internal static string Summary(int added, int fixedCount)
    {
        var parts = new List<string>();

        if (added > 0)
            parts.Add($"{added} {Plural(added, "новое", "новых", "новых")}");

        if (fixedCount > 0)
            parts.Add($"{fixedCount} {Plural(fixedCount, "исправление", "исправления", "исправлений")}");

        return string.Join(" · ", parts);
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int tens = n % 100, ones = n % 10;

        return tens is >= 11 and <= 14 ? many
            : ones == 1 ? one
            : ones is >= 2 and <= 4 ? few
            : many;
    }

    /// <summary>Показывает чейнджлог версии и отмечает её в ленте — с «Подробностей» тоже.</summary>
    private void Select(string version)
    {
        if (_releases.FirstOrDefault(r => string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase)) is not { } release)
            return;

        if (NotesTab.IsChecked != true)
        {
            NotesTab.IsChecked = true;
            OnTab(NotesTab, new RoutedEventArgs());
        }

        ShowNotes(release);
        Highlight(version);
    }

    /// <summary>Выбранная в ленте версия — для проверок.</summary>
    internal string? Selected => _shown;

    /// <summary>Точка каждой версии и её обычный цвет — чтобы погасить свечение при смене выбора.</summary>
    private readonly Dictionary<string, (System.Windows.Shapes.Ellipse Dot, string Fill, double Size)> _dots =
        new(StringComparer.OrdinalIgnoreCase);

    private void Highlight(string version)
    {
        if (string.Equals(version, _shown, StringComparison.OrdinalIgnoreCase))
            return;

        if (_shown is not null)
        {
            if (_items.TryGetValue(_shown, out var was))
            {
                was.Background = Brushes.Transparent;
                was.BorderBrush = Brushes.Transparent;
            }

            if (_dots.TryGetValue(_shown, out var dim))
            {
                dim.Dot.Effect = null;
                dim.Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, dim.Fill);
                Size(dim.Dot, dim.Size);
            }
        }

        _shown = version;

        if (_items.TryGetValue(version, out var now))
        {
            now.SetResourceReference(Border.BackgroundProperty, "Raised");
            now.SetResourceReference(Border.BorderBrushProperty, "Border");
            now.BringIntoView();
        }

        // Точка выбранной версии светится цветом темы (владелец 06.10) и чуть
        // крупнее своей: на мелкой точке в восемь пунктов свечение терялось.
        if (_dots.TryGetValue(version, out var lit))
        {
            lit.Dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Accent");
            Size(lit.Dot, lit.Size + 4);

            lit.Dot.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = TryFindResource("AccentColor") is Color accent ? accent : Colors.White,
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 1,
            };
        }
    }

    /// <summary>Размер точки — с тем же центром на линии.</summary>
    private static void Size(System.Windows.Shapes.Ellipse dot, double size)
    {
        dot.Width = size;
        dot.Height = size;
        dot.Margin = new Thickness(0, Center - size / 2, 0, 0);
    }

    /// <summary>Строка из кусков: полужирное, код моноширинным на подложке.</summary>
    private static TextBlock Inline(IEnumerable<NotesSpan> spans)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };

        foreach (var span in spans)
        {
            var run = new Run(span.Text) { FontWeight = span.Bold ? FontWeights.Bold : FontWeights.Normal };

            if (span.Code)
            {
                run.SetResourceReference(TextElement.FontFamilyProperty, "MonoFont");
                run.SetResourceReference(TextElement.BackgroundProperty, "Raised");
                run.FontSize = 12;
            }

            text.Inlines.Add(run);
        }

        return text;
    }

    private static FrameworkElement Render(NotesBlock block)
    {
        switch (block.Kind)
        {
            // Разделы — как заголовки групп в окнах настроек: малыми прописными.
            case NotesBlockKind.Heading:
                var heading = new TextBlock { Text = block.Plain.ToUpperInvariant(), Margin = new Thickness(0, 14, 0, 8) };
                heading.SetResourceReference(StyleProperty, "GroupHead");
                return heading;

            case NotesBlockKind.Code:
                var code = new TextBlock
                {
                    Text = block.Plain,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    LineHeight = 19,
                };
                code.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");

                var frame = new Border
                {
                    Child = code,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(12, 8, 12, 8),
                    Margin = new Thickness(0, 2, 0, 10),
                    BorderThickness = new Thickness(1),
                };
                frame.SetResourceReference(Border.BackgroundProperty, "Raised");
                frame.SetResourceReference(Border.BorderBrushProperty, "Border");
                return frame;

            case NotesBlockKind.Bullet:
                var row = new Grid { Margin = new Thickness(2, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());

                var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 9, 0), FontWeight = FontWeights.Bold };
                dot.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
                row.Children.Add(dot);

                var item = Inline(block.Spans);
                Grid.SetColumn(item, 1);
                row.Children.Add(item);
                return row;

            default:
                // «**Что изменилось.** Пояснение» — фраза строкой над пояснением:
                // в Bahnschrift полужирное на глаз почти не отличить (снимок
                // владельца 06.10), а заголовок пункта — сразу.
                if (block.Lead is { } lead)
                {
                    var pair = new StackPanel { Margin = new Thickness(0, 2, 0, 12) };

                    var title = new TextBlock
                    {
                        Text = lead,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 14,
                        FontWeight = FontWeights.SemiBold,
                    };
                    pair.Children.Add(title);

                    var body = block.Body.Where(s => s.Text.Trim().Length > 0).ToList();

                    if (body.Count > 0)
                    {
                        var rest = Inline(body.Select((s, i) => i == 0 ? s with { Text = s.Text.TrimStart() } : s));
                        rest.Margin = new Thickness(0, 3, 0, 0);
                        rest.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
                        pair.Children.Add(rest);
                    }

                    return pair;
                }

                var paragraph = Inline(block.Spans);
                paragraph.Margin = new Thickness(0, 2, 0, 10);
                return paragraph;
        }
    }

    /// <summary>«Подробности»: что скачается и что будет с настройками.</summary>
    private void ShowDetails()
    {
        void Line(string label, string value)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition());

            var name = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap };
            name.SetResourceReference(StyleProperty, "Caption");
            row.Children.Add(name);

            var said = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(said, 1);
            row.Children.Add(said);

            Details.Children.Add(row);
        }

        Line("Установлена", UpdateCheck.Current);
        Line(_newer ? "Новая" : "Последняя на GitHub", _latest.Version
            + (_latest.Published is { } when ? ", вышла " + when.ToLocalTime().ToString("d MMMM yyyy", Russian) : string.Empty));

        var archive = Uri.TryCreate(_latest.ArchiveUrl, UriKind.Absolute, out var url)
            ? Uri.UnescapeDataString(url.Segments[^1])
            : "архив выпуска";

        Line("Архив", _latest.ArchiveSize > 0
            ? $"{archive}, {_latest.ArchiveSize / 1024.0 / 1024.0:0} МБ"
            : archive);

        Line("Источник", $"GitHub, {UpdateCheck.Repository}");

        Line("Как пройдёт", "Сперва скачается архив — обход в это время работает, а если поднят туннель, "
            + "архив идёт через него. Потом движки остановятся, программа закроется, файлы заменятся, "
            + "и она откроется снова. Соединения оборвутся на эти секунды.");

        Line("Что сохранится", "Настройки и подписки, свои маршруты, подставленные адреса (addresses.yaml), "
            + "свой каталог адресов (catalog.yaml). Строки, дописанные вами в списки, переносятся в новые списки.");

        Line("Что заменится", "Программа, движки, пресеты, списки, базовые правила, снимок каталога Zapret "
            + "и пины напрямую.");
    }

    private void OnTab(object sender, RoutedEventArgs e)
    {
        bool notes = NotesTab.IsChecked == true;

        NotesScroll.Visibility = notes ? Visibility.Visible : Visibility.Collapsed;
        DetailsScroll.Visibility = notes ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBrowser(object sender, RoutedEventArgs e) => Open(UpdateNotice.PageOf(_latest));

    private void OnTelegram(object sender, RoutedEventArgs e) => Open(Telegram);

    private static void Open(string address)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = address, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет браузера по умолчанию — окно остаётся, ставить можно и так.
        }
    }

    private void OnSkip(object sender, RoutedEventArgs e) => Finish(UpdateChoice.Skip);

    private void OnLater(object sender, RoutedEventArgs e) => Finish(UpdateChoice.Later);

    private void OnInstall(object sender, RoutedEventArgs e) => Finish(UpdateChoice.Install);

    private void Finish(UpdateChoice choice)
    {
        Choice = choice;
        Close();
    }
}
