using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Mobile.Data;
using NetZapret.Mobile.Shell;
using NetZapret.Proxy;
using NetZapret.Subscriptions;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// «Главная»: запуск и остановка, состояние, режим, сервер, предупреждения.
/// </summary>
/// <remarks>
/// Слова состояний — те же, что у «Главной» Windows (StatusView.ShowState):
/// «Остановлено», «Запускается…», «Работает», «Работает с оговорками»,
/// «Движок не работает». Человек, который видел окно на компьютере, узнаёт
/// телефон без пояснений.
/// </remarks>
internal sealed class HomeSection : Section
{
    private readonly Ellipse _ring = new() { Width = 116, Height = 116, StrokeThickness = 6 };
    private readonly Path _ringIcon = Ui.Icon(Icons.Power, 40);
    private readonly TextBlock _state = Ui.Text("", "h1");
    private readonly TextBlock _hint = Ui.Text("", "muted");
    private readonly Button _go;
    private readonly Button _restart;
    private readonly StackPanel _warnings = new() { Spacing = 8 };
    private readonly StackPanel _mode = new() { Spacing = 10 };
    private readonly StackPanel _params = new() { Spacing = 10 };
    private readonly StackPanel _engines = new() { Spacing = 8 };
    private DispatcherTimer? _poll;
    private string? _live;
    private bool _liveAutomatic;

    public HomeSection(MainView shell) : base(shell)
    {
        _ringIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _ringIcon.VerticalAlignment = VerticalAlignment.Center;

        var ring = new Grid { Width = 116, Height = 116, HorizontalAlignment = HorizontalAlignment.Center };
        ring.Children.Add(_ring);
        ring.Children.Add(_ringIcon);

        _state.HorizontalAlignment = HorizontalAlignment.Center;
        _state.TextAlignment = TextAlignment.Center;
        _hint.TextAlignment = TextAlignment.Center;

        _go = Ui.Button("Запустить", () => _ = ToggleAsync(), "primary", "big");
        _go.HorizontalAlignment = HorizontalAlignment.Stretch;

        _restart = Ui.Button("Перезапустить", () => _ = Shell.RestartAsync(), "link");
        _restart.HorizontalAlignment = HorizontalAlignment.Center;

        Body.Children.Add(Ui.Card(Ui.Stack(12, ring, _state, _hint, _go, _restart)));
        Body.Children.Add(_warnings);
        Body.Children.Add(Ui.Card(_mode));
        Body.Children.Add(Ui.Card(_params));
        Body.Children.Add(Ui.Card(_engines));

        Body.Children.Add(Ui.Card(Ui.Stack(0,
            Ui.Caption("Разбор"),
            Ui.NavRow("Проверка блокировок", "Что закрыто в этой сети и чем это лечится", () => Shell.SelectTab(MainView.CheckTab), Icons.Check),
            Ui.NavRow("Диагностика", "Что мешает VPN на этом телефоне", () => Shell.OpenIn(MainView.MoreTab, new DoctorSection(Shell)), Icons.Doctor),
            Ui.NavRow("Журнал", "Последние строки движка", () => Shell.OpenIn(MainView.MoreTab, new LogSection(Shell)), Icons.Log))));

        Shell.EngineChanged += () =>
        {
            Refresh();

            // Только что поднялись — выход спросить сразу, а не через три секунды.
            if (IsVisibleNow)
                _ = PollExitAsync();
        };
    }

    public override string Title => "Главная";

    protected override void Shown()
    {
        Refresh();
        ShowMode();

        // Живой выход — пока раздел на экране и движки работают: раз в три
        // секунды, как у окна. Невидимой «Главной» опрашивать некого.
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) =>
        {
            // «Подняты N мин назад» прежде обновлялось каждой строкой журнала:
            // извещение о смене состояния шло и на неё. С 27.10 журнал
            // извещений не шлёт (MainView), и минуты считает этот опрос.
            if (Host.Status is { IsRunning: true } running && _state.Text == "Работает")
                _hint.Text = Since(running.StartedAt);

            _ = PollExitAsync();
        };
        _poll.Start();
        _ = PollExitAsync();
    }

    protected override void Hidden()
    {
        _poll?.Stop();
        _poll = null;
    }

    private async Task ToggleAsync()
    {
        if (Host.Status.State is "running" or "starting" || Shell.EngineBusy)
            await Shell.StopAsync();
        else
            await Shell.StartAsync();
    }

    private void Refresh()
    {
        var status = Host.Status;
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var mobile = MobileSettings.Load();
        var facts = SafeFacts();

        bool busy = Shell.EngineBusy;
        bool desyncExpected = settings.NeedsDesync && (mobile.Desync == DesyncEngine.ByeDpi || facts?.Root == true && mobile.UseRoot);

        var (word, hint, colour) = status.State switch
        {
            "running" when desyncExpected && !status.DesyncRunning =>
                ("Работает с оговорками", "Десинк не работает — закрытое по имени открываться не будет. Подробности — в журнале.", "Warn"),
            "running" when status.DesyncRestarts > 0 =>
                ("Работает с оговорками", $"Десинк перезапускался: {status.DesyncRestarts}. Подробности — в журнале.", "Warn"),
            "running" => ("Работает", Since(status.StartedAt), "Accent"),
            "starting" => ("Запускается…", "Сборка конфига и подъём движков — несколько секунд.", "Warn"),
            "stopping" => ("Останавливается…", string.Empty, "Muted"),
            "failed" => ("Движок не работает", status.Error ?? "Причина не названа — смотрите журнал.", "Danger"),
            _ when busy => ("Запускается…", "Сборка конфига и подъём движков — несколько секунд.", "Warn"),
            _ => ("Остановлено", "При первом запуске Android спросит разрешение на VPN — без него трафик не завернуть.", "Faint"),
        };

        _state.Text = word;
        _hint.Text = hint;
        _hint.IsVisible = hint.Length > 0;
        _ring.Stroke = Ui.Brush(colour);
        _ringIcon.Fill = Ui.Brush(colour);

        bool up = status.State is "running" or "starting" || busy;
        _go.Content = new TextBlock { Text = up ? "Остановить" : "Запустить", TextAlignment = TextAlignment.Center };
        _go.Classes.Set("primary", !up);
        _go.Classes.Set("stop", up);
        _go.IsEnabled = status.State != "stopping";
        _restart.IsVisible = status.State == "running" && !busy;

        ShowWarnings(status, facts);
        ShowParams(settings, mobile, facts, status);
        ShowEngines(status, settings, desyncExpected);
    }

    private PlatformFacts? SafeFacts()
    {
        try
        {
            return Host.Facts();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Since(DateTimeOffset? started)
    {
        if (started is not { } at)
            return string.Empty;

        var span = DateTimeOffset.Now - at;
        var text = span.TotalHours >= 1 ? $"{(int)span.TotalHours} ч {span.Minutes} мин" : $"{Math.Max(0, span.Minutes)} мин";
        return "Движки подняты " + text + " назад.";
    }

    private void ShowWarnings(EngineStatus status, PlatformFacts? facts)
    {
        _warnings.Children.Clear();

        // Последняя ошибка движка, пока он работает (при «failed» она уже в подписи под кольцом):
        // ByeDPI перезапускался, команда не прошла — человек видит, что случилось, не открывая журнал.
        if (status.Error is { Length: > 0 } error && status.State != "failed")
            _warnings.Children.Add(Ui.Card(Ui.Text("Последняя ошибка: " + error), "dangercard"));

        // Частный DNS с именем сервера: телефон спрашивает имена сам, по TLS,
        // мимо DNS движка — правила по именам и пины слепнут (docs/android.md §6).
        if (facts?.PrivateDns is { Length: > 0 } privateDns && privateDns != "auto")
        {
            Warn($"Частный DNS телефона — «{privateDns}». Имена спрашиваются мимо NetZapret: маршруты по именам "
                + "и пины не действуют. В настройках Android поставьте «Автоматически» или выключите его.");
        }

        if (facts?.OtherVpnActive == true && !status.IsRunning)
            Warn("Работает другой VPN. Android держит только один: запуск NetZapret его отключит.");

        if (facts?.BatteryOptimized == true)
        {
            Warn("Android вправе усыплять NetZapret, и VPN может падать в фоне. Снимите для него "
                + "оптимизацию батареи в настройках приложения.");
        }

        _warnings.IsVisible = _warnings.Children.Count > 0;
    }

    private void Warn(string text) => _warnings.Children.Add(Ui.Card(Ui.Text(text), "warncard"));

    private void ShowMode()
    {
        _mode.Children.Clear();
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var current = WorkModes.Of(settings.Engines) ?? WorkMode.Route;
        var modes = WorkModes.All;

        var explain = Ui.Text(WorkModes.Explain(current), "muted", "small");
        var complaint = Ui.Text(string.Empty, "warn", "small");

        void Describe(WorkMode mode, AppSettings now)
        {
            explain.Text = WorkModes.Explain(mode);

            // Без подписки и WARP туннелю нечего поднимать: «Гибрид» тогда —
            // тот же «Десинк», «Туннель» — ничего. Говорим словами, как окно.
            complaint.Text = mode != WorkMode.Desync && !now.HasTunnelExit
                ? mode == WorkMode.Tunnel
                    ? "VPN не задан — добавьте подписку или включите WARP на вкладке VPN."
                    : "VPN не задан — работает только десинк. Подписка — на вкладке VPN."
                : string.Empty;
            complaint.IsVisible = complaint.Text.Length > 0;
        }

        Describe(current, settings);

        var segmented = Ui.Segmented(
            modes.Select(WorkModes.Name).ToList(),
            modes.ToList().IndexOf(current),
            index =>
            {
                var mode = modes[index];

                try
                {
                    var fresh = AppSettings.Load(AppSettings.DefaultPath);
                    var chosen = WorkModes.Choose(fresh, mode);
                    chosen.Save(AppSettings.DefaultPath);
                    Describe(mode, chosen);
                    Shell.OfferRestart($"Режим: {WorkModes.Name(mode)}");
                    Refresh();
                }
                catch (Exception ex)
                {
                    Shell.Say("Не записалось: " + ex.GetBaseException().Message);
                }
            });

        _mode.Children.Add(Ui.Caption("Режим работы NZ"));
        _mode.Children.Add(segmented);
        _mode.Children.Add(explain);
        _mode.Children.Add(complaint);
    }

    private void ShowParams(AppSettings settings, MobileSettings mobile, PlatformFacts? facts, EngineStatus status)
    {
        _params.Children.Clear();
        _params.Children.Add(Ui.Caption("Параметры подключения"));

        var server = TunnelStatus.ServerLine(settings, _live);
        var country = _live is not null ? CountryTag.Split(_live).Country
            : Warp.PreferredExit(settings) is { } preferred ? CountryTag.Split(preferred).Country : string.Empty;

        var serverValue = Ui.Row(8,
            country.Length > 0 ? Ui.Chip(country) : null,
            Ui.Text(settings.HasTunnelExit ? server : "не задан"));

        var serverRow = Ui.Pair("Сервер", serverValue);
        _params.Children.Add(Tappable(serverRow, () => Shell.SelectTab(MainView.VpnTab)));

        var dns = DnsSurvey.ByAddress(settings.DnsServer)?.Name ?? settings.DnsServer;
        _params.Children.Add(Tappable(Ui.Pair("DNS", $"{dns} · {DnsRoutes.Word(DnsSection.OnPhone(settings.DnsVia))}"),
            () => Shell.OpenIn(MainView.MoreTab, new DnsSection(Shell))));

        string subscriptions;

        try
        {
            subscriptions = SubscriptionStores.Store.Describe(settings);
        }
        catch (Exception)
        {
            subscriptions = "не прочиталась книга подписок";
        }

        _params.Children.Add(Plain(Ui.Pair("Подписки", subscriptions)));

        // nfqws2 включён, а root ещё не спрашивали: до запуска не известно,
        // что поднимется, — так и сказано. После запуска «нет» значит отказ
        // (хозяин спросил при запуске), и десинк идёт без root.
        var desync = mobile.UseRoot && facts?.Root != true && !status.IsRunning
            ? "nfqws2, если root дадут; иначе " + mobile.DescribeDesync(false)
            : mobile.DescribeDesync(facts?.Root == true);

        _params.Children.Add(Tappable(Ui.Pair("Десинк", desync),
            () => Shell.OpenIn(MainView.MoreTab, new SettingsSection(Shell))));

        if (settings.PresetName is { } preset)
            _params.Children.Add(Plain(Ui.Pair("Пресет", preset)));
    }

    /// <summary>Строка без касания — с тем же отступом, что у строк-кнопок, чтобы подписи стояли в столбец.</summary>
    private static Control Plain(Control content) => new Border { Child = content, Padding = new Thickness(4, 8) };

    private static Control Tappable(Control content, Action onTap)
    {
        var button = new Button { Content = content, Classes = { "row" }, MinHeight = 40 };
        button.Click += (_, _) => onTap();
        return button;
    }

    private void ShowEngines(EngineStatus status, AppSettings settings, bool desyncExpected)
    {
        _engines.Children.Clear();
        _engines.Children.Add(Ui.Caption("Движки"));

        bool running = status.IsRunning;

        string tunnel = !settings.Engines.Tunnel ? "не нужен — режим «Десинк»"
            : !settings.HasTunnelExit ? "не задан выход: нет подписки и WARP"
            : running ? _live is null ? "работает" : $"работает · {TunnelStatus.NameOf(_live)}{(_liveAutomatic ? " (авто)" : string.Empty)}"
            : "остановлен";

        string desync = !desyncExpected ? (settings.Engines.Desync ? "без своего процесса" : "не нужен — всё через VPN")
            : running ? status.DesyncRunning ? "работает" + (status.DesyncRestarts > 0 ? $" · перезапусков: {status.DesyncRestarts}" : string.Empty) : "не работает"
            : "остановлен";

        _engines.Children.Add(EngineRow("Туннель", tunnel, running && settings.Engines.Tunnel && settings.HasTunnelExit));
        _engines.Children.Add(EngineRow("Десинк", desync, running && (!desyncExpected || status.DesyncRunning)));
    }

    private static Control EngineRow(string name, string word, bool good)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 10 };
        grid.Children.Add(Ui.Dot(Ui.Brush(good ? "Accent" : "Faint")));

        var title = Ui.Text(name);
        title.MinWidth = 80;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        var value = Ui.Text(word, "muted");
        Grid.SetColumn(value, 2);
        grid.Children.Add(value);
        return grid;
    }

    private async Task PollExitAsync()
    {
        if (!Host.Status.IsRunning || !IsVisibleNow)
        {
            if (_live is not null)
            {
                _live = null;
                Refresh();
            }

            return;
        }

        try
        {
            var json = await Task.Run(() => Host.CommandAsync("groups", "{}"));
            var (server, automatic) = EngineGroups.Current(EngineGroups.Parse(json));

            if (server != _live || automatic != _liveAutomatic)
            {
                _live = server;
                _liveAutomatic = automatic;
                Refresh();
            }
        }
        catch (Exception)
        {
            // Движок не ответил на опрос — это не поломка: следующий через три секунды.
        }
    }
}
