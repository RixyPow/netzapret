using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Одна строка отчёта диагностики.</summary>
/// <remarks>
/// Цвет — именем ресурса, а кисть берётся при отрисовке: проверки идут
/// в фоне (замер 28.09: 0,4 с), а ресурс окна из фона не прочитать.
/// </remarks>
public sealed record DoctorLine(string Text, string Kind, DoctorLevel Level, string Section,
    string? ActionLabel = null, Action? Act = null)
{
    public Brush Color => (Brush)Application.Current.FindResource(Kind);

    /// <summary>Имя раздела в строке — только в общем списке; в раскрытой плитке он и так виден.</summary>
    public bool ShowSection { get; init; }

    public Visibility SectionShown => ShowSection ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Плитка раздела проверок.</summary>
public sealed record DoctorTile(string Title, string Glyph, DoctorLevel Level, IReadOnlyList<DoctorLine> Lines)
{
    public bool Open { get; init; }

    /// <summary>Фигура вместо знака <see cref="Glyph"/>; null — знак.</summary>
    public Geometry? Shape { get; init; }

    public double Angle => Chevrons.Angle(Open);

    public Brush Color => (Brush)Application.Current.FindResource(DoctorView.KindOf(Level));

    /// <summary>Значок — цветом только при замечании: десять зелёных значков шумят.</summary>
    public Brush GlyphColor => Level == DoctorLevel.Ok
        ? (Brush)Application.Current.FindResource("Muted")
        : Color;

    public string State
    {
        get
        {
            int count = Lines.Count(l => l.Level == Level);

            return Level switch
            {
                DoctorLevel.Bad => count > 1 ? $"Поломок: {count}" : "Поломка",
                DoctorLevel.Warn => count > 1 ? $"Оговорок: {count}" : "Оговорка",
                _ => "В порядке",
            };
        }
    }

    /// <summary>
    /// Первая строка худшего уровня — так, как её пишет <see cref="Doctor"/>.
    /// Своих коротких слов окно не выдумывает: плитка и отчёт говорили бы разное.
    /// </summary>
    public string Detail => Lines.FirstOrDefault(l => l.Level == Level)?.Text ?? string.Empty;
}

/// <summary>Звено цепочки ответа «куда пойдёт соединение».</summary>
public sealed record RouteLink(string Glyph, string Label, string Value, string? Note, string Kind, bool Empty = false)
{
    public Brush Color => (Brush)Application.Current.FindResource(Kind);

    public Brush ValueColor => (Brush)Application.Current.FindResource(Empty ? "Faint" : Kind == "Muted" ? "Text" : Kind);

    public Visibility NoteShown => string.IsNullOrEmpty(Note) ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Диагностика: всё, от чего зависит работа, разом.
/// </summary>
/// <remarks>
/// <para>
/// Сами проверки — в <see cref="Doctor"/>: те же уходят в отчёт для разбора
/// и в <c>nz doctor</c>. Здесь только сводка, плитки, цвета и кнопка у строки.
/// </para>
/// <para>
/// Здесь же вопрос «куда пойдёт это имя». Он задаётся ровно тогда, когда
/// сервис уходит не туда, то есть в ту же минуту, когда открывают этот
/// раздел, — и разносить их по разным местам значило бы заставлять человека
/// с одной бедой ходить по двум.
/// </para>
/// <para>
/// Вид — по макету владельца 19.10 (сводка, цепочка, плитки, подробности
/// с отбором), с правками: десинк в цепочке — цветом оговорки, а не поломки;
/// плитка «Проверок» без галочки; ни страны выхода, ни версий движков,
/// которых проверки не знают.
/// </para>
/// </remarks>
public partial class DoctorView : UserControl
{
    private IReadOnlyList<DoctorTile> _tiles = [];

    private string? _open;

    private bool _problemsOnly;

    private bool _detailsOpen;

    private int _columns = 2;

    public DoctorView()
    {
        InitializeComponent();

        Loaded += (_, _) => Run();
    }

    internal static string KindOf(DoctorLevel level) => level switch
    {
        DoctorLevel.Bad => "Danger",
        DoctorLevel.Warn => "Warn",
        _ => "Accent",
    };

    private void OnRun(object sender, RoutedEventArgs e) => Run();

    /// <remarks>
    /// Проверки — в фоне. Замер 28.09: все вместе 0,4 с, и прежде раздел
    /// всё это время стоял пустым, а окно не отзывалось (владелец: «диагностика
    /// долго является пустой»). Теперь раздел открывается сразу с многоточием
    /// в сводке, а плитки приходят следом.
    /// </remarks>
    private async void Run()
    {
        RunButton.IsEnabled = false;
        TotalCount.Text = BadCount.Text = WarnCount.Text = "…";
        Status.Visibility = Visibility.Collapsed;

        try
        {
            var sections = await Task.Run(() => Doctor.Run(AppSettings.Load(AppSettings.DefaultPath)));

            Show(sections);

            Motion.Arrive(Components, dy: 0, ms: 200);
        }
        catch (Exception ex)
        {
            Status.Visibility = Visibility.Visible;
            Status.Text = "Диагностика сорвалась: " + ex.GetBaseException().Message;
        }
        finally
        {
            RunButton.IsEnabled = true;
        }
    }

    /// <summary>Раскладывает итог проверок: сводка, плитки, подробности.</summary>
    internal void Show(IReadOnlyList<DoctorSection> sections)
    {
        _tiles = sections
            .Select(s =>
            {
                var lines = s.Lines.Select(c => Line(c, s.Title)).ToList();
                var worst = lines.Count == 0 ? DoctorLevel.Ok : lines.Max(l => l.Level);

                return new DoctorTile(s.Title, GlyphOf(s.Title), worst, lines) { Shape = ShapeOf(s.Title) };
            })
            .ToList();

        var all = _tiles.SelectMany(t => t.Lines).ToList();
        int bad = all.Count(l => l.Level == DoctorLevel.Bad);
        int warn = all.Count(l => l.Level == DoctorLevel.Warn);

        TotalCount.Text = all.Count.ToString();
        BadCount.Text = bad.ToString();
        WarnCount.Text = warn.ToString();

        Badge(BadBadge, BadGlyph, bad > 0 ? "Danger" : null);
        Badge(WarnBadge, WarnGlyph, warn > 0 ? "Warn" : null);

        // Подсказка Verdict нужна только при красном: «чинить первым».
        Status.Visibility = bad > 0 ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = Doctor.Verdict(sections);

        AllFilter.Content = $"Все ({all.Count})";
        ProblemFilter.Content = $"Только замечания ({bad + warn})";
        DetailsCount.Text = $"{all.Count} проверок · поломок {bad} · оговорок {warn}";

        // Плитка, раскрытая до повтора, остаётся раскрытой, если раздел ещё есть.
        if (_open is not null && _tiles.All(t => t.Title != _open))
            _open = null;

        _problemsOnly = bad + warn > 0;
        _detailsOpen = bad + warn > 0;

        LayTiles();
        FillDetails();
    }

    private void Badge(Border badge, TextBlock glyph, string? kind)
    {
        if (kind is null)
        {
            badge.SetResourceReference(Border.BorderBrushProperty, "Border");
            glyph.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        }
        else
        {
            badge.SetResourceReference(Border.BorderBrushProperty, kind);
            glyph.SetResourceReference(TextBlock.ForegroundProperty, kind);
        }
    }

    private static DoctorLine Line(DoctorCheck check, string section) => new(
        check.Text,
        KindOf(check.Level),
        check.Level,
        section,
        check.ActionLabel,
        check.Open is { } console ? () => OpenConsole(console) : null);

    /// <summary>Знак раздела — по его имени из <see cref="Doctor.Run"/>.</summary>
    private static string GlyphOf(string title) => title switch
    {
        "Права" => "\uEA18",
        "Папка программы" => "\uE8B7",
        "Движки" => "\uE950",
        "Десинк" => "\uE9E9",
        "Правила" => "\uE816",
        "Конфиг туннеля" => "\uE774",
        "Другие VPN-клиенты" => "\uE8AB",
        "Имена и hosts" => "\uE8A5",
        "Браузеры и сертификаты" => "\uEB95",
        "Супервизор" => "\uE7E8",
        "Прокси Telegram" => string.Empty,
        _ => "\uE9D9",
    };

    /// <summary>Фигура вместо знака — у Telegram свой самолётик (владелец 22.10: «везде»).</summary>
    private static Geometry? ShapeOf(string title) =>
        title == "Прокси Telegram" ? (Geometry)Application.Current.FindResource("TelegramPlane") : null;

    private void OnPageSize(object sender, SizeChangedEventArgs e)
    {
        // Узкое окно (~1000 точек вместе с меню, страница ~680) — плитки
        // в одну колонку, цепочка в два ряда: в две колонки и в один ряд
        // подписи обрезались на втором слове (снимок 19.10).
        int columns = e.NewSize.Width < 760 ? 1 : 2;

        if (columns == _columns)
            return;

        _columns = columns;
        LayChain(narrow: columns == 1);
        LayTiles();
    }

    /// <summary>Цепочка ответа: в один ряд на широком окне, два по два — на узком.</summary>
    private void LayChain(bool narrow)
    {
        Place(LinkPath, narrow ? 1 : 0, narrow ? 0 : 4);
        Place(Arrow3, narrow ? 1 : 0, narrow ? 1 : 5);
        Place(LinkTarget, narrow ? 1 : 0, narrow ? 2 : 6);

        LinkPath.Margin = LinkTarget.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
        Arrow2.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;

        // Пустые колонки со звёздочкой забирали бы половину ширины.
        for (int c = 3; c < 7; c++)
        {
            Chain.ColumnDefinitions[c].Width = narrow
                ? new GridLength(0)
                : c % 2 == 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }

        static void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
        }
    }

    /// <summary>
    /// Плитки рядами, раскрытая — под своим рядом на всю ширину.
    /// </summary>
    /// <remarks>
    /// Рядами из сетки, а не одной UniformGrid: раскрытой плитке нужна
    /// вся ширина сразу под ней, а не в конце списка из пяти рядов.
    /// </remarks>
    private void LayTiles()
    {
        Components.Children.Clear();

        var template = (DataTemplate)FindResource("TileTemplate");

        for (int i = 0; i < _tiles.Count; i += _columns)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            DoctorTile? opened = null;

            for (int c = 0; c < _columns; c++)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition());

                if (i + c >= _tiles.Count)
                    continue;

                var tile = _tiles[i + c] with { Open = _tiles[i + c].Title == _open };

                if (tile.Open)
                    opened = tile;

                var cell = new ContentControl
                {
                    Content = tile,
                    ContentTemplate = template,
                    Focusable = false,
                    Margin = new Thickness(c == 0 ? 0 : 5, 0, c == _columns - 1 ? 0 : 5, 0),
                };

                Grid.SetColumn(cell, c);
                row.Children.Add(cell);
            }

            Components.Children.Add(row);

            if (opened is not null)
                Components.Children.Add(Opened(opened));
        }
    }

    private Border Opened(DoctorTile tile)
    {
        var lines = new ItemsControl
        {
            ItemsSource = tile.Lines,
            ItemTemplate = (DataTemplate)FindResource("LineTemplate"),
        };

        var card = new Card { Margin = new Thickness(0, 0, 0, 10), Child = lines, Padding = new Thickness(18, 14, 18, 4) };
        card.SetResourceReference(StyleProperty, "Card");

        Motion.Arrive(card, dy: -6, ms: 180);

        return card;
    }

    private void OnTile(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DoctorTile tile)
            return;

        _open = _open == tile.Title ? null : tile.Title;
        LayTiles();
    }

    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        _problemsOnly = false;
        _detailsOpen = true;
        FillDetails();
    }

    private void OnShowProblems(object sender, RoutedEventArgs e)
    {
        _problemsOnly = true;
        _detailsOpen = true;
        FillDetails();
    }

    private void OnToggleDetails(object sender, MouseButtonEventArgs e)
    {
        _detailsOpen = !_detailsOpen;
        FillDetails();
    }

    private void OnSearch(object sender, TextChangedEventArgs e) => FillDetails();

    private void FillDetails()
    {
        DetailsBody.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        DetailsTools.Visibility = DetailsBody.Visibility;
        // Своим поворотом: у стиля стрелки он общий и заморожен.
        DetailsChevron.RenderTransform = new RotateTransform(Chevrons.Angle(_detailsOpen));

        AllFilter.SetResourceReference(StyleProperty, _problemsOnly ? "Ghost" : "Primary");
        ProblemFilter.SetResourceReference(StyleProperty, _problemsOnly ? "Primary" : "Ghost");

        var query = Search.Text.Trim();

        // Поломки первыми: красное чинить первым, остальное часто следствие.
        var lines = _tiles
            .SelectMany(t => t.Lines)
            .Where(l => !_problemsOnly || l.Level != DoctorLevel.Ok)
            .Where(l => query.Length == 0
                || l.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || l.Section.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(l => l.Level)
            .Select(l => l with { ShowSection = true })
            .ToList();

        Details.ItemsSource = lines;
        DetailsEmpty.Visibility = lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLineAction(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DoctorLine { Act: { } act })
            act();
    }

    /// <summary>
    /// Открывает окно сертификатов Windows.
    /// </summary>
    /// <remarks>
    /// Удаляет человек сам: хранилище доверия — системная настройка
    /// безопасности, и у удаления есть цена (сайты банков). Программа
    /// показывает, где лежит, и не решает за него.
    /// </remarks>
    private static void OpenConsole(string console)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(console) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Не открылось — строка проверки уже говорит, где искать вручную.
        }
    }

    private void OnHostKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Ask();
    }

    private void OnAsk(object sender, RoutedEventArgs e) => Ask();

    private void OnLog(object sender, RoutedEventArgs e) => LogWindow.Open();

    /// <summary>
    /// Собирает отчёт для разбора и показывает его в проводнике.
    /// </summary>
    /// <remarks>
    /// Сборка и вычистка — в библиотеке (<see cref="SupportReport"/>), ссылки
    /// подписок она находит сама. Окно только открывает папку с готовым файлом,
    /// чтобы его сразу можно было перетащить в чат.
    /// </remarks>
    private async void OnReport(object sender, RoutedEventArgs e)
    {
        ReportButton.IsEnabled = false;
        ReportValue.Visibility = Visibility.Visible;
        ReportValue.Text = "Собираю отчёт…";

        try
        {
            var result = await Task.Run(() => SupportReport.Create(MainWindow.Version(), machine: true));

            ReportValue.Text = $"Отчёт готов: {Path.GetFileName(result.Path)} — в папке reports. "
                + $"Внутри: {string.Join(", ", result.Files)}.";

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{result.Path}\"");
            }
            catch (Exception)
            {
                // Проводник не открылся — путь назван выше, файл на месте.
            }
        }
        catch (Exception ex)
        {
            ReportValue.Text = "Отчёт не собрался: " + ex.Message;
        }
        finally
        {
            ReportButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Прогоняет имя через настоящий движок правил.
    /// </summary>
    /// <remarks>
    /// Через движок, а не поиском правила по совпадению строк: иначе ответ
    /// разошёлся бы с действительным ровно в тех случаях, ради которых
    /// вопрос и задают, — при перекрытии правил.
    /// </remarks>
    private void Ask()
    {
        var host = Host.Text.Trim().Trim('/').ToLowerInvariant();

        if (host.Contains("://"))
            host = host.Split("://")[1];

        host = host.Split('/')[0].TrimStart('*', '.');

        var exe = Exe.Text.Trim();
        var addressText = Address.Text.Trim();

        if (host.Length == 0 && exe.Length == 0 && addressText.Length == 0)
        {
            Refuse("Нечего спрашивать", "Задайте хотя бы одно: имя, программу или адрес.");
            return;
        }

        // Имя проверяется на точку только когда оно и есть вопрос: с одним
        // лишь процессом или адресом пустое имя — законный случай.
        if (host.Length > 0 && !host.Contains('.'))
        {
            Refuse("Это не похоже на имя сайта", "Нужно что-то вроде web.whatsapp.com.");
            return;
        }

        IPAddress? address = null;

        if (addressText.Length > 0 && !IPAddress.TryParse(addressText, out address))
        {
            Refuse("Это не похоже на адрес", $"«{addressText}» не разбирается как IP-адрес.");
            return;
        }

        ushort port = 443;

        if (Port.Text.Trim().Length > 0 && !ushort.TryParse(Port.Text.Trim(), out port))
        {
            Refuse("Это не похоже на порт", $"«{Port.Text.Trim()}» не разбирается как номер порта.");
            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            var engine = RuleSetLoader.LoadFor(settings);

            RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);

            var connection = new ConnectionEvent
            {
                Timestamp = DateTimeOffset.Now,
                Protocol = ProtocolKind.Tcp,

                // Выдуманный адрес не подставляется: если про него не спросили,
                // его нет. IPAddress.None — это 255.255.255.255, и он однажды
                // уже попал в список российских подсетей, отчего все сервисы
                // показывались идущими напрямую.
                RemoteAddress = address,
                RemotePort = port,
                ExecutablePath = exe.Length > 0 ? exe : null,
                Hostname = host.Length > 0 ? host : null,
                Direction = ConnectionDirection.Outbound,
            };

            var decision = engine.Evaluate(connection);

            // Десинк — цветом оговорки, а не поломки: в макете он был красным,
            // и рядом с «Поломок 1» читался как беда.
            var (mode, glyph, kind) = decision.Mode switch
            {
                RoutingMode.Direct => ("напрямую", "\uE72A", "Muted"),
                RoutingMode.Desync => ("через десинк", "\uE9E9", "Warn"),
                _ => ("через VPN", "\uE774", "Accent"),
            };

            var rule = decision.Rule;

            var source = rule?.Source switch
            {
                null => "ни одно не подошло",
                RuleSource.User => "ваше правило",
                RuleSource.Setting => "«Прятать VPN»",
                RuleSource.Fallback => "российские зоны",
                _ => "из поставки",
            };

            var target = host.Length > 0 ? $"{host}:{port}" : address is not null ? $"{address}:{port}" : $"порт {port}";

            ShowChain(
                new RouteLink("\uE71D", "Программа", exe.Length > 0 ? exe : "не задано", null, "Muted", Empty: exe.Length == 0),
                new RouteLink("\uE8FD", "Правило", source,
                    rule is null ? "по умолчанию" : $"{rule.Match.ToString().ToLowerInvariant()} «{rule.Value}»", "Muted"),
                new RouteLink(glyph, "Путь", mode, null, kind),
                new RouteLink("\uE909", "Назначение", target,
                    host.Length > 0 && address is not null ? address.ToString() : null, "Muted"));

            AnswerWhy.Text = rule is not null
                ? $"Сработало правило: {rule.Match.ToString().ToLowerInvariant()} «{rule.Value}»"
                  + (decision.Reason is null ? "." : $" — {decision.Reason}.")
                  + rule.Source switch
                  {
                      RuleSource.User => " Это ваше правило.",
                      RuleSource.Setting => " Это выключатель «Прятать VPN от российских приложений» "
                          + "в «Настройках туннеля».",
                      RuleSource.Fallback => " Это правило «российские зоны напрямую»: оно решает, "
                          + "только если ни одно другое правило не подошло.",
                      _ => " Это правило из поставки.",
                  }
                : $"Ни одно правило не совпало, применён режим по умолчанию — {mode}."
                  + (decision.HadUnevaluableDomainRules
                      ? " Часть доменных правил проверить было нечем: имя не задано."
                      : string.Empty);
        }
        catch (Exception ex)
        {
            Refuse("Правила не читаются", ex.GetBaseException().Message, "Danger");
        }
    }

    private void ShowChain(RouteLink program, RouteLink rule, RouteLink path, RouteLink target)
    {
        AnswerCard.Visibility = Visibility.Visible;
        Chain.Visibility = Visibility.Visible;
        AnswerMode.Visibility = Visibility.Collapsed;

        LinkProgram.Content = program;
        LinkRule.Content = rule;
        LinkPath.Content = path;
        LinkTarget.Content = target;
    }

    /// <summary>Вопрос не разобран: вместо цепочки — что не так.</summary>
    private void Refuse(string title, string why, string colorKey = "Warn")
    {
        AnswerCard.Visibility = Visibility.Visible;
        Chain.Visibility = Visibility.Collapsed;
        AnswerMode.Visibility = Visibility.Visible;
        AnswerMode.Text = title;
        AnswerMode.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        AnswerWhy.Text = why;
    }
}
