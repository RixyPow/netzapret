using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Одно замеченное соединение и решение по нему.</summary>
public sealed record WatchRow(
    string Time,
    string Mode,
    Brush Colour,
    string Process,
    string Endpoint,
    string Rule);

/// <summary>
/// Показывает соединения по мере появления и правило, применённое к каждому.
/// </summary>
/// <remarks>
/// <para>
/// Отвечает на вопрос, на который не отвечает ничто другое: «почему это пошло
/// не туда». Маршруты показывают, как правила <i>записаны</i>, проверка
/// блокировок — что закрыто снаружи, а здесь видно, какое правило досталось
/// настоящему соединению настоящей программы. Расхождение между первым
/// и третьим и есть та ошибка, которую иначе ищут наугад.
/// </para>
/// <para>
/// Не перенаправляет ничего и ни на что не влияет: события только читаются.
/// Выключать движки ради него не нужно, и на их работу он не действует.
/// </para>
/// <para>
/// Источник — события ядра (ETW), сеанс — <see cref="ConnectionWatch"/>, общий
/// с <c>nz watch</c>; каждое соединение пишется в <c>runtime\watch.log</c>,
/// и журнал со сводкой по программам едет в отчёт. WFP источником не взят:
/// на этой системе он не отдаёт событий о <i>разрешённых</i> соединениях,
/// то есть показывал бы пустую таблицу.
/// </para>
/// </remarks>
public partial class WatchView : UserControl
{
    /// <summary>
    /// Сколько строк держать.
    /// </summary>
    /// <remarks>
    /// Ограничение обязательно, а не на всякий случай: браузер с десятком
    /// вкладок даёт сотни соединений в минуту, и список без предела съел бы
    /// память за полчаса наблюдения.
    /// </remarks>
    private const int Limit = 400;

    /// <summary>
    /// Как часто переносить накопленное в таблицу.
    /// </summary>
    /// <remarks>
    /// Пачками, а не по событию. События приходят очередями по десятку
    /// за миг, и вставка каждого поодиночке заставляет WPF пересчитывать
    /// разметку столько же раз — окно начинает заикаться ровно тогда, когда
    /// на него смотрят.
    /// </remarks>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Сколько строк помнить для отбора по программе.
    /// </summary>
    /// <remarks>
    /// Больше, чем показывается: браузер вытесняет из четырёхсот последних
    /// строк всё остальное за минуту, и отбор по редкой программе иначе
    /// находил бы пустоту. Рисуются всё равно не больше <see cref="Limit"/> —
    /// список не виртуализирован, и тысячи строк окно не потянет.
    /// </remarks>
    private const int StoreLimit = 5000;

    /// <summary>Надпись кнопки без отбора.</summary>
    private const string AllProcesses = "Все программы";

    /// <summary>Сколько замеченных программ показывать в меню.</summary>
    private const int MenuPrograms = 15;

    private readonly ObservableCollection<WatchRow> _rows = [];
    private readonly List<WatchRow> _store = [];
    private readonly Dictionary<string, int> _perProcess = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WatchRow> _pending = [];

    /// <summary>Какую программу показывать; <c>null</c> — все.</summary>
    private string? _process;
    private readonly DispatcherTimer _flush = new() { Interval = FlushInterval };

    private CancellationTokenSource? _work;
    private ConnectionWatch? _watch;
    private Dictionary<RoutingMode, Brush> _brushes = [];

    /// <summary>Показывать только то, что уходит в туннель или под десинк.</summary>
    private bool _interestingOnly;

    /// <summary>Счёт сеанса на миг «Очистить»: таблица считает с нуля, журнал — нет.</summary>
    private long _totalBase;
    private long _matchedBase;
    private long _hidden;

    public WatchView()
    {
        InitializeComponent();

        Rows.ItemsSource = _rows;
        _flush.Tick += (_, _) => Flush();

        Status.Text = "Наблюдение выключено. Оно ничего не меняет — только читает события ядра.";

        // Сессия ETW принадлежит системе, а не окну: не остановленная, она
        // переживёт уход с вкладки и останется висеть до перезагрузки.
        Unloaded += (_, _) => Stop();
    }

    private void OnPower(object sender, RoutedEventArgs e)
    {
        if (_watch is not null)
        {
            Stop();
            return;
        }

        try
        {
            // Тот же сеанс, что у nz watch (ConnectionWatch): те же правила,
            // что у сборки конфига, и тот же журнал runtime\watch.log.
            _watch = ConnectionWatch.Start(AppSettings.Load(AppSettings.DefaultPath), ConnectionWatch.DefaultJournal);
            _totalBase = 0;
            _matchedBase = 0;

            // Кисти — здесь, в потоке окна: строки собираются в фоне.
            _brushes = new Dictionary<RoutingMode, Brush>
            {
                [RoutingMode.Proxy] = (Brush)FindResource("Accent"),
                [RoutingMode.Desync] = (Brush)FindResource("Warn"),
                [RoutingMode.Direct] = (Brush)FindResource("Muted"),
            };

            _work = new CancellationTokenSource();
            _ = ReadAsync(_watch, _work.Token);

            _flush.Start();

            PowerButton.Content = "Остановить";
            Status.Text = $"Смотрю. Правил: {_watch.RuleCount}, режим «{_watch.Mode}». Журнал — {ConnectionWatch.DefaultJournal}.";
        }
        catch (Exception ex)
        {
            Stop();

            Status.Text = "Не удалось начать: " + ex.GetBaseException().Message
                + ". Сессия ETW требует прав администратора — окно их запрашивает при запуске.";
        }
    }

    /// <summary>
    /// Читает соединения и складывает готовые строки в очередь.
    /// </summary>
    /// <remarks>
    /// В фоне, а не при показе: решение правил разворачивает списки доменов,
    /// и в потоке разметки это подвешивало бы окно на каждой строке.
    /// В журнал соединение уходит всякое — отбор здесь касается только показа.
    /// </remarks>
    private async Task ReadAsync(ConnectionWatch watch, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var entry in watch.ReadAsync(cancellationToken))
            {
                if (_interestingOnly && entry.Mode == RoutingMode.Direct)
                {
                    Interlocked.Increment(ref _hidden);
                    continue;
                }

                var row = new WatchRow(
                    entry.Time.ToLocalTime().ToString("HH:mm:ss.fff"),
                    entry.ModeWord,
                    _brushes[entry.Mode],
                    entry.Process,
                    entry.Endpoint,
                    entry.RuleShown);

                lock (_pending)
                    _pending.Add(row);
            }
        }
        catch (OperationCanceledException)
        {
            // Обычная остановка.
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                Stop();
                Status.Text = "Наблюдение прервалось: " + ex.GetBaseException().Message;
            });
        }
    }

    /// <summary>Переносит накопленное в таблицу одной пачкой.</summary>
    private void Flush()
    {
        List<WatchRow> batch;

        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                ShowStats();
                return;
            }

            batch = [.. _pending];
            _pending.Clear();
        }

        // Новое сверху: живой список смотрят ради последнего события,
        // а не ради первого, и прокручивать за ним вниз пришлось бы вручную.
        foreach (var row in batch)
        {
            _store.Insert(0, row);

            _perProcess[row.Process] = _perProcess.GetValueOrDefault(row.Process) + 1;

            if (Shows(row))
                _rows.Insert(0, row);
        }

        if (_store.Count > StoreLimit)
            _store.RemoveRange(StoreLimit, _store.Count - StoreLimit);

        while (_rows.Count > Limit)
            _rows.RemoveAt(_rows.Count - 1);

        ShowStats();
    }

    private bool Shows(WatchRow row) =>
        _process is null || string.Equals(row.Process, _process, StringComparison.OrdinalIgnoreCase);

    /// <summary>Показать одну программу или все — заново из запомненного.</summary>
    /// <remarks>
    /// Сравнение по имени файла без регистра: ETW даёт путь, раздел
    /// показывает имя в нижнем регистре (<c>ExecutableName</c>), а окно
    /// выбора и файл — как записано на диске.
    /// </remarks>
    private void ShowProcess(string? name)
    {
        _process = name;
        ProcessButton.Content = name ?? AllProcesses;
        if (name is null)
            ProcessButton.ClearValue(ForegroundProperty);
        else
            ProcessButton.Foreground = (Brush)FindResource("Accent");

        _rows.Clear();

        foreach (var row in _store.Where(Shows).Take(Limit))
            _rows.Add(row);

        ShowStats();
    }

    /// <summary>
    /// Меню выбора программы — как «Добавить программу» в «Маршрутах».
    /// </summary>
    /// <remarks>
    /// Не только из замеченных (владелец 10.10: «почему ты выбираешь только
    /// из существующих»): программу выбирают, чтобы увидеть, куда она пойдёт,
    /// часто до того, как её запустили. Отбор по ней ждёт её первых соединений.
    /// Замеченные — ниже, по числу соединений: это те, что шумят сейчас.
    /// </remarks>
    private void OnProcessMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ProcessButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        if (_process is not null)
            menu.Items.Add(MenuEntry("", AllProcesses, () => ShowProcess(null)));

        menu.Items.Add(MenuEntry("", "Из запущенных…", PickRunning));
        menu.Items.Add(MenuEntry("", "Выбрать файл…", PickFile));

        var seen = _perProcess
            .Where(p => p.Value > 0)
            .OrderByDescending(p => p.Value)
            .Take(MenuPrograms)
            .ToList();

        if (seen.Count > 0)
        {
            menu.Items.Add(new Separator());

            foreach (var (name, count) in seen)
                menu.Items.Add(MenuEntry(string.Empty, $"{name} — {count}", () => ShowProcess(name)));
        }

        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string glyph, string text, Action act)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

        var item = new MenuItem { Header = text, Icon = icon };
        item.Click += (_, _) => act();

        return item;
    }

    private void PickRunning()
    {
        var window = new ProgramPickerWindow { Owner = Window.GetWindow(this) };

        if (window.ShowDialog() == true && window.Chosen is { } chosen)
            ShowProcess(chosen.Name);
    }

    private void PickFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Программа",
            Filter = "Программы|*.exe",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            ShowProcess(System.IO.Path.GetFileName(dialog.FileName));
    }

    private void OnProcessClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WatchRow row })
            ShowProcess(row.Process);
    }

    private void ShowStats()
    {
        if (_watch is null)
            return;

        var parts = new List<string>
        {
            $"соединений: {_watch.Total - _totalBase}",
            $"под правило попало: {_watch.Matched - _matchedBase}",
            $"имён узнано: {_watch.NamesKnown}",
        };

        if (_watch.Dropped > 0)
            parts.Add($"потеряно при переполнении: {_watch.Dropped}");

        if (_interestingOnly && _hidden > 0)
            parts.Add($"скрыто прямых: {_hidden}");

        if (_process is not null)
            parts.Add($"{_process}: {_perProcess.GetValueOrDefault(_process)}");

        Status.Text = string.Join(" · ", parts);
    }

    private void Stop()
    {
        _flush.Stop();
        _work?.Cancel();
        _work = null;

        if (_watch is not null)
        {
            // Синхронно и до конца: сессия ETW переживает процесс, и брошенная
            // она останется в системе именем NetZapret до перезагрузки.
            _watch.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _watch = null;
        }

        lock (_pending)
            _pending.Clear();

        PowerButton.Content = "Начать";
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        _interestingOnly = !_interestingOnly;
        _hidden = 0;

        FilterButton.Content = _interestingOnly ? "Показывать всё" : "Только туннель и десинк";

        FilterButton.Foreground = (Brush)FindResource(_interestingOnly ? "Accent" : "Muted");
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        _rows.Clear();
        _store.Clear();
        // Выбранная программа остаётся в отборе: очистка — чтобы смотреть
        // её соединения с чистого листа, а не чтобы сбросить выбор.
        _perProcess.Clear();

        lock (_pending)
            _pending.Clear();

        _totalBase = _watch?.Total ?? 0;
        _matchedBase = _watch?.Matched ?? 0;
        _hidden = 0;
    }
}
