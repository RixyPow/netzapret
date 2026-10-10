using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Etw;
using NetZapret.Zapret;

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
/// Источник один — ETW, в отличие от консоли, где есть ещё <c>--source wfp</c>.
/// Тот оставлен там не для пользы, а потому что интероп проверен и пригодится
/// под фильтры защиты от утечек: на этой системе WFP не отдаёт событий
/// о <i>разрешённых</i> соединениях, то есть показывает пустую таблицу.
/// Выпускать такой выбор в окно — значит предложить человеку способ
/// не увидеть ничего и решить, что соединений нет.
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

    /// <summary>Пункт списка программ «без отбора».</summary>
    private const string AllProcesses = "Все программы";

    private readonly ObservableCollection<WatchRow> _rows = [];
    private readonly List<WatchRow> _store = [];
    private readonly ObservableCollection<string> _processes = [AllProcesses];
    private readonly Dictionary<string, int> _perProcess = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<WatchRow> _pending = [];

    /// <summary>Какую программу показывать; <c>null</c> — все.</summary>
    private string? _process;
    private readonly DispatcherTimer _flush = new() { Interval = FlushInterval };

    private CancellationTokenSource? _work;
    private EtwConnectionSource? _source;

    /// <summary>Показывать только то, что уходит в туннель или под десинк.</summary>
    private bool _interestingOnly;

    private long _total;
    private long _matched;
    private long _hidden;

    public WatchView()
    {
        InitializeComponent();

        Rows.ItemsSource = _rows;
        ProcessBox.ItemsSource = _processes;
        ProcessBox.SelectedIndex = 0;
        _flush.Tick += (_, _) => Flush();

        Status.Text = "Наблюдение выключено. Оно ничего не меняет — только читает события ядра.";

        // Сессия ETW принадлежит системе, а не окну: не остановленная, она
        // переживёт уход с вкладки и останется висеть до перезагрузки.
        Unloaded += (_, _) => Stop();
    }

    private void OnPower(object sender, RoutedEventArgs e)
    {
        if (_source is not null)
        {
            Stop();
            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Оба слоя правил, как в консоли: наблюдатель показывает, какое
            // правило применилось бы, и читать он обязан ровно то же, что
            // читает сборка конфига. Иначе он не диагностика, а источник
            // ложных выводов.
            var engine = RuleSetLoader.LoadFor(settings);

            RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);

            _source = new EtwConnectionSource(new EtwConnectionSourceOptions
            {
                SkipLoopback = true,
                ObserveDns = true,
            });

            _source.Start();

            _work = new CancellationTokenSource();
            _ = ReadAsync(engine, _source, _work.Token);

            _flush.Start();

            PowerButton.Content = "Остановить";
            Status.Text = $"Смотрю. Правил: {engine.RuleSet.Rules.Count}, "
                + $"режим «{settings.DescribeMode()}».";
        }
        catch (Exception ex)
        {
            Stop();

            Status.Text = "Не удалось начать: " + ex.GetBaseException().Message
                + ". Сессия ETW требует прав администратора — окно их запрашивает при запуске.";
        }
    }

    /// <summary>
    /// Читает события и складывает готовые строки в очередь.
    /// </summary>
    /// <remarks>
    /// Решение считается здесь, в фоне, а не при показе: <see cref="RuleEngine"/>
    /// разворачивает списки доменов, и делать это в потоке разметки означало бы
    /// подвешивать окно на каждой строке.
    /// </remarks>
    private async Task ReadAsync(RuleEngine engine, EtwConnectionSource source, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var connection in source.ReadEventsAsync(cancellationToken))
            {
                var decision = engine.Evaluate(connection);

                Interlocked.Increment(ref _total);

                if (decision.Rule is not null)
                    Interlocked.Increment(ref _matched);

                if (_interestingOnly && decision.Mode == RoutingMode.Direct)
                {
                    Interlocked.Increment(ref _hidden);
                    continue;
                }

                var row = Row(connection, decision);

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

    private WatchRow Row(ConnectionEvent connection, RuleDecision decision)
    {
        // Имя информативнее адреса, поэтому показываем его, когда оно известно.
        // Известно оно далеко не всегда: браузеры ходят мимо службы DNS-клиента
        // со своим резолвером, и наблюдать за их запросами нечем.
        var endpoint = connection.Hostname is { } host
            ? $"{host}:{connection.RemotePort}"
            : connection.DescribeEndpoint();

        var rule = decision.Rule is null
            ? decision.Reason ?? "по умолчанию"
            : $"#{decision.Rule.Ordinal} {decision.Reason}";

        if (connection.Verdict == ObservedVerdict.Dropped)
            rule = "система отбросила · " + rule;

        var (mode, colourKey) = decision.Mode switch
        {
            RoutingMode.Proxy => ("туннель", "Accent"),
            RoutingMode.Desync => ("десинк", "Warn"),
            _ => ("напрямую", "Muted"),
        };

        return new WatchRow(
            connection.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff"),
            mode,
            (Brush)FindResource(colourKey),
            connection.ExecutableName ?? "?",
            endpoint,
            rule);
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

            if (_perProcess.TryGetValue(row.Process, out int seen))
            {
                _perProcess[row.Process] = seen + 1;
            }
            else
            {
                _perProcess[row.Process] = 1;
                AddProcess(row.Process);
            }

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

    /// <summary>Новая программа — в список по алфавиту, после «все».</summary>
    /// <remarks>
    /// По алфавиту, а не по числу соединений: порядок по числу менялся бы
    /// под рукой, пока человек ищет в раскрытом списке.
    /// </remarks>
    private void AddProcess(string name)
    {
        int at = 1;

        while (at < _processes.Count && string.Compare(_processes[at], name, StringComparison.OrdinalIgnoreCase) < 0)
            at++;

        _processes.Insert(at, name);
    }

    /// <summary>Показать одну программу или все — заново из запомненного.</summary>
    private void ShowProcess(string? name)
    {
        _process = name;
        _rows.Clear();

        foreach (var row in _store.Where(Shows).Take(Limit))
            _rows.Add(row);

        ShowStats();
    }

    private void OnProcessChosen(object sender, SelectionChangedEventArgs e)
    {
        var name = ProcessBox.SelectedItem as string;
        name = name is null or AllProcesses ? null : name;

        if (!string.Equals(name, _process, StringComparison.OrdinalIgnoreCase))
            ShowProcess(name);
    }

    private void OnProcessClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WatchRow row })
            ProcessBox.SelectedItem = row.Process;
    }

    private void ShowStats()
    {
        if (_source is null)
            return;

        long total = Interlocked.Read(ref _total);
        long matched = Interlocked.Read(ref _matched);

        var parts = new List<string>
        {
            $"соединений: {total}",
            $"под правило попало: {matched}",
            $"имён узнано: {_source.DnsNames.Count}",
        };

        if (_source.DroppedCount > 0)
            parts.Add($"потеряно при переполнении: {_source.DroppedCount}");

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

        if (_source is not null)
        {
            // Синхронно и до конца: сессия ETW переживает процесс, и брошенная
            // она останется в системе именем NetZapret до перезагрузки.
            _source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _source = null;
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
        _perProcess.Clear();

        // Выбранная программа остаётся в списке и в отборе: очистка — чтобы
        // смотреть её соединения с чистого листа, а не чтобы сбросить выбор.
        foreach (var name in _processes.Skip(1).Where(n => !string.Equals(n, _process, StringComparison.OrdinalIgnoreCase)).ToList())
            _processes.Remove(name);

        if (_process is not null)
            _perProcess[_process] = 0;

        lock (_pending)
            _pending.Clear();

        Interlocked.Exchange(ref _total, 0);
        Interlocked.Exchange(ref _matched, 0);
        _hidden = 0;
    }
}
