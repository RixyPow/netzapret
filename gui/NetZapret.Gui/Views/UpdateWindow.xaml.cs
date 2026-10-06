using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
        }

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

            // Ниже найденной — все прошлые выпуски, листать вниз (владелец
            // 06.10: «возможность мотать вниз и видеть другие версии»).
            // Выпуски новее найденной не показываются: окно о ней.
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

    /// <summary>«Что нового» по списку выпусков, новые сверху.</summary>
    /// <remarks>
    /// Версии новее установленной — цветом темы, установленная помечена,
    /// прошлые — обычным цветом: видно, где кончается то, что принесёт
    /// обновление.
    /// </remarks>
    private void ShowReleases(IReadOnlyList<ReleaseInfo> releases)
    {
        Notes.Children.Clear();

        var ordered = releases
            .OrderByDescending(r => Version.TryParse(r.Version, out var v) ? v : new Version(0, 0))
            .ToList();

        foreach (var release in ordered)
        {
            bool first = Notes.Children.Count == 0;

            if (!first)
            {
                var line = new Border { Margin = new Thickness(0, 22, 0, 18) };
                line.SetResourceReference(StyleProperty, "RowLine");
                Notes.Children.Add(line);
            }

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
        }
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
