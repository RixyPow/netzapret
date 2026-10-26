using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NetZapret.Core;
using NetZapret.Mobile.Data;
using NetZapret.Mobile.Shell;
using NetZapret.Proxy;
using NetZapret.Subscriptions;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «VPN»: подписки, их серверы, выбор сервера и автоподбора, WARP.
/// </summary>
/// <remarks>
/// <para>
/// Выбор пишется в настройки (<c>PreferredServer</c>) всегда, а работающему
/// движку — командой <c>select</c> сразу: перезапуск ради смены сервера не
/// нужен ни на Windows, ни здесь.
/// </para>
/// <para>
/// Замер — по одному серверу, по очереди (CLAUDE.md, «Продавцов не бить
/// залпом»): у Trust все страны — один вход, и пачка проверок разом
/// закрывала его на минуту. Команда <c>urltest</c> группе мерит всех разом,
/// поэтому её зовём только по серверу (android/DESIGN.md §4).
/// </para>
/// </remarks>
internal sealed class VpnSection : Section
{
    /// <summary>Сколько серверов папки показывать сразу: у подписок бывает и 400.</summary>
    private const int FirstRows = 30;

    private readonly StackPanel _current = new() { Spacing = 8 };
    private readonly StackPanel _folders = new() { Spacing = 12 };
    private readonly TextBlock _progress = Ui.Text("", "muted", "small");
    private readonly Button _measure;
    private readonly StackPanel _header;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _showAll = new(StringComparer.Ordinal);
    private IReadOnlyList<ServerFolder> _data = [];
    private IReadOnlyDictionary<string, int> _delays = new Dictionary<string, int>();
    private (string? Server, bool Automatic) _live;
    private DispatcherTimer? _poll;
    private CancellationTokenSource? _measuring;
    private bool _refreshing;

    public VpnSection(MainView shell) : base(shell)
    {
        var refresh = new Button { Content = Ui.Icon(Icons.Refresh, 20), Classes = { "ghost" }, Width = 48 };
        refresh.Click += (_, _) => _ = RefreshAsync();
        ToolTip.SetTip(refresh, "Обновить подписки");
        Avalonia.Automation.AutomationProperties.SetName(refresh, "Обновить подписки");

        var add = new Button { Content = Ui.Icon(Icons.Plus, 20), Classes = { "ghost" }, Width = 48 };
        add.Click += (_, _) => ShowAddSheet();
        ToolTip.SetTip(add, "Добавить подписку");
        Avalonia.Automation.AutomationProperties.SetName(add, "Добавить подписку");

        _header = Ui.Row(0, refresh, add);

        _measure = Ui.Button("Замерить все", () => _ = MeasureAllAsync());
        _measure.HorizontalAlignment = HorizontalAlignment.Stretch;

        Body.Children.Add(Ui.Card(_current));
        Body.Children.Add(Ui.Stack(6, _measure, _progress));
        Body.Children.Add(WarpCard());
        Body.Children.Add(_folders);

        var addBig = Ui.Button("Добавить подписку", ShowAddSheet, "primary");
        addBig.HorizontalAlignment = HorizontalAlignment.Stretch;
        Body.Children.Add(addBig);

        Shell.EngineChanged += () =>
        {
            if (IsVisibleNow)
                _ = PollAsync();
        };
    }

    public override string Title => "VPN";

    public override Control? HeaderAction => _header;

    protected override void Shown()
    {
        Reload();
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _poll.Tick += (_, _) => _ = PollAsync();
        _poll.Start();
        _ = PollAsync();
    }

    protected override void Hidden()
    {
        _poll?.Stop();
        _poll = null;
    }

    private void Reload()
    {
        if (_data.Count == 0)
        {
            _folders.Children.Clear();
            _folders.Children.Add(Placeholder("Читаю подписки…"));
        }

        Load(() =>
        {
            var folders = VpnData.Read(SubscriptionStores.Store);
            var delays = Host.Status.IsRunning ? _delays : StoredDelays(folders);
            return (folders, delays);
        }, result =>
        {
            _data = result.folders;
            _delays = result.delays;
            ShowAll();
        });
    }

    /// <summary>Задержки из памяти замеров — когда движок стоит и спросить некого.</summary>
    private static IReadOnlyDictionary<string, int> StoredDelays(IReadOnlyList<ServerFolder> folders)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            var health = ServerHealthCache.Load();

            foreach (var row in folders.SelectMany(f => f.Servers))
            {
                if (health.Find(row.Tag) is { } seen)
                    result[row.Tag] = seen.Success && seen.LatencyMs is { } ms ? (int)Math.Round(ms) : -1;
            }
        }
        catch (Exception)
        {
            // Нет памяти замеров — задержек просто не видно.
        }

        return result;
    }

    private async Task PollAsync()
    {
        if (!Host.Status.IsRunning)
        {
            if (_live.Server is not null)
            {
                _live = (null, true);
                ShowAll();
            }

            return;
        }

        try
        {
            var groups = EngineGroups.Parse(await Task.Run(() => Host.CommandAsync("groups", "{}")));
            _live = EngineGroups.Current(groups);
            _delays = EngineGroups.Delays(groups);
            ShowAll();
        }
        catch (Exception)
        {
            // Движок не ответил — покажем, что было; следующий опрос через пять секунд.
        }
    }

    private void ShowAll()
    {
        ShowCurrent();
        ShowFolders();
        _measure.IsEnabled = Host.Status.IsRunning && _measuring is null || _measuring is not null;
        _measure.Content = new TextBlock { Text = _measuring is null ? "Замерить все" : "Остановить замер", TextAlignment = TextAlignment.Center };

        if (_measuring is null && !_refreshing)
            _progress.Text = Host.Status.IsRunning ? string.Empty : "Замер — когда движки работают: серверы меряет сам движок.";

        _progress.IsVisible = !string.IsNullOrEmpty(_progress.Text);
    }

    private void ShowCurrent()
    {
        _current.Children.Clear();
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var preferred = Warp.PreferredExit(settings);
        var status = Host.Status;

        string caption = !settings.Engines.Tunnel ? "Режим «Десинк» — туннель не поднимается"
            : status.IsRunning && _live.Server is not null ? "Сейчас трафик идёт через"
            : preferred is not null ? "Закреплён — поднимется вместе с движками"
            : "Движки не запущены";

        _current.Children.Add(Ui.Caption(caption));

        var tag = status.IsRunning ? _live.Server ?? preferred : preferred;
        var name = tag is null ? "Автоподбор" : TunnelStatus.NameOf(tag);
        var country = tag is null ? string.Empty : CountryTag.Split(tag).Country;

        _current.Children.Add(Ui.Row(10, country.Length > 0 ? Ui.Chip(country) : null, Ui.Text(name, "h2")));

        string standing = !status.IsRunning || _live.Server is null
            ? preferred is null ? "Движок сам возьмёт быстрейший из живых." : "Выбран вами."
            : TunnelStatus.Standing(_live.Server, preferred) switch
            {
                ExitStanding.Pinned => "Закреплён вами.",
                ExitStanding.Other => $"Временно: выбран «{TunnelStatus.NameOf(preferred!)}», движок держит другой.",
                _ => "Выбран автоподбором.",
            };

        var line = standing;

        if (tag is not null && _delays.TryGetValue(tag, out var ms))
            line += ms > 0 ? $" Отвечает · {ms} мс." : " Не отвечает.";

        _current.Children.Add(Ui.Text(line, "muted"));

        if (!settings.HasTunnelExit)
            _current.Children.Add(Ui.Text("VPN не задан: добавьте подписку или включите WARP.", "warn"));
    }

    private Control WarpCard()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        var toggle = Ui.Switch(
            "Cloudflare WARP",
            "Бесплатный выход Cloudflare. Включён — подписки на паузе, весь туннель идёт через него. "
            + "Из России выпускает в Москве: от блокировок оператора спасает, от отказов по стране — нет.",
            settings.WarpEnabled,
            on =>
            {
                var fresh = AppSettings.Load(AppSettings.DefaultPath);
                (fresh with { WarpEnabled = on }).Save(AppSettings.DefaultPath);
                Shell.OfferRestart(on ? "WARP включён" : "WARP выключен");
                ShowAll();
            });

        return Ui.Card(toggle);
    }

    private void ShowFolders()
    {
        _folders.Children.Clear();
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var preferred = settings.PreferredServer;

        // «Авто» — первой строкой, как плитка «Авто» окна.
        var auto = ServerButton(
            null,
            "Авто",
            "Автоподбор: движок сам берёт быстрейший из живых",
            null,
            preferred is null,
            () => _ = ChooseAsync(null, null));

        _folders.Children.Add(Ui.Card(auto));

        if (_data.Count == 0)
        {
            _folders.Children.Add(Ui.Card(Ui.Stack(8,
                Ui.Text("Подписок нет", "h3"),
                Ui.Text("Добавьте ссылку подписки или ключи (vless://, trojan://, ss://, hysteria2://…) — "
                    + "из буфера обмена, QR-кодом или вручную.", "muted"))));
            return;
        }

        foreach (var folder in _data)
            _folders.Children.Add(FolderCard(folder, preferred, settings.WarpEnabled));
    }

    private Control FolderCard(ServerFolder folder, string? preferred, bool warp)
    {
        var key = folder.IsKeys ? "\0keys" : folder.Name;
        // Одна папка — раскрыта сразу: прятать единственный список незачем.
        bool open = _expanded.Contains(key) || _data.Count == 1;

        var title = Ui.Stack(2,
            Ui.Text(folder.Name, "h3"),
            Ui.Text(folder.Detail, "muted", "small"),
            folder.Warning is null ? null : Ui.Text(folder.Warning, "warn", "small"),
            warp && folder.Working ? Ui.Text("На паузе: туннель идёт через WARP.", "faint", "small") : null);

        var toggle = new ToggleSwitch { IsChecked = folder.Working, OnContent = null, OffContent = null, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(toggle, "В работе");
        toggle.IsCheckedChanged += (_, _) => SetWorking(folder, toggle.IsChecked == true);

        var more = new Button { Content = Ui.Icon(Icons.More, 18, Ui.Brush("Muted")), Classes = { "ghost" }, Width = 48, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(more, "Ещё");
        Avalonia.Automation.AutomationProperties.SetName(more, $"Действия с «{folder.Name}»");
        Avalonia.Automation.AutomationProperties.SetName(toggle, $"«{folder.Name}» в работе");
        more.Click += (_, _) => FolderMenu(folder);

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 4 };
        var headButton = new Button { Content = title, Classes = { "row" } };
        headButton.Click += (_, _) =>
        {
            if (!_expanded.Add(key))
                _expanded.Remove(key);

            ShowFolders();
        };

        head.Children.Add(headButton);
        Grid.SetColumn(toggle, 1);
        head.Children.Add(toggle);
        Grid.SetColumn(more, 2);
        head.Children.Add(more);

        var stack = Ui.Stack(4, head);

        if (open)
        {
            var rows = _showAll.Contains(key) ? folder.Servers : folder.Servers.Take(FirstRows).ToList();

            foreach (var server in rows)
            {
                stack.Children.Add(ServerButton(
                    server.Country,
                    server.Name,
                    server.Protocol,
                    server.Tag,
                    folder.Working && server.Tag == preferred,
                    () => _ = ChooseAsync(folder, server)));
            }

            if (rows.Count < folder.Servers.Count)
            {
                stack.Children.Add(Ui.Button($"Показать все ({folder.Servers.Count})", () =>
                {
                    _showAll.Add(key);
                    ShowFolders();
                }, "link"));
            }

            if (folder.Servers.Count == 0)
                stack.Children.Add(Ui.Text("Серверов нет. «Обновить» спросит панель подписки.", "muted", "small"));
        }
        else if (folder.Servers.Count > 0)
        {
            stack.Children.Add(Ui.Text("Нажмите на название, чтобы увидеть серверы.", "faint", "small"));
        }

        return Ui.Card(stack);
    }

    private Button ServerButton(string? country, string name, string? hint, string? tag, bool chosen, Action onTap)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10 };

        Control flag = country is { Length: > 0 } ? Ui.Chip(country)
            : tag is null ? Ui.Icon(Icons.Refresh, 18, Ui.Brush("Accent"))
            : new Border();
        flag.VerticalAlignment = VerticalAlignment.Center;
        flag.Width = 34;

        grid.Children.Add(flag);

        var texts = Ui.Stack(1, Ui.Text(name), hint is null ? null : Ui.Text(hint, "muted", "small"));
        texts.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var right = Ui.Stack(2);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        right.VerticalAlignment = VerticalAlignment.Center;

        if (tag is not null && _delays.TryGetValue(tag, out var ms))
        {
            var delay = Ui.Text(ms > 0 ? $"{ms} мс" : "нет ответа", "small");
            delay.Foreground = Ui.Brush(ms <= 0 ? "Danger" : ms < 200 ? "Accent" : ms < 500 ? "Warn" : "Danger");
            delay.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(delay);
        }

        if (chosen)
        {
            var mark = Ui.Text("Выбран", "accent", "small");
            mark.HorizontalAlignment = HorizontalAlignment.Right;
            mark.FontWeight = FontWeight.SemiBold;
            right.Children.Add(mark);
        }

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var button = new Button { Content = grid, Classes = { "row" }, MinHeight = 52 };

        if (chosen)
            button.Background = Ui.Brush("Raised");

        button.Click += (_, _) => onTap();
        return button;
    }

    /// <summary>Выбрать сервер (или «Авто» — <paramref name="server"/> null).</summary>
    private async Task ChooseAsync(ServerFolder? folder, ServerRow? server)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            string? tag = server?.Tag;

            if (server is not null && settings.WarpEnabled)
            {
                Shell.Say("Сейчас туннель идёт через WARP. Выключите WARP выше — тогда выбор сервера подписки заработает.");
                return;
            }

            // Папка не в работе — включаем её: выбрать сервер вне пула значило бы
            // поставить в настройки тег, которого движок не получит.
            if (folder is { Working: false } && server is not null)
            {
                int index = folder.Servers.ToList().IndexOf(server);
                SetWorkingNow(folder, true);
                var fresh = await Task.Run(() => VpnData.Read(SubscriptionStores.Store));
                var same = fresh.FirstOrDefault(f => f.IsKeys == folder.IsKeys && f.Name == folder.Name);
                tag = same is not null && index >= 0 && index < same.Servers.Count ? same.Servers[index].Tag : server.Tag;
                _data = fresh;
            }

            (AppSettings.Load(AppSettings.DefaultPath) with { PreferredServer = tag }).Save(AppSettings.DefaultPath);

            var label = tag is null ? "Автоподбор" : TunnelStatus.NameOf(tag);

            if (Host.Status.IsRunning)
            {
                var answer = await Host.CommandAsync("select",
                    EngineGroups.SelectArgument(EngineGroups.Selector, tag ?? EngineGroups.AutoPick));

                if (EngineGroups.Error(answer) is { } error)
                    Shell.Offer($"«{label}» выбран, но движок не переключился: {error}", "Перезапустить", () => _ = Shell.RestartAsync());
                else
                    Shell.Say($"Выбран «{label}».");

                await PollAsync();
            }
            else
            {
                Shell.Say($"Выбран «{label}» — применится при запуске.");
                ShowAll();
            }
        }
        catch (Exception ex)
        {
            Shell.Say("Не выбралось: " + ex.GetBaseException().Message);
        }
    }

    private void SetWorking(ServerFolder folder, bool working)
    {
        try
        {
            SetWorkingNow(folder, working);
            Shell.OfferRestart(working ? $"«{folder.Name}» в работе" : $"«{folder.Name}» не в работе");
            Reload();
        }
        catch (Exception ex)
        {
            Shell.Say("Не записалось: " + ex.GetBaseException().Message);
        }
    }

    private static void SetWorkingNow(ServerFolder folder, bool working)
    {
        var store = SubscriptionStores.Store;

        if (folder.IsKeys)
            store.SaveKeys(store.Keys(), working);
        else if (folder.Url is { } url)
            store.SetWorking(url, working);
    }

    private void FolderMenu(ServerFolder folder)
    {
        Sheet? sheet = null;
        var items = Ui.Stack(4);

        if (!folder.IsKeys)
        {
            items.Children.Add(Ui.NavRow("Обновить", "Спросить панель подписки заново", () =>
            {
                sheet?.Close();
                _ = RefreshAsync(folder);
            }, Icons.Refresh));
        }

        items.Children.Add(Ui.NavRow(
            folder.IsKeys ? "Убрать все ключи…" : "Удалить…",
            folder.IsKeys ? "Ключи пропадут из программы" : "Подписка и её запас пропадут из программы",
            () =>
            {
                sheet?.Close();
                _ = RemoveAsync(folder);
            }));

        sheet = Shell.ShowSheet(folder.Name, items);
    }

    private async Task RemoveAsync(ServerFolder folder)
    {
        var yes = await Shell.ConfirmAsync(
            folder.IsKeys ? "Убрать ключи?" : $"Удалить «{folder.Name}»?",
            folder.IsKeys
                ? "Все отдельные ключи пропадут. Вернуть их можно, только вставив заново."
                : "Ссылку выдаёт поставщик, и восстановить её программа не может — сохраните её у себя, если она ещё нужна.",
            folder.IsKeys ? "Убрать" : "Удалить");

        if (!yes)
            return;

        try
        {
            var store = SubscriptionStores.Store;

            if (folder.IsKeys)
                store.SaveKeys([], store.KeysInPool());
            else if (folder.Url is { } url)
            {
                store.Remove(url);

                // Запас — весь ответ панели, с ключами: без подписки ему незачем лежать.
                SubscriptionPool.KeepReserves(store.Entries().Select(e => (string?)e.Url));
            }

            Shell.OfferRestart(folder.IsKeys ? "Ключи убраны" : $"«{folder.Name}» удалена");
            Reload();
        }
        catch (Exception ex)
        {
            Shell.Say("Не удалилось: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Спросить панели подписок; одну — <paramref name="only"/>.</summary>
    private async Task RefreshAsync(ServerFolder? only = null)
    {
        if (_refreshing)
            return;

        _refreshing = true;
        _progress.Text = "Читаю подписки…";
        _progress.IsVisible = true;

        try
        {
            var entries = SubscriptionStores.Store.Entries()
                .Where(e => only is null || e.Url == only.Url)
                .ToList();

            var problems = new List<string>();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await Task.Run(() => SubscriptionPool.ByPanel(entries, e => e.Url, async e =>
            {
                var read = await SubscriptionPool.ReadOneAsync(e.Url, force: true, limit.Token);

                if (read.Source is SubscriptionReadSource.None or SubscriptionReadSource.Reserve && read.Error is { } error)
                {
                    lock (problems)
                        problems.Add($"«{e.Name}»: {error}");
                }
            }));

            _progress.Text = problems.Count == 0
                ? entries.Count == 0 ? "Подписок нет." : "Подписки прочитаны."
                : string.Join("\n", problems);

            if (Host.Status.IsRunning && entries.Count > 0)
                Shell.OfferRestart("Подписки обновлены");
        }
        catch (Exception ex)
        {
            _progress.Text = "Не прочиталось: " + ex.GetBaseException().Message;
        }
        finally
        {
            _refreshing = false;
            Reload();
        }
    }

    private async Task MeasureAllAsync()
    {
        if (_measuring is not null)
        {
            _measuring.Cancel();
            return;
        }

        if (!Host.Status.IsRunning)
            return;

        var tags = VpnData.PoolTags(_data);
        _measuring = new CancellationTokenSource();
        var token = _measuring.Token;
        ShowAll();

        try
        {
            for (int i = 0; i < tags.Count && !token.IsCancellationRequested; i++)
            {
                _progress.Text = $"Замер: {i + 1} из {tags.Count}…";
                await Host.CommandAsync("urltest", EngineGroups.GroupArgument(tags[i]));

                // По одному и с паузой: вход продавца закрывается от пачки проверок.
                await Task.Delay(400, token);
                await PollAsync();
            }

            _progress.Text = token.IsCancellationRequested ? "Замер остановлен." : "Замер окончен.";
        }
        catch (OperationCanceledException)
        {
            _progress.Text = "Замер остановлен.";
        }
        catch (Exception ex)
        {
            _progress.Text = "Замер прерван: " + ex.GetBaseException().Message;
        }
        finally
        {
            _measuring.Dispose();
            _measuring = null;
            ShowAll();
            _progress.IsVisible = true;
        }
    }

    // ── Добавление ────────────────────────────────────────────────────────

    /// <summary>Лист «Добавить подписку»: ссылка или ключи — вручную, из буфера, QR-кодом.</summary>
    internal void ShowAddSheet()
    {
        Sheet? sheet = null;

        // Взятое из буфера и с камеры в поле не кладётся: ссылка подписки —
        // пароль, и на экране ей не место (ui.md §3, «Common rules»).
        string? taken = null;

        var box = new TextBox
        {
            PlaceholderText = "Ссылка подписки или ключ",
            PasswordChar = '•',
        };

        var name = new TextBox { PlaceholderText = "Имя (необязательно)" };
        var status = Ui.Text("", "muted", "small");
        bool working = true;

        void Took(string? text, string from)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                status.Text = from == "камера" ? "Сканирование не поддержано или отменено." : "В буфере пусто.";
                return;
            }

            taken = text;
            box.Text = string.Empty;
            var found = KeyImport.FromText(text);

            status.Text = found.SubscriptionUrl is not null ? $"Взято ({from}): ссылка подписки."
                : found.Keys.Count > 0 ? $"Взято ({from}): ключей — {found.Keys.Count}."
                : $"Взято ({from}), но в нём {found.Problem}.";
        }

        var paste = Ui.Button("Из буфера", () => _ = PasteAsync());
        var scan = Ui.Button("Сканировать QR", () => _ = ScanAsync());
        paste.HorizontalAlignment = HorizontalAlignment.Stretch;
        scan.HorizontalAlignment = HorizontalAlignment.Stretch;

        async Task PasteAsync() => Took(await Host.ReadClipboardAsync(), "буфер");

        async Task ScanAsync() => Took(await Host.ScanQrAsync(), "камера");

        var sources = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        sources.Children.Add(paste);
        Grid.SetColumn(scan, 1);
        sources.Children.Add(scan);

        Button addButton = null!;
        addButton = Ui.Button("Добавить", () => _ = AddAsync(), "primary");
        addButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        async Task AddAsync()
        {
            var text = taken ?? box.Text;
            var found = KeyImport.FromText(text, null);

            if (found.Problem is not null)
            {
                status.Text = "Не нашлось: " + found.Problem + ".";
                return;
            }

            addButton.IsEnabled = false;

            try
            {
                var store = SubscriptionStores.Store;

                if (found.Keys.Count > 0)
                {
                    var keys = store.Keys().Concat(found.Keys).Distinct(StringComparer.Ordinal).ToList();
                    var (parsed, errors) = KeyRing.Parse(found.Keys);
                    store.SaveKeys(keys, working || store.KeysInPool());
                    sheet?.Close();
                    Shell.OfferRestart(errors.Count == 0
                        ? $"Ключей добавлено: {parsed.Count}"
                        : $"Ключей добавлено: {parsed.Count}, не разобралось: {errors.Count}");
                    Reload();
                    return;
                }

                var url = found.SubscriptionUrl!;
                status.Text = "Читаю подписку…";

                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var read = await Task.Run(() => SubscriptionPool.ReadOneAsync(url, force: true, limit.Token));

                if (read.Info is not { Servers.Count: > 0 } info)
                {
                    status.Text = "Панель не отдала серверов: " + (read.Error ?? "ответ пустой") + ".";
                    return;
                }

                var title = string.IsNullOrWhiteSpace(name.Text)
                    ? string.IsNullOrWhiteSpace(info.Title) ? store.FreeName() : info.Title!.Trim()
                    : name.Text.Trim();

                store.Add(title, url, working);
                sheet?.Close();
                _expanded.Add(title);
                Shell.OfferRestart($"«{title}»: {VpnData.Plural(info.Servers.Count)}");
                Reload();
            }
            catch (Exception ex)
            {
                status.Text = "Не добавилось: " + ex.GetBaseException().Message;
            }
            finally
            {
                addButton.IsEnabled = true;
            }
        }

        var content = Ui.Stack(12,
            Ui.Text("Ссылка https://… от продавца VPN или ключи vless://, trojan://, ss://, hysteria2://, "
                + "wireguard://. Ссылка подписки — как пароль: на экране не показывается.", "muted", "small"),
            box,
            sources,
            name,
            Ui.Switch("Сразу в работу", "Серверы попадут в автоподбор", true, on => working = on),
            status,
            addButton);

        sheet = Shell.ShowSheet("Добавить подписку", content);
    }
}
