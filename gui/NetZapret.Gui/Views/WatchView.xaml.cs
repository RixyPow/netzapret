using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Одно замеченное соединение — строка таблицы.</summary>
public sealed record WatchRow(
    string Time,
    string Mode,
    Brush Colour,
    Brush Dot,
    string CertaintyHint,
    string Process,
    string Endpoint,
    string AddressLine,
    Visibility AddressShown,
    string RuleName,
    string Rule,
    WatchEntry Entry);

/// <summary>Строка легенды кольца.</summary>
public sealed record WatchLegendLine(WatchRoute Route, Brush Brush, string Word, int Count, string Share);

/// <summary>Строка «Шумят больше всех» или «Сайты».</summary>
public sealed record WatchTopLine(string Name, int Count, double Bar, double Track, string Hint, bool IsSite);

/// <summary>
/// Показывает соединения по мере появления и правило, применённое к каждому.
/// </summary>
/// <remarks>
/// <para>
/// Отвечает на вопрос, на который не отвечает ничто другое: «почему это пошло
/// не туда». Маршруты показывают, как правила <i>записаны</i>, проверка
/// блокировок — что закрыто снаружи, а здесь видно, какое правило досталось
/// настоящему соединению настоящей программы.
/// </para>
/// <para>
/// Не перенаправляет ничего и ни на что не влияет: события только читаются.
/// </para>
/// <para>
/// Источник — события ядра (ETW), сеанс — <see cref="ConnectionWatch"/>, общий
/// с <c>nz watch</c>; каждое соединение пишется в <c>runtime\watch.log</c>,
/// и журнал со сводкой по программам едет в отчёт.
/// </para>
/// <para>
/// Вид — по макету владельца 10.10: поиск, отборы «Программа», «Куда»,
/// «Правило», счётчики, кружок «точно / по правилам», меню у строки
/// («Проверить», «Почему так», «Добавить маршрут»), справа кольцо долей
/// и «кто шумит».
/// </para>
/// </remarks>
public partial class WatchView : UserControl
{
    /// <summary>
    /// Сколько строк рисовать.
    /// </summary>
    /// <remarks>
    /// Список не виртуализирован, и тысячи строк окно не потянет: браузер
    /// с десятком вкладок даёт сотни соединений в минуту.
    /// </remarks>
    private const int Limit = 400;

    /// <summary>
    /// Как часто переносить накопленное в таблицу.
    /// </summary>
    /// <remarks>
    /// Пачками, а не по событию: вставка каждого поодиночке заставляет WPF
    /// пересчитывать разметку столько же раз — окно начинает заикаться ровно
    /// тогда, когда на него смотрят.
    /// </remarks>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Правая колонка и кольцо — раз в столько перерисовок таблицы.</summary>
    private const int SideEvery = 4;

    /// <summary>
    /// Сколько строк помнить для отбора.
    /// </summary>
    /// <remarks>
    /// Больше, чем рисуется: браузер вытесняет из четырёхсот последних строк
    /// всё остальное за минуту, и отбор по редкой программе иначе находил
    /// бы пустоту.
    /// </remarks>
    private const int StoreLimit = 5000;

    /// <summary>Сколько строк в меню программ и правил, в «кто шумит».</summary>
    private const int MenuItems = 15;
    private const int TopItems = 5;

    /// <summary>
    /// «Сайтов» в правой колонке широкого окна — с запасом: рисуется столько,
    /// сколько влезло по высоте (FitStack), на высоком окне больше пяти.
    /// </summary>
    private const int SiteItemsWide = 15;

    /// <summary>Правая колонка уходит под таблицу, когда раздел уже этого.</summary>
    private const double NarrowBelow = 1100;

    private static readonly WatchRoute[] Routes =
        [WatchRoute.Proxy, WatchRoute.Desync, WatchRoute.Direct, WatchRoute.Local, WatchRoute.Engine];

    /// <summary>
    /// Цвет движка: своего в палитре нет, а серый уже у «напрямую»
    /// и «локально». Голубовато-серый, как на макете.
    /// </summary>
    private static readonly Brush EngineBrush = Frozen(Color.FromRgb(0x7D, 0x8F, 0xB0));

    // Сеанс и накопленное — статические, как у «Замера скорости»: раздел
    // создаётся заново при каждом заходе, и прежде уход с вкладки (Unloaded)
    // останавливал наблюдение. Владелец 10.10: «не останавливай наблюдение
    // при переключении вкладок». Останавливают кнопка и выход из программы.

    /// <summary>Замок над запомненным: его пишет фоновое чтение, читает раздел.</summary>
    private static readonly object Gate = new();

    /// <summary>Запомненные строки, старые первыми.</summary>
    private static readonly List<WatchRow> _store = [];

    /// <summary>Пришедшее с последней перерисовки — пока раздел открыт.</summary>
    private static readonly List<WatchRow> _pending = [];

    // Счёт с последнего «Очистить» — для кольца, счётчиков и «кто шумит».
    private static readonly Dictionary<string, int> _perProcess = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> _perSite = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> _perRule = new(StringComparer.Ordinal);
    private static readonly Dictionary<WatchRoute, int> _perRoute = [];
    private static readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);

    // Отбор показа; в журнал соединение уходит всякое.
    private static string? _process;
    private static WatchRoute? _route;

    /// <summary>
    /// Без отбора по виду — только «наружу»: туннель, десинк, напрямую.
    /// </summary>
    /// <remarks>
    /// По умолчанию: на снимках владельца 10.10 локальное и движок — 70 %
    /// строк (мультикаст, DNS к 172.19.0.2, sing-box к серверу), а разбирают
    /// почти всегда то, что уходит наружу. «Всё» — в меню «Куда».
    /// </remarks>
    private static bool _outsideOnly = true;
    private static string? _rule;
    private static string _search = string.Empty;

    private static CancellationTokenSource? _work;
    private static ConnectionWatch? _watch;
    private static Dictionary<WatchRoute, Brush> _brushes = [];
    private static DateTimeOffset _startedAt;

    /// <summary>Счёт сеанса на миг «Очистить»: таблица считает с нуля, журнал — нет.</summary>
    private static long _totalBase;
    private static long _matchedBase;

    /// <summary>Открытый раздел; <c>null</c> — наблюдение идёт без него.</summary>
    private static WatchView? _open;

    /// <summary>Последнее, что сказать без сеанса: почему не началось или прервалось.</summary>
    private static string? _note;

    private static bool _exitHooked;

    private readonly ObservableCollection<WatchRow> _rows = [];
    private readonly DispatcherTimer _flush = new() { Interval = FlushInterval };
    private int _ticks;

    /// <summary>Проверка имени из меню строки — одна за раз.</summary>
    private CancellationTokenSource? _probe;

    public WatchView()
    {
        InitializeComponent();

        Rows.ItemsSource = _rows;
        _flush.Tick += (_, _) => Flush();

        Loaded += (_, _) =>
        {
            lock (Gate)
                _open = this;

            if (_brushes.Count == 0)
                _brushes = Palette();

            Search.Text = _search;
            ShowFilters();
            Refill();
            ShowSide();
            ShowCounters();

            ShowPower(_watch is not null);
            Say(_watch is null ? _note : null);

            _flush.Start();
        };

        // Уход с вкладки сеанс не трогает: раздел только перестаёт рисовать.
        Unloaded += (_, _) =>
        {
            _flush.Stop();
            _probe?.Cancel();

            lock (Gate)
            {
                if (_open == this)
                    _open = null;

                _pending.Clear();
            }
        };
    }

    private Dictionary<WatchRoute, Brush> Palette() => new()
    {
        [WatchRoute.Proxy] = (Brush)FindResource("Accent"),
        [WatchRoute.Desync] = (Brush)FindResource("Warn"),
        [WatchRoute.Direct] = (Brush)FindResource("Muted"),
        [WatchRoute.Local] = Brush("Faint", Brushes.DimGray),
        [WatchRoute.Engine] = EngineBrush,
    };

    /// <summary>Кисть темы; у темы без неё — запасная, а не падение раздела.</summary>
    private Brush Brush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Say(string? text)
    {
        Status.Text = text ?? string.Empty;
        Status.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Подпись и значок кнопки по тому, идёт ли наблюдение.</summary>
    private void ShowPower(bool running)
    {
        PowerButton.Content = running ? "Остановить" : "Начать";
        ButtonGlyph.SetLead(PowerButton, running ? Glyph.Stop : Glyph.Play);
    }

    private void OnPower(object sender, RoutedEventArgs e)
    {
        if (_watch is not null)
        {
            StopSession();
            ShowPower(false);
            ShowCounters();
            return;
        }

        try
        {
            // Тот же сеанс, что у nz watch (ConnectionWatch): те же правила,
            // что у сборки конфига, и тот же журнал runtime\watch.log.
            _watch = ConnectionWatch.Start(AppSettings.Load(AppSettings.DefaultPath), ConnectionWatch.DefaultJournal);
            _startedAt = DateTimeOffset.Now;
            _totalBase = 0;
            _matchedBase = 0;
            _note = null;

            // Кисти — здесь, в потоке окна: строки собираются в фоне.
            _brushes = Palette();

            // Сессия ETW переживает процесс: выход из программы обязан её
            // остановить, раз раздел этого больше не делает.
            if (!_exitHooked && Application.Current is { } app)
            {
                app.Exit += (_, _) => StopSession();
                _exitHooked = true;
            }

            _work = new CancellationTokenSource();
            _ = ReadAsync(_watch, _work.Token);

            ShowPower(true);
            // Строкой под кнопками это отъедало у таблицы высоту — в подсказку.
            Say(null);
            ClockText.ToolTip = $"Правил {_watch.RuleCount}, режим «{_watch.Mode}». Журнал — {ConnectionWatch.DefaultJournal}.";
        }
        catch (Exception ex)
        {
            StopSession();

            _note = "Не удалось начать: " + ex.GetBaseException().Message
                + ". Сессия ETW требует прав администратора — окно их запрашивает при запуске.";
            Say(_note);
        }
    }

    /// <summary>
    /// Читает соединения и запоминает готовые строки — и без открытого раздела.
    /// </summary>
    /// <remarks>
    /// В фоне, а не при показе: решение правил разворачивает списки доменов,
    /// и в потоке разметки это подвешивало бы окно на каждой строке.
    /// </remarks>
    private static async Task ReadAsync(ConnectionWatch watch, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in watch.ReadAsync(cancellationToken))
            {
                var row = Row(entry);

                lock (Gate)
                {
                    _store.Add(row);
                    Count(entry);

                    // Срезается пачкой, а не по строке: сдвиг пятитысячного
                    // списка на каждое соединение — лишняя работа.
                    if (_store.Count > StoreLimit + StoreLimit / 10)
                        _store.RemoveRange(0, _store.Count - StoreLimit);

                    if (_open is not null)
                        _pending.Add(row);
                }
            }

            // Поток кончился без нашей остановки — сессию погасили снаружи:
            // имя сессии одно на машину, и nz watch --force её забирает.
            if (!cancellationToken.IsCancellationRequested)
                Interrupted("сессию забрал nz watch --force или остановила система");
        }
        catch (OperationCanceledException)
        {
            // Обычная остановка.
        }
        catch (Exception ex)
        {
            Interrupted(ex.GetBaseException().Message);
        }
    }

    private static WatchRow Row(WatchEntry entry)
    {
        var colour = _brushes.TryGetValue(entry.Mode, out var brush) ? brush : Brushes.Gray;
        bool named = entry.Host is not null;

        return new WatchRow(
            entry.Time.ToLocalTime().ToString("HH:mm:ss.fff"),
            entry.ModeWord,
            colour,
            entry.Certain ? colour : Brushes.Transparent,
            entry.Certain
                ? "Известно точно: подставной адрес движка, сам движок или домашняя сеть"
                : "Так сказали бы правила — движок не спрашивали",
            entry.Process,
            entry.Endpoint,
            named ? entry.Address : string.Empty,
            named ? Visibility.Visible : Visibility.Collapsed,
            entry.RuleName,
            entry.RuleShown,
            entry);
    }

    /// <summary>Учёт одной строки — под замком <see cref="Gate"/>.</summary>
    private static void Count(WatchEntry entry)
    {
        _perProcess[entry.Process] = _perProcess.GetValueOrDefault(entry.Process) + 1;
        _perRoute[entry.Mode] = _perRoute.GetValueOrDefault(entry.Mode) + 1;
        _perRule[entry.RuleName] = _perRule.GetValueOrDefault(entry.RuleName) + 1;

        if (entry.Mode is not (WatchRoute.Local or WatchRoute.Engine))
        {
            _addresses.Add(entry.Ip);

            if (entry.Host is { } host)
            {
                _names.Add(host);

                var site = WatchEntry.SiteOf(host);
                _perSite[site] = _perSite.GetValueOrDefault(site) + 1;
            }
        }
    }

    private static void Interrupted(string why) =>
        Application.Current?.Dispatcher.Invoke(() =>
        {
            StopSession();
            _note = "Наблюдение прервалось: " + why + ".";

            if (_open is { } view)
            {
                view.ShowPower(false);
                view.Say(_note);
                view.ShowCounters();
            }
        });

    /// <summary>Переносит накопленное в таблицу одной пачкой.</summary>
    private void Flush()
    {
        List<WatchRow> batch;

        lock (Gate)
        {
            batch = [.. _pending];
            _pending.Clear();
        }

        // Новое сверху: живой список смотрят ради последнего события.
        foreach (var row in batch)
        {
            if (Shows(row))
                _rows.Insert(0, row);
        }

        while (_rows.Count > Limit)
            _rows.RemoveAt(_rows.Count - 1);

        ShowCounters();

        if (++_ticks % SideEvery == 0)
            ShowSide();
    }

    /// <summary>Проходит ли строка нынешний отбор.</summary>
    private static bool Shows(WatchRow row)
    {
        var e = row.Entry;

        if (_process is not null && !string.Equals(e.Process, _process, StringComparison.OrdinalIgnoreCase))
            return false;

        if (_route is { } route ? e.Mode != route : _outsideOnly && e.Mode is (WatchRoute.Local or WatchRoute.Engine))
            return false;

        if (_rule is not null && e.RuleName != _rule)
            return false;

        return _search.Length == 0
            || e.Process.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || e.Endpoint.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || e.Address.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || e.RuleShown.Contains(_search, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Таблица заново из запомненного — после смены отбора.</summary>
    private void Refill()
    {
        List<WatchRow> shown;

        lock (Gate)
        {
            shown = [.. Enumerable.Range(0, _store.Count).Select(i => _store[_store.Count - 1 - i]).Where(Shows).Take(Limit)];
            _pending.Clear();
        }

        _rows.Clear();

        foreach (var row in shown)
            _rows.Add(row);
    }

    private void ShowFilters()
    {
        Mark(ProcessButton, _process is null ? "Программа: все" : $"Программа: {_process}", _process is not null);
        Mark(RouteButton, _route is { } r ? $"Куда: {WatchEntry.Word(r)}" : _outsideOnly ? "Куда: наружу" : "Куда: всё", _route is not null || !_outsideOnly);
        Mark(RuleButton, _rule is null ? "Правило: все" : $"Правило: {_rule}", _rule is not null);
    }

    private void Mark(Button button, string text, bool on)
    {
        button.Content = text;

        if (on)
            button.Foreground = (Brush)FindResource("Accent");
        else
            button.ClearValue(ForegroundProperty);
    }

    private void Filter(Action change)
    {
        change();
        ShowFilters();
        Refill();
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var text = Search.Text.Trim();

        if (text == _search)
            return;

        _search = text;
        Refill();
    }

    private void ShowCounters()
    {
        if (_watch is { } watch)
        {
            TotalText.Text = (watch.Total - _totalBase).ToString("N0");
            MatchedText.Text = (watch.Matched - _matchedBase).ToString("N0");

            var running = DateTimeOffset.Now - _startedAt;
            ClockText.Text = $"{(int)running.TotalHours:00}:{running.Minutes:00}:{running.Seconds:00}";
            ClockCaption.Text = watch.Dropped > 0 ? $"идёт · потеряно {watch.Dropped}" : "идёт";
        }
        else
        {
            ClockText.Text = "—";
            ClockCaption.Text = "выключено";
        }

        lock (Gate)
        {
            NamesText.Text = _names.Count.ToString("N0");
            AddressesText.Text = _addresses.Count.ToString("N0");
        }
    }

    /// <summary>Кольцо, легенда и «кто шумит».</summary>
    private void ShowSide()
    {
        // Размер приходит раньше Loaded, и кистей тогда ещё нет.
        if (_brushes.Count == 0)
            _brushes = Palette();

        Dictionary<WatchRoute, int> routes;
        List<KeyValuePair<string, int>> programs, sites;

        lock (Gate)
        {
            routes = new(_perRoute);
            programs = [.. _perProcess.OrderByDescending(p => p.Value).Take(TopItems)];
            sites = [.. _perSite.OrderByDescending(p => p.Value).Take(_narrow ? TopItems : SiteItemsWide)];
        }

        int total = routes.Values.Sum();

        Ring.Show([.. Routes.Select(r => ((double)routes.GetValueOrDefault(r), _brushes[r]))], Brush("Raised", Brushes.DimGray));
        RingTotal.Text = total.ToString("N0");

        RingLegend.ItemsSource = Routes
            .Select(r => new WatchLegendLine(
                r,
                _brushes[r],
                WatchEntry.Word(r),
                routes.GetValueOrDefault(r),
                total == 0 ? "—" : $"{100.0 * routes.GetValueOrDefault(r) / total:0}%"))
            .ToList();

        TopPrograms.ItemsSource = Top(programs, isSite: false);
        TopSites.ItemsSource = Top(sites, isSite: true);
    }

    /// <summary>Длина дорожки «кто шумит»: на узком окне вдвое короче — место имени.</summary>
    private double _track = 70;

    private List<WatchTopLine> Top(List<KeyValuePair<string, int>> items, bool isSite)
    {
        int max = items.Count == 0 ? 1 : Math.Max(1, items.Max(i => i.Value));

        return [.. items.Select(i => new WatchTopLine(
            i.Key,
            i.Value,
            _track * i.Value / max,
            _track,
            $"Показать только {i.Key}",
            isSite))];
    }

    private void OnTopClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WatchTopLine line })
            return;

        if (line.IsSite)
            Search.Text = line.Name;
        else
            Filter(() => _process = line.Name);
    }

    private void OnLegendClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WatchLegendLine line })
            Filter(() => _route = _route == line.Route ? null : line.Route);
    }

    /// <summary>На узком окне правая колонка встаёт строкой под таблицу.</summary>
    /// <remarks>
    /// У владельца ~830 точек содержимого: колонка в 300 оставила бы таблице
    /// пятьсот, и назначения обрезались бы на третьей букве.
    /// </remarks>
    private void OnBodySize(object sender, SizeChangedEventArgs e)
    {
        bool narrow = Body.ActualWidth < NarrowBelow;

        // Узкое окно — сводка под таблицей, но скрыта, пока не попросят
        // кнопкой «Сводка»: открытая отнимала у таблицы всё, кроме трёх
        // строк (сборка 9), а прокрутка страницы, которой это лечилось
        // в сборке 10, заставляла мотать везде (владелец 10.10).
        bool changed = _narrow != narrow;
        _narrow = narrow;
        SummaryButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        ShowSummary();

        SideColumn.Width = new GridLength(narrow ? 0 : 330);
        SideRow.Height = narrow ? GridLength.Auto : new GridLength(0);

        Grid.SetColumn(SideHost, narrow ? 0 : 1);
        Grid.SetRow(SideHost, narrow ? 1 : 0);
        SideHost.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);

        if (changed || _track != (narrow ? 36 : 70))
        {
            _track = narrow ? 36 : 70;
            ShowSide();
        }

        // Карточка кольца шире двух других: в ней кольцо и легенда рядом.
        SideC0.Width = new GridLength(narrow ? 1.6 : 1, GridUnitType.Star);
        SideC1.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SideC2.Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        // Кольцо под таблицей меньше: строка сводки должна быть низкой,
        // иначе таблице на окне владельца оставалось четыре строки.
        RingBox.Width = RingBox.Height = narrow ? 88 : 116;
        RingCaption.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;

        Place(RingCard, narrow ? 0 : 0, narrow ? 0 : 0, narrow ? new Thickness(0, 0, 8, 0) : new Thickness(0, 0, 0, 12));
        Place(ProgramsCard, narrow ? 0 : 1, narrow ? 1 : 0, narrow ? new Thickness(4, 0, 4, 0) : new Thickness(0, 0, 0, 12));
        Place(SitesCard, narrow ? 0 : 2, narrow ? 2 : 0, narrow ? new Thickness(8, 0, 0, 0) : new Thickness(0));
    }

    /// <summary>Открыта ли сводка на узком окне — помнится между заходами.</summary>
    private static bool _summaryOpen;

    private bool _narrow;

    private void ShowSummary()
    {
        SideHost.Visibility = !_narrow || _summaryOpen ? Visibility.Visible : Visibility.Collapsed;
        Mark(SummaryButton, _summaryOpen ? "Скрыть сводку" : "Сводка", _summaryOpen);
    }

    private void OnSummary(object sender, RoutedEventArgs e)
    {
        _summaryOpen = !_summaryOpen;
        ShowSummary();
    }

    private static void Place(FrameworkElement card, int row, int column, Thickness margin)
    {
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        card.Margin = margin;
    }

    /// <summary>
    /// Меню выбора программы — как «Добавить программу» в «Маршрутах».
    /// </summary>
    /// <remarks>
    /// Не только из замеченных (владелец 10.10: «почему ты выбираешь только
    /// из существующих»): программу выбирают, чтобы увидеть, куда она пойдёт,
    /// часто до того, как её запустили. Отбор по ней ждёт её первых соединений.
    /// </remarks>
    private void OnProcessMenu(object sender, RoutedEventArgs e)
    {
        var menu = Menu(ProcessButton);

        if (_process is not null)
            menu.Items.Add(MenuEntry("", "Все программы", () => Filter(() => _process = null)));

        menu.Items.Add(MenuEntry("", "Из запущенных…", PickRunning));
        menu.Items.Add(MenuEntry("", "Выбрать файл…", PickFile));

        List<KeyValuePair<string, int>> seen;

        lock (Gate)
            seen = [.. _perProcess.Where(p => p.Value > 0).OrderByDescending(p => p.Value).Take(MenuItems)];

        if (seen.Count > 0)
        {
            menu.Items.Add(new Separator());

            foreach (var (name, count) in seen)
                menu.Items.Add(MenuEntry(string.Empty, $"{name} — {count}", () => Filter(() => _process = name)));
        }

        menu.IsOpen = true;
    }

    private void OnRouteMenu(object sender, RoutedEventArgs e)
    {
        var menu = Menu(RouteButton);

        Dictionary<WatchRoute, int> counts;

        lock (Gate)
            counts = new(_perRoute);

        int outside = counts.GetValueOrDefault(WatchRoute.Proxy) + counts.GetValueOrDefault(WatchRoute.Desync)
            + counts.GetValueOrDefault(WatchRoute.Direct);

        menu.Items.Add(MenuEntry(string.Empty, $"Наружу: туннель, десинк, напрямую — {outside}",
            () => Filter(() => (_route, _outsideOnly) = (null, true))));
        menu.Items.Add(MenuEntry(string.Empty, $"Всё, и локальное с движком — {counts.Values.Sum()}",
            () => Filter(() => (_route, _outsideOnly) = (null, false))));
        menu.Items.Add(new Separator());

        foreach (var route in Routes)
            menu.Items.Add(MenuEntry(string.Empty, $"{WatchEntry.Word(route)} — {counts.GetValueOrDefault(route)}", () => Filter(() => _route = route)));

        menu.IsOpen = true;
    }

    private void OnRuleMenu(object sender, RoutedEventArgs e)
    {
        var menu = Menu(RuleButton);

        menu.Items.Add(MenuEntry(string.Empty, "Все", () => Filter(() => _rule = null)));

        List<KeyValuePair<string, int>> seen;

        lock (Gate)
            seen = [.. _perRule.OrderByDescending(p => p.Value).Take(MenuItems)];

        if (seen.Count > 0)
            menu.Items.Add(new Separator());

        foreach (var (name, count) in seen)
            menu.Items.Add(MenuEntry(string.Empty, $"{name} — {count}", () => Filter(() => _rule = name)));

        menu.IsOpen = true;
    }

    private static ContextMenu Menu(UIElement target) => new()
    {
        PlacementTarget = target,
        Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
    };

    private static MenuItem MenuEntry(string glyph, string text, Action act)
    {
        var item = new MenuItem { Header = text, Icon = MenuGlyph(glyph) };
        item.Click += (_, _) => act();

        return item;
    }

    private static TextBlock MenuGlyph(string glyph)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

        return icon;
    }

    /// <summary>
    /// Знаки IconFont раздела. Проверены снимком 10.10 (Segoe Fluent Icons);
    /// «Маршрут», «VPN» и «Десинк» — те же, что у пунктов бокового меню.
    /// </summary>
    private static class Glyph
    {
        public const string Play = "\uE768";
        public const string Stop = "\uE71A";
        public const string Search = "\uE721";
        public const string Info = "\uE946";
        public const string Route = "\uE816";
        public const string Vpn = "\uE774";
        public const string Desync = "\uE9E9";
        public const string Direct = "\uE72A";
        public const string Copy = "\uE8C8";
        public const string Filter = "\uE71C";
    }

    private void PickRunning()
    {
        var window = new ProgramPickerWindow { Owner = Window.GetWindow(this) };

        if (window.ShowDialog() == true && window.Chosen is { } chosen)
            Filter(() => _process = chosen.Name);
    }

    private void PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Программа",
            Filter = "Программы|*.exe",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            Filter(() => _process = System.IO.Path.GetFileName(dialog.FileName));
    }

    private void OnProcessClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WatchRow row })
            Filter(() => _process = row.Process);
    }

    /// <summary>
    /// Меню строки: от соединения к разбору в один щелчок (макет владельца 10.10).
    /// </summary>
    private void OnRowMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: WatchRow row } button)
            return;

        var entry = row.Entry;
        var target = entry.Host ?? entry.Ip;
        bool outside = entry.Mode is not (WatchRoute.Local or WatchRoute.Engine);
        var menu = Menu(button);

        if (outside)
        {
            menu.Items.Add(MenuEntry(Glyph.Search, entry.Host is null ? "Проверить этот адрес" : "Проверить это имя",
                () => _ = CheckAsync(entry)));
        }

        menu.Items.Add(MenuEntry(Glyph.Info, "Почему так", () => _ = WhyAsync(target)));

        if (outside && entry.Rule != WatchEntry.FakeRule)
        {
            var (kind, value, shown) = entry.Host is { } host
                ? (MatchKind.Domain, "*." + WatchEntry.SiteOf(host), WatchEntry.SiteOf(host))
                : (MatchKind.Ip, entry.Ip, entry.Ip);

            var add = new MenuItem { Header = $"Добавить маршрут для {shown}", Icon = MenuGlyph(Glyph.Route) };
            add.Items.Add(MenuEntry(Glyph.Vpn, "через VPN", () => AddRoute(kind, value, shown, RoutingMode.Proxy)));
            add.Items.Add(MenuEntry(Glyph.Desync, "десинк", () => AddRoute(kind, value, shown, RoutingMode.Desync)));
            add.Items.Add(MenuEntry(Glyph.Direct, "напрямую", () => AddRoute(kind, value, shown, RoutingMode.Direct)));
            menu.Items.Add(add);
        }

        menu.Items.Add(new Separator());

        if (entry.Host is { } name)
            menu.Items.Add(MenuEntry(Glyph.Copy, "Скопировать имя", () => CopyText(name)));

        menu.Items.Add(MenuEntry(Glyph.Copy, "Скопировать адрес", () => CopyText(entry.Ip)));
        menu.Items.Add(MenuEntry(Glyph.Filter, $"Показать только {entry.Process}", () => Filter(() => _process = entry.Process)));

        menu.IsOpen = true;
    }

    private static void CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // Буфер обмена держит другая программа — бывает, и не наша беда.
        }
    }

    private void ShowDetails(string title, string text)
    {
        DetailsTitle.Text = title;
        DetailsText.Text = text;
        Details.Visibility = Visibility.Visible;
    }

    private void OnDetailsClose(object sender, RoutedEventArgs e)
    {
        _probe?.Cancel();
        Details.Visibility = Visibility.Collapsed;
    }

    /// <summary>«Почему так» — то же объяснение, что nz where (RouteWhy).</summary>
    private async Task WhyAsync(string target)
    {
        ShowDetails($"Почему так: {target}", "Смотрю правила, hosts и DNS…");

        try
        {
            var lines = await Task.Run(() => RouteWhy.Explain(target));
            ShowDetails($"Почему так: {target}", string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            ShowDetails($"Почему так: {target}", "Не вышло: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// «Проверить это имя» — тот же разбор, что строка проверки блокировок.
    /// </summary>
    /// <remarks>
    /// Через туннель — если соединение и ушло в туннель: вердикты про DPI
    /// к такому имени неприменимы (<c>throughTunnel</c>).
    /// </remarks>
    private async Task CheckAsync(WatchEntry entry)
    {
        var target = entry.Host ?? entry.Ip;
        var title = $"Проверка: {target}";

        _probe?.Cancel();
        _probe = new CancellationTokenSource();
        var token = _probe.Token;

        ShowDetails(title, "Проверяю — до полуминуты, если имя молчит…");

        try
        {
            var report = await BlockCheck.CheckAsync(target, null, token, throughTunnel: entry.Mode == WatchRoute.Proxy);

            var text = report.Describe()
                + (report.Why is { } why ? Environment.NewLine + why : string.Empty)
                + Environment.NewLine
                + $"TCP: {report.Tcp.Describe()} · TLS: {report.Tls.Describe()} · данные: {report.DescribeData()}";

            if (!token.IsCancellationRequested)
                ShowDetails(title, text);
        }
        catch (OperationCanceledException)
        {
            // Закрыли карточку или ушли с вкладки.
        }
        catch (Exception ex)
        {
            ShowDetails(title, "Не вышло: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// «Добавить маршрут» — своё правило, как «Свой сайт» в «Маршрутах».
    /// </summary>
    /// <remarks>
    /// Для имени — весь сайт (<c>*.сайт</c>): соединение одного поддомена
    /// почти никогда не всё, что сайту нужно. Просьба Евгения в Telegram 10.10:
    /// «добавить маршрут прямо по наблюдаемым назначениям». Применится
    /// при следующем запуске движков — окно предложит перезапуск.
    /// </remarks>
    private void AddRoute(MatchKind kind, string value, string shown, RoutingMode mode)
    {
        try
        {
            var file = UserRulesFile.Load();
            file.Set(kind, value, mode, recipe: null);
            file.Save();

            var word = mode switch
            {
                RoutingMode.Proxy => "через VPN",
                RoutingMode.Desync => "десинк",
                _ => "напрямую",
            };

            Say($"Записано: {shown} → {word}. Применится при следующем запуске движков; править — в «Маршрутах».");
            this.Offer($"Добавлен маршрут: {shown}");
        }
        catch (Exception ex)
        {
            Say("Не удалось записать: " + ex.GetBaseException().Message);
        }
    }

    private void OnJournal(object sender, RoutedEventArgs e)
    {
        var path = System.IO.Path.GetFullPath(ConnectionWatch.DefaultJournal);

        if (!System.IO.File.Exists(path))
        {
            Answer(JournalButton, "Открыть журнал", "Журнала ещё нет");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception)
        {
            Answer(JournalButton, "Открыть журнал", "Не открылся");
        }
    }

    private void OnCopyHosts(object sender, RoutedEventArgs e) =>
        Copy(CopyHostsButton, "Скопировать домены", WatchEntry.HostsOf);

    private void OnCopyAddresses(object sender, RoutedEventArgs e) =>
        Copy(CopyAddressesButton, "Скопировать IP", WatchEntry.AddressesOf);

    /// <summary>
    /// Кладёт в буфер всё запомненное по нынешнему отбору, а не только
    /// видимые четыреста строк, и отвечает на самой кнопке.
    /// </summary>
    private static void Copy(Button button, string caption, Func<IEnumerable<WatchEntry>, IReadOnlyList<string>> pick)
    {
        IReadOnlyList<string> values;

        lock (Gate)
            values = pick(_store.Where(Shows).Select(r => r.Entry).ToList());

        if (values.Count == 0)
        {
            Answer(button, caption, "Нечего копировать");
            return;
        }

        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, values));
            Answer(button, caption, $"Скопировано: {values.Count}");
        }
        catch (Exception)
        {
            Answer(button, caption, "Буфер занят — ещё раз");
        }
    }

    /// <summary>Ответ на кнопке на две секунды, потом прежняя надпись.</summary>
    private static void Answer(Button button, string caption, string text)
    {
        button.Content = text;

        var back = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        back.Tick += (_, _) =>
        {
            back.Stop();
            button.Content = caption;
        };
        back.Start();
    }

    /// <summary>Останавливает сеанс — кнопкой или с выходом из программы.</summary>
    private static void StopSession()
    {
        _work?.Cancel();
        _work = null;

        if (_watch is not null)
        {
            // Синхронно и до конца: сессия ETW переживает процесс, и брошенная
            // она останется в системе именем NetZapret до перезагрузки.
            _watch.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _watch = null;
        }

        lock (Gate)
            _pending.Clear();
    }

    /// <summary>
    /// Очистить таблицу и счёт. Отбор остаётся: очистка — чтобы смотреть
    /// выбранное с чистого листа, а не чтобы сбросить выбор.
    /// </summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        _rows.Clear();

        lock (Gate)
        {
            _store.Clear();
            _pending.Clear();
            _perProcess.Clear();
            _perSite.Clear();
            _perRule.Clear();
            _perRoute.Clear();
            _names.Clear();
            _addresses.Clear();
        }

        _totalBase = _watch?.Total ?? 0;
        _matchedBase = _watch?.Matched ?? 0;
        _startedAt = DateTimeOffset.Now;

        ShowCounters();
        ShowSide();
    }
}
