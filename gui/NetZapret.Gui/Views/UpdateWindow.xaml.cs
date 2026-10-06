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

    public UpdateChoice Choice { get; private set; } = UpdateChoice.Later;

    public UpdateWindow(ReleaseInfo latest)
    {
        InitializeComponent();

        _latest = latest;
        MaxHeight = SystemParameters.WorkArea.Height - 40;

        ShowReleases([latest]);
        ShowDetails();

        Loaded += async (_, _) =>
        {
            var all = await UpdateCheck.ReleasesAsync(_work.Token);
            var between = UpdateCheck.Between(all, UpdateCheck.Current, latest.Version);

            if (between.Count > 0 && IsLoaded)
                ShowReleases(between);
        };

        Closed += (_, _) => _work.Cancel();
    }

    /// <summary>Шапка и «Что нового» по списку выпусков, новые сверху.</summary>
    private void ShowReleases(IReadOnlyList<ReleaseInfo> releases)
    {
        Subtitle.Text = $"{UpdateCheck.Current} → {_latest.Version}  ·  версий в обновлении: {releases.Count}"
            + "  ·  источник: GitHub";

        Notes.Children.Clear();

        foreach (var release in releases)
        {
            var head = new TextBlock { Margin = new Thickness(0, Notes.Children.Count == 0 ? 0 : 22, 0, 6) };

            var version = new Run("v" + release.Version) { FontSize = 22, FontWeight = FontWeights.SemiBold };
            version.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            head.Inlines.Add(version);

            if (release.Published is { } when)
            {
                var date = new Run("  ·  " + when.ToLocalTime().ToString("d MMMM yyyy", Russian));
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

    private static FrameworkElement Render(NotesBlock block)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };

        foreach (var span in block.Spans)
            text.Inlines.Add(new Run(span.Text) { FontWeight = span.Bold ? FontWeights.SemiBold : FontWeights.Normal });

        switch (block.Kind)
        {
            case NotesBlockKind.Heading:
                text.FontSize = 15;
                text.FontWeight = FontWeights.SemiBold;
                text.Margin = new Thickness(0, 10, 0, 4);
                return text;

            case NotesBlockKind.Bullet:
                var row = new Grid { Margin = new Thickness(4, 2, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());

                var dot = new TextBlock { Text = "•", Margin = new Thickness(0, 0, 8, 0) };
                row.Children.Add(dot);

                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                return row;

            default:
                text.Margin = new Thickness(0, 2, 0, 6);
                return text;
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
        Line("Новая", _latest.Version
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
