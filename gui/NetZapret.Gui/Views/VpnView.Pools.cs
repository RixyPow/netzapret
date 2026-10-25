using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;

namespace NetZapret.Gui.Views;

/// <summary>Папка пула серверов на вкладке VPN.</summary>
public sealed class PoolRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Перечитать все привязки — папка одна и та же между пересборками, как <see cref="SubRow"/>.</summary>
    public void Refresh() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

    public required ServerPool Pool { get; set; }

    public string Name => Pool.Name;

    public bool Open { get; set; }

    public IReadOnlyList<ServerRow> Servers { get; set; } = [];

    /// <summary>«3 сервера».</summary>
    public string Count { get; set; } = string.Empty;

    /// <summary>Тумблер автоподбора: включён, пока в пуле никто не закреплён.</summary>
    public bool Auto => Pool.Preferred is null;

    /// <summary>В пустом пуле закреплять некого — тумблер неактивен.</summary>
    public bool CanSwitch { get; set; }

    public string Detail { get; set; } = string.Empty;

    /// <summary>Над папкой держат сервер — она подсвечена.</summary>
    public bool Hot { get; set; }

    public double Fade { get; set; } = 1;

    public double ChevronAngle => Chevrons.Angle(Open);

    public Brush Edge => (Brush)Application.Current.FindResource(Hot ? "Accent" : "Border");
}

/// <summary>
/// Пулы серверов на вкладке VPN (владелец 19.10, идеи №6).
/// </summary>
/// <remarks>
/// <para>
/// Пул — папка серверов через все подписки. Сервер кладут в пул, бросив его
/// строку на папку («как в Маршрутах») или из «⋯» строки; сервер может лежать
/// в нескольких пулах. «Выбрать» в папке пула закрепляет сервер в этом пуле.
/// Пула по умолчанию здесь нет — он все серверы, и выбор в нём прежний:
/// «Выбрать» в папке подписки.
/// </para>
/// <para>
/// В движок пул попадает, только когда в него ведёт маршрут — путь маршрута
/// выбирается в «Маршрутах». Поэтому правка пула без маршрутов перезапуска
/// не просит.
/// </para>
/// </remarks>
public partial class VpnView
{
    private const string ServerDragFormat = "netzapret-server";

    /// <summary>Раскрытые папки пулов — между заходами на вкладку, пока окно живо.</summary>
    private static readonly HashSet<string> OpenPools = new(StringComparer.Ordinal);

    private List<PoolRow> _poolRows = [];

    /// <summary>Сколько маршрутов ведёт в каждый пул; <c>null</c> — перечитать файл маршрутов.</summary>
    private Dictionary<string, int>? _poolRoutes;

    private Dictionary<string, int> PoolRoutes() => _poolRoutes ??= CountPoolRoutes();

    /// <summary>Маршруты «через VPN», ведущие в пулы, — по своему файлу маршрутов.</summary>
    private static Dictionary<string, int> CountPoolRoutes()
    {
        try
        {
            return UserRulesFile.Load().Entries
                .Where(e => e.Enabled && !e.Off && e.Mode == RoutingMode.Proxy)
                .Select(e => ServerPools.IdOf(e.Server))
                .OfType<string>()
                .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Включён ли «Selective Routing» — без него блока пулов на вкладке нет
    /// (владелец 21.10: «сделай поле пулов серверов отдельной настройкой»).
    /// </summary>
    private bool _poolsOn;

    /// <summary>Папки пулов из настроек и уже раскрашенных строк подписок.</summary>
    private void ShowPools(AppSettings settings)
    {
        _poolsOn = settings.SelectiveRouting == true;

        if (!_poolsOn)
        {
            _poolRows = [];
            return;
        }

        // Сервер — первой строкой с таким тегом: тег у движка один на сервер.
        var known = new Dictionary<string, ServerRow>(StringComparer.Ordinal);

        foreach (var server in _rows.SelectMany(r => r.Servers))
            known.TryAdd(server.Tag, server);

        var routes = PoolRoutes();
        var before = _poolRows.ToDictionary(p => p.Pool.Id, StringComparer.Ordinal);
        var rows = new List<PoolRow>();

        foreach (var pool in settings.Pools)
        {
            var row = before.TryGetValue(pool.Id, out var kept) ? kept : new PoolRow { Pool = pool };
            row.Pool = pool;
            row.Open = OpenPools.Contains(pool.Id);

            var present = pool.Servers.Where(known.ContainsKey).ToList();
            int missing = pool.Servers.Count - present.Count;

            row.Servers = [.. present.Select(tag => InPool(known[tag], pool))];
            row.Count = Servers(pool.Servers.Count);
            row.CanSwitch = present.Count > 0;

            int routed = routes.TryGetValue(pool.Id, out var n) ? n : 0;

            var detail = pool.Preferred is { } pinned
                ? $"закреплён «{(known.TryGetValue(pinned, out var shown) ? shown.Name : pinned)}»"
                : "автоподбор внутри пула";

            detail += routed > 0
                ? $" · маршрутов сюда: {routed}"
                : " · маршрутов пока нет — пул выбирается в пути маршрута «через VPN»";

            if (missing > 0)
                detail += $" · нет в подписках в работе: {missing}";

            if (pool.Servers.Count == 0)
                detail = "Пусто — перетащите сюда строку сервера из подписки или добавьте его через «⋯» строки.";

            row.Detail = detail;
            rows.Add(row);
        }

        _poolRows = rows;

        PoolsLine.Text = settings.Pools.Count == 0
            ? "Пока все серверы в одном пуле — по умолчанию, и туда идут все маршруты «через VPN». "
                + "Свой пул — папка серверов для части маршрутов: «Россия» для госуслуг, «Скоростной» для загрузок."
            : "Сервер кладут в пул, перетащив его строку на папку пула, или через «⋯» строки; "
                + "«Выбрать» в папке пула закрепляет его в этом пуле и выключает автоподбор пула; "
                + "тумблер «автоподбор» возвращает его, а выключенный — закрепляет прежний сервер или быстрейший. "
                + "Пул по умолчанию — все серверы. "
                + (settings.DeadPoolToDefault
                    ? "Пул, где никто не отвечает, уводит свои маршруты в пул по умолчанию."
                    : "Пул, где никто не отвечает, ждёт — его маршруты не открываются, пока он молчит.");
    }

    /// <summary>Строка сервера в папке пула: «Выбран» — закреплённый в пуле.</summary>
    private ServerRow InPool(ServerRow server, ServerPool pool)
    {
        bool pinned = pool.Preferred == server.Tag;

        return server with
        {
            PoolId = pool.Id,
            CanChoose = !pinned,
            ChooseLabel = pinned ? "выбран" : "выбрать",
            Edge = (Brush)FindResource(pinned ? "AccentFill" : "Border"),

            // «Сейчас» — про выход пула по умолчанию: в пуле он значил бы не то.
            NowShown = Visibility.Collapsed,
        };
    }

    private static string Servers(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => $"{count} сервер",
        (2 or 3 or 4, not (12 or 13 or 14)) => $"{count} сервера",
        _ => $"{count} серверов",
    };

    /// <summary>Пишет пулы в настройки и перерисовывает; ведут ли сюда маршруты — предложить перезапуск.</summary>
    private void SavePools(Func<IReadOnlyList<ServerPool>, IReadOnlyList<ServerPool>> change, string done, string? poolId)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings = settings with { Pools = change(settings.Pools) };
            settings.Save(AppSettings.DefaultPath);

            _poolRoutes = null;
            Redraw();

            bool routed = poolId is not null && PoolRoutes().TryGetValue(poolId, out var n) && n > 0;

            Status.Text = routed ? done + " Применится при следующем запуске движков." : done;

            if (routed)
                this.Offer("Пул изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать пулы: " + ex.GetBaseException().Message;
        }
    }

    private void OnNewPool(object sender, RoutedEventArgs e) => NewPool(null);

    /// <summary>Заводит пул; с сервером — сразу кладёт его туда.</summary>
    private void NewPool(ServerRow? first)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        var window = new FolderNameWindow("Новый пул серверов", [.. settings.Pools.Select(p => p.Name)], pool: true)
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() != true || window.Chosen is not { } name)
            return;

        var pool = ServerPools.Create(settings.Pools, name);
        OpenPools.Add(pool.Id);

        string? tag = first is null ? null : IntoWork(first, settings, out _);

        if (tag is not null)
            pool = pool with { Servers = [tag] };

        SavePools(pools => [.. pools, pool],
            tag is null
                ? $"Пул «{name}» заведён. Перетащите в него серверы, а маршрут направьте в него в «Маршрутах»."
                : $"Пул «{name}» заведён, в нём «{first!.Name}». Маршрут направьте в него в «Маршрутах».",
            pool.Id);
    }

    private void OnPoolFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PoolRow row })
            return;

        row.Open = !row.Open;

        if (row.Open)
            OpenPools.Add(row.Pool.Id);
        else
            OpenPools.Remove(row.Pool.Id);

        Redraw();
    }

    private void OnPoolMore(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: PoolRow row } anchor)
            return;

        var pool = row.Pool;
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };

        // Автоподбор — тумблером в шапке папки (20.10), не пунктом меню.
        menu.Items.Add(Item("", "Переименовать…", () => RenamePool(pool)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(Item("", "Удалить пул…", () => RemovePool(pool)));

        menu.IsOpen = true;
    }

    private void RenamePool(ServerPool pool)
    {
        var others = AppSettings.Load(AppSettings.DefaultPath).Pools
            .Where(p => p.Id != pool.Id)
            .Select(p => p.Name)
            .ToList();

        var window = new FolderNameWindow($"Переименовать «{pool.Name}»", others, pool.Name, "Переименовать", pool: true)
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() != true || window.Chosen is not { } name)
            return;

        // Маршруты ссылаются на ключ пула, а не на имя: переименование их не трогает.
        SavePools(
            pools => [.. pools.Select(p => p.Id == pool.Id ? p with { Name = name } : p)],
            $"Пул «{pool.Name}» теперь «{name}».",
            poolId: null);
    }

    /// <summary>
    /// Удаляет пул; его маршруты переводятся в пул по умолчанию — и в файле
    /// маршрутов тоже, чтобы там не осталось ссылки в никуда.
    /// </summary>
    private void RemovePool(ServerPool pool)
    {
        int routed = PoolRoutes().TryGetValue(pool.Id, out var n) ? n : 0;

        var answer = MessageBox.Show(
            routed > 0
                ? $"Удалить пул «{pool.Name}»? Маршруты, ведущие в него ({routed}), пойдут в пул по умолчанию. Серверы останутся в своих подписках."
                : $"Удалить пул «{pool.Name}»? Серверы останутся в своих подписках.",
            "Удалить пул",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
            return;

        try
        {
            if (routed > 0)
            {
                var file = UserRulesFile.Load();
                var server = ServerPools.RuleServer(pool.Id);

                for (int i = 0; i < file.Entries.Count; i++)
                {
                    if (string.Equals(file.Entries[i].Server, server, StringComparison.OrdinalIgnoreCase))
                        file.ReplaceAt(i, file.Entries[i] with { Server = null });
                }

                file.Save();
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переписать маршруты пула: " + ex.GetBaseException().Message;
            return;
        }

        OpenPools.Remove(pool.Id);

        SavePools(pools => [.. pools.Where(p => p.Id != pool.Id)], $"Пул «{pool.Name}» удалён.", poolId: null);

        if (routed > 0)
            this.Offer("Пул удалён");
    }

    /// <summary>Пункты пулов в меню строки сервера.</summary>
    private void AddPoolItems(ContextMenu menu, ServerRow row)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (settings.SelectiveRouting != true)
            return;

        var pools = settings.Pools;

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

        if (row.PoolId is { } id && ServerPools.Find(pools, id) is { } own)
        {
            if (own.Preferred == row.Tag)
            {
                menu.Items.Add(Item("", "Автоподбор в пуле", () => SavePools(
                    all => ServerPools.SetAuto(all, own.Id, auto: true),
                    $"В пуле «{own.Name}» снова автоподбор.",
                    own.Id)));
            }

            menu.Items.Add(Item("", $"Убрать из пула «{own.Name}»", () => SavePools(
                all => ServerPools.Take(all, own.Id, row.Tag),
                $"«{row.Name}» убран из пула «{own.Name}».",
                own.Id)));

            return;
        }

        // Пулы — пунктами самого меню, а не подменю «В пул»: шаблон нашего
        // меню (Controls.xaml) подменю не раскрывает — «Маршруты» наткнулись
        // на это 13.10, а здесь 19.10 я снова собрал подменю, и «⋯ → В пул»
        // не делал ничего (обсуждение №19, 24.10).
        foreach (var pool in pools)
        {
            bool inside = pool.Servers.Contains(row.Tag, StringComparer.Ordinal);
            var target = pool;

            menu.Items.Add(inside
                ? Item("", $"Уже в пуле «{pool.Name}»", () => { }, enabled: false)
                : Item("", $"В пул «{pool.Name}»", () => PutIntoPool(target, row)));
        }

        menu.Items.Add(Item("", "В новый пул…", () => NewPool(row)));
    }

    /// <summary>Кладёт сервер в пул; подписку вне работы ставит в работу — иначе его нет в конфиге.</summary>
    private void PutIntoPool(ServerPool pool, ServerRow row)
    {
        try
        {
            var tag = IntoWork(row, AppSettings.Load(AppSettings.DefaultPath), out bool switched);

            SavePools(
                pools => ServerPools.Put(pools, pool.Id, tag),
                switched
                    ? $"«{row.Name}» в пуле «{pool.Name}», и подписка «{row.Owner}» поставлена в работу."
                    : $"«{row.Name}» в пуле «{pool.Name}».",
                pool.Id);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>«Выбрать» в папке пула — закрепить сервер в этом пуле.</summary>
    private void PinInPool(string poolId, ServerRow row)
    {
        var pool = ServerPools.Find(AppSettings.Load(AppSettings.DefaultPath).Pools, poolId);

        if (pool is null)
            return;

        SavePools(
            pools => ServerPools.Pin(pools, poolId, row.Tag),
            $"В пуле «{pool.Name}» закреплён «{row.Name}».",
            poolId);
    }

    /// <summary>
    /// Тумблер автоподбора в шапке папки пула (владелец 20.10).
    /// </summary>
    /// <remarks>
    /// Выключение возвращает сервер, закреплённый до включения; не было
    /// такого — закрепляет быстрейший живой из пула по замерам вкладки
    /// (тот же порядок, что «по задержке» в списке).
    /// </remarks>
    private void OnPoolAuto(object sender, RoutedEventArgs e)
    {
        // Тумблер внутри кнопки-папки: без этого нажатие захлопнет папку.
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: PoolRow row })
            return;

        var pool = row.Pool;
        bool auto = pool.Preferred is not null;

        var best = row.Servers.OrderBy(Rank).ThenBy(Ms).FirstOrDefault();
        var after = ServerPools.Find(ServerPools.SetAuto([pool], pool.Id, auto, best?.Tag), pool.Id);

        if (!auto && after?.Preferred is not { } pinned)
        {
            Status.Text = $"В пуле «{pool.Name}» нет серверов из подписок в работе — закреплять некого.";
            row.Refresh();
            return;
        }

        var name = after?.Preferred is { } tag
            ? row.Servers.FirstOrDefault(s => s.Tag == tag)?.Name ?? tag
            : null;

        SavePools(
            pools => ServerPools.SetAuto(pools, pool.Id, auto, best?.Tag),
            auto
                ? $"В пуле «{pool.Name}» автоподбор."
                : $"В пуле «{pool.Name}» автоподбор выключен, закреплён «{name}».",
            pool.Id);
    }

    // Перетаскивание сервера на папку пула.

    private Point _serverPressAt;
    private ServerRow? _serverPressed;
    private PoolRow? _hotPool;
    private ScrollViewer? _pageScroll;

    private void OnServerPress(object sender, MouseButtonEventArgs e)
    {
        _serverPressed = null;

        if (sender is not FrameworkElement { DataContext: ServerRow row } card || Interactive(e.OriginalSource as DependencyObject, card))
            return;

        _serverPressed = row;
        _serverPressAt = e.GetPosition(this);
    }

    /// <summary>Нажали на кнопку внутри строки — это не перетаскивание.</summary>
    private static bool Interactive(DependencyObject? source, FrameworkElement card)
    {
        for (var node = source; node is not null && node != card;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ButtonBase or TextBoxBase or Hyperlink)
                return true;
        }

        return false;
    }

    private void OnServerDrag(object sender, MouseEventArgs e)
    {
        if (_serverPressed is null || e.LeftButton != MouseButtonState.Pressed || sender is not FrameworkElement card)
            return;

        var shift = e.GetPosition(this) - _serverPressAt;

        if (Math.Abs(shift.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(shift.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var row = _serverPressed;
        _serverPressed = null;

        // Пулов нет — класть некуда.
        if (_poolRows.Count == 0)
            return;

        // Событий перетаскивания WPF не шлёт элементу, который сброс не
        // принимает: без AllowDrop у списка прокрутка у края не срабатывала
        // ни разу (владелец 19.10: «страница не прокручивается наверх, если
        // тащу сервер в пул»). Только на время перетаскивания — свойство
        // наследуется всеми строками.
        Page.AllowDrop = true;
        Page.PreviewDragOver += OnPageDragOver;
        _edgeScrolledAt = 0;

        try
        {
            DragDrop.DoDragDrop(card, new DataObject(ServerDragFormat, row), DragDropEffects.Copy);
        }
        finally
        {
            Page.PreviewDragOver -= OnPageDragOver;
            Page.AllowDrop = false;
            Cool();
        }
    }

    private static ServerRow? Dragged(DragEventArgs e) =>
        e.Data.GetDataPresent(ServerDragFormat) ? e.Data.GetData(ServerDragFormat) as ServerRow : null;

    private void OnPoolDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { DataContext: PoolRow pool } || Dragged(e) is not { } row
            || pool.Pool.Servers.Contains(row.Tag, StringComparer.Ordinal))
        {
            e.Effects = DragDropEffects.None;
            Cool();
            return;
        }

        e.Effects = DragDropEffects.Copy;

        if (_hotPool == pool)
            return;

        Cool();
        _hotPool = pool;
        pool.Hot = true;
        pool.Refresh();
    }

    private void OnPoolDragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PoolRow pool } && pool == _hotPool)
            Cool();
    }

    private void OnPoolDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Cool();

        if (sender is not FrameworkElement { DataContext: PoolRow pool } || Dragged(e) is not { } row)
            return;

        // Следующим ходом: внутри Drop запись и перерисовка оставили бы
        // перетаскивание висеть над пересозданными строками.
        Dispatcher.BeginInvoke(() => PutIntoPool(pool.Pool, row), DispatcherPriority.Background);
    }

    private void Cool()
    {
        if (_hotPool is null)
            return;

        _hotPool.Hot = false;
        _hotPool.Refresh();
        _hotPool = null;
    }

    /// <summary>Прокрутка у края списка, пока тянут: пулы над подписками, и тянут вверх.</summary>
    private void OnPageDragOver(object sender, DragEventArgs e)
    {
        // Мимо папки пула бросать некуда — курсор «нельзя»; папка, если она
        // под курсором, разрешит сама (её обработчик идёт после этого).
        e.Effects = DragDropEffects.None;

        _pageScroll ??= Below<ScrollViewer>(Page);

        if (_pageScroll is null)
            return;

        const double edge = 90;
        double y = e.GetPosition(_pageScroll).Y;
        double bottom = _pageScroll.ActualHeight - y;

        // Насколько глубоко курсор в поле у края: 0 — на его границе, 1 — у самого края.
        double depth = y < edge ? -(1 - Math.Max(y, 0) / edge)
            : bottom < edge ? 1 - Math.Max(bottom, 0) / edge
            : 0;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (depth == 0)
        {
            _edgeScrolledAt = 0;
            return;
        }

        // По времени, а не по событию. Событие перетаскивания приходит и на
        // каждое движение мыши, и само по таймеру, и прежние 10–60 точек
        // за событие давали скорость, которую не поймать (владелец 19.10:
        // «прокрутка слишком быстрая, когда сервер тянем»). Теперь — точки
        // в секунду: у границы поля медленно, у самого края быстро (квадрат
        // глубины, чтобы начало было плавным). Первое событие в поле только
        // засекает время — без скачка; долгая пауза между событиями
        // засчитывается не больше чем 50 мс.
        double seconds = _edgeScrolledAt == 0
            ? 0
            : Math.Min(System.Diagnostics.Stopwatch.GetElapsedTime(_edgeScrolledAt, now).TotalSeconds, 0.05);

        _edgeScrolledAt = now;

        double speed = EdgeSlowest + (EdgeFastest - EdgeSlowest) * depth * depth;

        _pageScroll.ScrollToVerticalOffset(_pageScroll.VerticalOffset + Math.Sign(depth) * speed * seconds);
    }

    /// <summary>Когда прокрутка у края двигала список в последний раз; 0 — курсор не у края.</summary>
    private long _edgeScrolledAt;

    /// <summary>Скорость у границы поля, точек в секунду.</summary>
    private const double EdgeSlowest = 120;

    /// <summary>Скорость у самого края, точек в секунду.</summary>
    private const double EdgeFastest = 1000;

    private static T? Below<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            if (child is T found)
                return found;

            if (Below<T>(child) is { } deeper)
                return deeper;
        }

        return null;
    }
}
