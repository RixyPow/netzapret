using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Что выбрано в окне пути.</summary>
public enum PathPick
{
    /// <summary>Снять своё правило — как задано заводскими.</summary>
    Default,

    Direct,

    Desync,

    Proxy,

    /// <summary>Выключить часть целиком (<see cref="NetZapret.Core.Rules.RoutingRule.Off"/>).</summary>
    Off,

    /// <summary>Пин на адреса самого сервиса с десинком (<see cref="RouteSpecial.ServicePin"/>).</summary>
    ServicePin,

    /// <summary>Пин на посредника, напрямую (<see cref="RouteSpecial.RelayPin"/>).</summary>
    RelayPin,

    /// <summary>Приложение Telegram через свой прокси (<see cref="RouteSpecial.TgProxy"/>).</summary>
    TgProxy,
}

/// <summary>Какие особые пути есть у маршрута и что стоит сейчас.</summary>
/// <remarks>
/// Владелец 12.11: пины сервиса, пины посредников и прокси Telegram — особыми
/// режимами окна пути, а не разделом настроек, булавкой и вкладкой.
/// </remarks>
public sealed record PathSpecials
{
    /// <summary>Пин сервиса этой части (раздел «Напрямую» каталога Zapret GUI); <c>null</c> — нет.</summary>
    public PinSolution? Solution { get; init; }

    /// <summary>Почему пин сервиса нельзя поставить в этой сети; <c>null</c> — можно.</summary>
    public string? SolutionBlocked { get; init; }

    /// <summary>Окно подбора адреса посредника; <c>null</c> — части прибивать нечего (подсети).</summary>
    public Func<PinWindow>? RelayPicker { get; init; }

    /// <summary>Часть — приложение Telegram, и прокси есть в поставке.</summary>
    public bool TgProxy { get; init; }

    /// <summary>Особый путь, записанный сейчас.</summary>
    public RouteSpecial Current { get; init; }

    /// <summary>Чей пин записан сейчас.</summary>
    public string? CurrentPin { get; init; }

    /// <summary>Сколько имён записано у нынешнего пина посредника.</summary>
    public int CurrentNames { get; init; }

    /// <summary>Список маршрута — автоподбору: пин сервиса части и имена для пина посредника.</summary>
    public string? List { get; init; }
}

/// <summary>
/// Путь маршрута отдельным окном: выключено, напрямую, десинк с рецептом,
/// через VPN с пулом серверов.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 19.10 (идеи №6): «выбор пути — отдельным окном; десинк раскрывает
/// рецепты, через VPN — пулы». Пулов в меню пути было не вместить: их у
/// человека сколько угодно, и у каждого своё — сколько серверов, кто закреплён.
/// </para>
/// <para>
/// Рецепт выбирается прежним окном рецептов (<see cref="RecipeWindow"/>):
/// оно проверяет рецепты на живом имени, и переносить эту проверку сюда
/// значило бы держать её в двух местах. Отсюда оно открывается кнопкой
/// «Рецепт…» у десинка.
/// </para>
/// <para>
/// С 27.10 — «Автоподбор» (владелец: «я вижу это как кнопку автоподбор в выборе
/// маршрута»): подбор «Сайт не открывается?» на примере маршрута, без записи
/// (<see cref="SiteFixer.PickAsync"/>). Открывший путь отмечается здесь же,
/// пишет его «Готово» — в правило самого маршрута, а не новым правилом на имя.
/// «По умолчанию» — кнопкой между «Отмена» и «Готово» (владелец, тогда же):
/// это не путь, а снятие своего правила.
/// </para>
/// </remarks>
public partial class PathWindow : Window
{
    public PathPick Picked { get; private set; }

    /// <summary>Пул при «через VPN»; <c>null</c> — пул по умолчанию.</summary>
    public string? Pool { get; private set; }

    /// <summary>Нажато «Рецепт…» — выбрать рецепт десинка следом.</summary>
    public bool RecipeWanted { get; private set; }

    /// <summary>Рецепт, найденный автоподбором (идентификатор каталога); пишется при «десинке».</summary>
    public string? FoundRecipe { get; private set; }

    /// <summary>Адрес посредника, подобранный в этом окне; <c>null</c> — не подбирали (остаётся прежний пин).</summary>
    public PinPick? RelayChosen { get; private set; }

    private readonly PathSpecials _specials;

    private readonly string? _defaultLabel;
    private readonly bool _canRecipe;
    private string _desyncNote;
    private readonly IReadOnlyList<ServerPool> _pools;

    /// <summary>«Selective Routing» включён — под «через VPN» выбирается пул.</summary>
    private readonly bool _poolsOn;

    /// <summary>Имя-пример маршрута, на котором идёт автоподбор.</summary>
    private readonly string? _probe;

    private readonly string _title;

    /// <summary>Идущий автоподбор — гасится закрытием окна.</summary>
    private CancellationTokenSource? _picking;

    /// <param name="title">Имя маршрута.</param>
    /// <param name="current">Что стоит сейчас.</param>
    /// <param name="pool">Пул маршрута «через VPN»; <c>null</c> — пул по умолчанию.</param>
    /// <param name="defaultLabel">
    /// Куда маршрут шёл бы без своего правила; <c>null</c> — умолчания у него нет (свой маршрут).
    /// </param>
    /// <param name="canRecipe">Есть ли маршруту рецепт: у списков адресов его нет (владелец 19.10).</param>
    /// <param name="desyncNote">Подпись десинка: каким рецептом он идёт сейчас.</param>
    /// <param name="pools">
    /// Пулы серверов с вкладки VPN; <c>null</c> — «Selective Routing» выключен, и пул не выбирается.
    /// </param>
    /// <param name="probe">Имя-пример маршрута для автоподбора; <c>null</c> — подбирать не на чем.</param>
    /// <param name="specials">Особые пути маршрута; <c>null</c> — их нет.</param>
    public PathWindow(
        string title,
        PathPick current,
        string? pool,
        string? defaultLabel,
        bool canRecipe,
        string desyncNote,
        IReadOnlyList<ServerPool>? pools,
        string? probe = null,
        PathSpecials? specials = null)
    {
        InitializeComponent();

        _specials = specials ?? new PathSpecials();

        // Окно растёт по содержимому вниз от места, где открылось: ход
        // автоподбора добавляет строки, и 03.11 у владельца (экран 1080
        // при 125 % — 832 точки по высоте) низ окна с «Готово» ушёл за
        // панель задач — 860 по высоте больше экрана. Высота — не больше
        // рабочей области, а выросшее окно поднимается, чтобы низ остался
        // на экране; лишнее прокручивается внутри.
        MaxHeight = Math.Min(MaxHeight, SystemParameters.WorkArea.Height - 40);
        SizeChanged += (_, _) => KeepOnScreen();

        _pools = pools ?? [];
        _poolsOn = pools is not null;

        _title = title;
        Head.Text = $"Путь: {title}";
        Picked = current;
        Pool = ServerPools.Find(_pools, pool)?.Id;
        _defaultLabel = defaultLabel;
        _canRecipe = canRecipe;
        _desyncNote = desyncNote;
        _probe = probe;

        if (defaultLabel is { } fallback)
        {
            DefaultButton.Visibility = Visibility.Visible;
            DefaultButton.ToolTip = $"Снять своё правило: маршрут пойдёт, как задано заводскими правилами, — {fallback}.";

            // Своего правила и так нет — снимать нечего.
            DefaultButton.IsEnabled = current != PathPick.Default;

            if (current == PathPick.Default)
            {
                DefaultNow.Text = $"Сейчас своего правила нет — по умолчанию: {fallback}.";
                DefaultNow.Visibility = Visibility.Visible;
            }
        }

        if (probe is { Length: > 0 })
        {
            PickRow.Visibility = Visibility.Visible;
            PickNote.Text = $"Проверит {probe} — до минуты; движки должны быть запущены.";
        }
        else if (_specials.TgProxy)
        {
            // У приложения Telegram имени нет — подбор проверяет пути самого прокси.
            PickRow.Visibility = Visibility.Visible;
            PickNote.Text = "Проверит пути прокси Telegram — прямой и через Cloudflare, несколько секунд.";
        }

        Rebuild();
    }

    private void Rebuild()
    {
        Options.Children.Clear();

        Options.Children.Add(Card(PathPick.Direct, "Напрямую", null, tip: "Мимо VPN и без десинка — как без программы."));

        Options.Children.Add(Card(PathPick.Desync, "Десинк", _desyncNote,
            Picked == PathPick.Desync && _canRecipe ? RecipeButton() : null));

        // Особые пути — между десинком и VPN: дешевле туннеля (ни трафика
        // подписки, ни выхода в другой стране), но пишут системный hosts.
        if (_specials.Solution is { } solution)
        {
            var card = Card(PathPick.ServicePin, "Пин сервиса",
                _specials.SolutionBlocked ?? $"{solution.Name}: адреса самого сервиса, {Names(solution.Names.Count)}; десинк поверх.",
                tip: (solution.Note is { } note ? note + " " : string.Empty)
                    + "Имена прибиваются в hosts к адресам самого сервиса (раздел «Напрямую» каталога Zapret GUI), "
                    + "а рецепт десинка чинит их по имени. Строки остаются в hosts и после выхода из программы.");

            card.IsEnabled = _specials.SolutionBlocked is null || Picked == PathPick.ServicePin;
            Options.Children.Add(card);
        }

        if (_specials.RelayPicker is not null)
        {
            Options.Children.Add(Card(PathPick.RelayPin, "Пин посредника", RelayNote(),
                Picked == PathPick.RelayPin ? RelayButton() : null,
                tip: "Имена прибиваются в hosts к прокси посредника — Zapret DNS, Comss и других: сайт видит "
                    + "чужую страну без VPN. Идёт напрямую, мимо рецептов; адрес раз в 6 часов сверяет автозамена."));
        }

        if (_specials.TgProxy)
        {
            Options.Children.Add(Card(PathPick.TgProxy, "TG Proxy",
                "Telegram Desktop — через свой прокси на этой машине.",
                tip: "Включает прокси Telegram (tg-ws-proxy) третьим движком. Telegram Desktop ходит через него, "
                    + "когда прокси добавлен в нём один раз — ссылкой с вкладки «TG Proxy»."));
        }

        Options.Children.Add(Card(PathPick.Proxy, "Через VPN", ProxyNote(),
            Picked == PathPick.Proxy && _poolsOn ? PoolList() : null));

        Options.Children.Add(Card(PathPick.Off, "Выключено", null,
            tip: "Не действует ни этот маршрут, ни заводское правило на тот же список — имена решают другие правила."));
    }

    private string RelayNote() =>
        RelayChosen is { } chosen
            ? $"Выбрано: {chosen.Source} — {Names(chosen.Hosts.Count)}, {string.Join(", ", chosen.Hosts.Values.Distinct().Take(2))}. Запишется по «Готово»."
            : _specials.Current == RouteSpecial.RelayPin
                ? $"Сейчас: {_specials.CurrentPin ?? "посредник"} — {Names(_specials.CurrentNames)}."
                : "Прокси посредника по DoH — обходит отказ по стране.";

    private static string Names(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => $"{count} имя",
        (2 or 3 or 4, not (12 or 13 or 14)) => $"{count} имени",
        _ => $"{count} имён",
    };

    /// <summary>«Подобрать адрес…» у пина посредника: окно подбора, выбранное — в карточку.</summary>
    private UIElement RelayButton()
    {
        var button = new Button
        {
            Content = RelayChosen is null && _specials.Current != RouteSpecial.RelayPin ? "Подобрать адрес…" : "Подобрать заново…",
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        button.SetResourceReference(StyleProperty, "Ghost");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            AskRelay();
        };

        return button;
    }

    /// <summary>Подбор адреса посредника; закрыли без выбора — путь остаётся прежним.</summary>
    /// <returns>Выбран ли адрес.</returns>
    private bool AskRelay()
    {
        if (_specials.RelayPicker is not { } make)
            return false;

        var window = make();
        window.Owner = this;
        window.ShowDialog();

        if (window.Chosen is not { } chosen)
            return false;

        RelayChosen = chosen;
        Picked = PathPick.RelayPin;
        Rebuild();
        return true;
    }

    private string ProxyNote() => ServerPools.Find(_pools, Pool) is { } pool
        ? $"Пул «{pool.Name}»."
        : _pools.Count == 0
            ? "Через серверы подписок: автоподбор или выбранный на вкладке VPN."
            : "Пул по умолчанию — все серверы.";

    private Button Card(PathPick pick, string head, string? note, UIElement? inside = null, string? tip = null)
    {
        bool chosen = Picked == pick;

        var text = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, FontSize = 15, VerticalAlignment = VerticalAlignment.Center });

        if (tip is not null)
        {
            // Отрицательный отступ по высоте: «i» не растит строку выше соседних без него.
            var info = new ContentControl { Content = tip, Margin = new Thickness(2, -4, 0, -4) };
            info.SetResourceReference(StyleProperty, "Info");
            title.Children.Add(info);
        }

        text.Children.Add(title);

        if (note is not null)
        {
            var caption = new TextBlock { Text = note, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
            caption.SetResourceReference(StyleProperty, "Caption");
            text.Children.Add(caption);
        }

        if (inside is not null)
            text.Children.Add(inside);

        var mark = new TextBlock
        {
            Text = chosen ? "" : string.Empty,
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(12, 2, 0, 0),
        };
        mark.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        mark.SetResourceReference(TextBlock.ForegroundProperty, "Accent");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        Grid.SetColumn(mark, 1);
        grid.Children.Add(mark);

        var button = new Button { Content = grid };
        button.SetResourceReference(StyleProperty, "Choice");
        button.SetResourceReference(BorderBrushProperty, chosen ? "Accent" : "Border");

        button.Click += (_, e) =>
        {
            // Нажатие внутри раскрытого (пул, «Рецепт…») — их дело.
            if (e.OriginalSource != button)
                return;

            // Пин посредника без адреса — не путь: адрес подбирается сразу,
            // закрыли подбор — выбор остаётся прежним.
            if (pick == PathPick.RelayPin && RelayChosen is null && _specials.Current != RouteSpecial.RelayPin)
            {
                AskRelay();
                return;
            }

            Picked = pick;
            Rebuild();
        };

        return button;
    }

    private UIElement RecipeButton()
    {
        var button = new Button { Content = "Рецепт…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        button.SetResourceReference(StyleProperty, "Ghost");
        button.Click += (_, e) =>
        {
            e.Handled = true;
            Picked = PathPick.Desync;
            RecipeWanted = true;
            DialogResult = true;
        };

        return button;
    }

    /// <summary>Пулы под «через VPN»: пул по умолчанию и свои.</summary>
    private UIElement PoolList()
    {
        var list = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        list.Children.Add(PoolCard(null, "Пул по умолчанию", "Все серверы подписок в работе."));

        foreach (var pool in _pools)
        {
            var note = pool.Servers.Count == 0
                ? "Пуст — пока в нём нет серверов, маршрут пойдёт в пул по умолчанию."
                : $"{Servers(pool.Servers.Count)} · " + (pool.Preferred is { } pinned ? $"закреплён «{pinned}»" : "автоподбор");

            list.Children.Add(PoolCard(pool.Id, pool.Name, note));
        }

        if (_pools.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "Свои пулы заводятся на вкладке VPN.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 2, 0, 0),
            };
            hint.SetResourceReference(StyleProperty, "Caption");
            list.Children.Add(hint);
        }

        return list;
    }

    private Button PoolCard(string? id, string head, string note)
    {
        bool chosen = Pool == id;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold });

        var caption = new TextBlock { Text = note, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        caption.SetResourceReference(StyleProperty, "Caption");
        text.Children.Add(caption);

        var button = new Button { Content = text, Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 0, 0, 6) };
        button.SetResourceReference(StyleProperty, "Choice");
        button.SetResourceReference(BorderBrushProperty, chosen ? "Accent" : "Border");

        button.Click += (_, e) =>
        {
            e.Handled = true;
            Pool = id;
            Picked = PathPick.Proxy;
            Rebuild();
        };

        return button;
    }

    private static string Servers(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => $"{count} сервер",
        (2 or 3 or 4, not (12 or 13 or 14)) => $"{count} сервера",
        _ => $"{count} серверов",
    };

    /// <summary>
    /// Автоподбор на имени-примере: подбор без записи, открывший путь — отметкой.
    /// </summary>
    private async void OnPick(object sender, RoutedEventArgs e)
    {
        if (_picking is not null)
            return;

        if (_probe is not { Length: > 0 } probe)
        {
            if (_specials.TgProxy)
                await PickTelegramAsync();

            return;
        }

        _picking = new CancellationTokenSource();
        var token = _picking.Token;

        PickButton.IsEnabled = false;
        PickButton.Content = "Проверяю…";
        Fix.Start();

        var progress = new Progress<string>(Fix.Now);
        var stepDone = new Progress<SiteFixStep>(Fix.Add);

        try
        {
            var list = _specials.List;
            var result = await Task.Run(() => SiteFixer.PickAsync(probe, _canRecipe, progress, token, stepDone, list), token);

            Fix.Finish(result.Steps);
            ShowPick(result);

            Journal.Write("автоподбор", $"«{_title}» на {probe}: "
                + string.Join("; ", result.Steps.Select(s => $"{s.Path} — {(s.Works ? "да" : "нет")}, {s.Detail}"))
                + (result.Found is { } found
                    ? $" → отмечен путь {(result.Special != RouteSpecial.None ? RouteSpecials.Describe(result.Special, result.PinSource) : found.ToString())}"
                      + (result.RecipeId is { } id ? $", рецепт {id}" : string.Empty)
                    : " → путь не найден"));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Окно закрыто посреди подбора — показывать некому.
        }
        catch (Exception ex)
        {
            Fix.Outcome(FixProgress.Glyphs.Warning, "Warn", "Подбор не удался", ex.GetBaseException().Message);
        }
        finally
        {
            _picking?.Dispose();
            _picking = null;

            if (IsLoaded)
            {
                PickButton.IsEnabled = true;
                PickButton.Content = "Автоподбор";
            }
        }
    }

    /// <summary>Автоподбор приложения Telegram: дотянется ли прокси до Telegram.</summary>
    private async Task PickTelegramAsync()
    {
        _picking = new CancellationTokenSource();
        var token = _picking.Token;

        PickButton.IsEnabled = false;
        PickButton.Content = "Проверяю…";
        Fix.Start();

        try
        {
            var progress = new Progress<string>(Fix.Now);
            var stepDone = new Progress<SiteFixStep>(Fix.Add);

            var result = await Task.Run(() => SiteFixer.PickTelegramAsync(progress, token, stepDone), token);

            Fix.Finish(result.Steps);

            Journal.Write("автоподбор", $"«{_title}»: "
                + string.Join("; ", result.Steps.Select(s => $"{s.Path} — {(s.Works ? "да" : "нет")}, {s.Detail}"))
                + (result.Special == RouteSpecial.TgProxy ? " → отмечен TG Proxy" : " → путь не найден"));

            if (result.Special == RouteSpecial.TgProxy)
            {
                Picked = PathPick.TgProxy;
                Fix.Outcome(FixProgress.Glyphs.Ok, "Accent", "Прокси Telegram дотягивается до Telegram",
                    "Отмечено ниже — запишется по «Готово». Сам Telegram Desktop не проверялся: прокси добавляется в нём ссылкой с вкладки «TG Proxy».");
                Rebuild();
            }
            else
            {
                Fix.Outcome(FixProgress.Glyphs.No, "Danger", "Прокси Telegram не дотянулся", result.Summary);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Fix.Outcome(FixProgress.Glyphs.Warning, "Warn", "Подбор не удался", ex.GetBaseException().Message);
        }
        finally
        {
            _picking?.Dispose();
            _picking = null;

            if (IsLoaded)
            {
                PickButton.IsEnabled = true;
                PickButton.Content = "Автоподбор";
            }
        }
    }

    /// <summary>Поднимает окно, если его низ ушёл за рабочую область.</summary>
    private void KeepOnScreen()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - 20;

        if (Top + ActualHeight > bottom)
            Top = Math.Max(area.Top + 20, bottom - ActualHeight);
    }

    /// <summary>Итог автоподбора: открывший путь отмечается, записывает его «Готово».</summary>
    private void ShowPick(SiteFixResult result)
    {
        const string apply = "Отмечено ниже — запишется по «Готово», заработает после перезапуска движков.";

        if (result.Steps.Count == 0)
        {
            Fix.Outcome(FixProgress.Glyphs.Warning, "Warn", result.Summary, null);
            return;
        }

        // Особый путь — своей отметкой: пин сервиса и пин посредника.
        if (result.Special == RouteSpecial.ServicePin && _specials.Solution is not null)
        {
            Picked = PathPick.ServicePin;
            Fix.Outcome(FixProgress.Glyphs.Ok, "Accent", "Открывается с пином сервиса", apply);
            Rebuild();
            return;
        }

        if (result.Special == RouteSpecial.RelayPin && result.PinHosts is { Count: > 0 } hosts && _specials.RelayPicker is not null)
        {
            Picked = PathPick.RelayPin;
            RelayChosen = new PinPick(hosts, null, result.PinSource ?? "автоподбор");
            Fix.Outcome(FixProgress.Glyphs.Ok, "Accent", $"Открывается через посредника {result.PinSource}",
                "Отмечено ниже — пин встанет в hosts по «Готово», действует сразу.");
            Rebuild();
            return;
        }

        switch (result.Found)
        {
            case RoutingMode.Direct:
                Picked = PathPick.Direct;
                Fix.Outcome(FixProgress.Glyphs.Ok, "Accent", "Открывается напрямую — десинк ему мешал", apply);
                break;

            case RoutingMode.Desync:
                Picked = PathPick.Desync;
                FoundRecipe = result.RecipeId;
                _desyncNote = result.Recipe is { } title ? $"рецепт «{title}» — подобран автоподбором" : "десинк пресета — подобран автоподбором";

                Fix.Outcome(FixProgress.Glyphs.Ok, "Accent",
                    result.Recipe is { } recipe ? $"Открывается с рецептом «{recipe}»" : "Открывается с десинком пресета", apply);
                break;

            case RoutingMode.Proxy:
                Picked = PathPick.Proxy;
                Fix.Outcome(FixProgress.Glyphs.Ok, "Accent", "Открывается через VPN", apply);
                break;

            default:
                bool open = result.Steps[0].Works;

                Fix.Outcome(open ? FixProgress.Glyphs.Ok : FixProgress.Glyphs.No, open ? "Accent" : "Danger",
                    open ? $"{result.Host} открывается как есть — менять нечего" : $"{result.Host} не открылся ни одним путём",
                    result.Advice);
                return;
        }

        Rebuild();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e) => _picking?.Cancel();

    private void OnDefault(object sender, RoutedEventArgs e)
    {
        Picked = PathPick.Default;
        DialogResult = true;
    }

    private void OnDone(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
