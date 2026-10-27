using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Core.Rules;
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
    public PathWindow(
        string title,
        PathPick current,
        string? pool,
        string? defaultLabel,
        bool canRecipe,
        string desyncNote,
        IReadOnlyList<ServerPool>? pools,
        string? probe = null)
    {
        InitializeComponent();

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
            PickNote.Text = $"Проверит {probe} и отметит первый открывший путь — до минуты. Движки должны быть запущены.";
        }

        Rebuild();
    }

    private void Rebuild()
    {
        Options.Children.Clear();

        Options.Children.Add(Card(PathPick.Direct, "Напрямую", "Мимо VPN и без десинка — как без программы."));

        Options.Children.Add(Card(PathPick.Desync, "Десинк", _desyncNote,
            Picked == PathPick.Desync && _canRecipe ? RecipeButton() : null));

        Options.Children.Add(Card(PathPick.Proxy, "Через VPN", ProxyNote(),
            Picked == PathPick.Proxy && _poolsOn ? PoolList() : null));

        Options.Children.Add(Card(PathPick.Off, "Выключено",
            "Не действует ни этот маршрут, ни заводское правило на тот же список — имена решают другие правила."));
    }

    private string ProxyNote() => ServerPools.Find(_pools, Pool) is { } pool
        ? $"Пул «{pool.Name}»."
        : _pools.Count == 0
            ? "Через серверы подписок: автоподбор или выбранный на вкладке VPN."
            : "Пул по умолчанию — все серверы.";

    private Button Card(PathPick pick, string head, string note, UIElement? inside = null)
    {
        bool chosen = Picked == pick;

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, FontSize = 15 });

        var caption = new TextBlock { Text = note, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
        caption.SetResourceReference(StyleProperty, "Caption");
        text.Children.Add(caption);

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
                Text = "Свои пулы — папки серверов, например «Россия» или «Скоростной», — заводятся на вкладке VPN.",
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
        if (_probe is not { Length: > 0 } probe || _picking is not null)
            return;

        _picking = new CancellationTokenSource();
        var token = _picking.Token;

        PickButton.IsEnabled = false;
        PickButton.Content = "Проверяю…";
        Fix.Start();

        var progress = new Progress<string>(Fix.Now);
        var stepDone = new Progress<SiteFixStep>(Fix.Add);

        try
        {
            var result = await Task.Run(() => SiteFixer.PickAsync(probe, _canRecipe, progress, token, stepDone), token);

            Fix.Finish(result.Steps);
            ShowPick(result);

            Journal.Write("автоподбор", $"«{_title}» на {probe}: "
                + string.Join("; ", result.Steps.Select(s => $"{s.Path} — {(s.Works ? "да" : "нет")}, {s.Detail}"))
                + (result.Found is { } found
                    ? $" → отмечен путь {found}{(result.RecipeId is { } id ? $", рецепт {id}" : string.Empty)}"
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

    /// <summary>Итог автоподбора: открывший путь отмечается, записывает его «Готово».</summary>
    private void ShowPick(SiteFixResult result)
    {
        const string apply = "Отмечено ниже — запишется по «Готово», заработает после перезапуска движков.";

        if (result.Steps.Count == 0)
        {
            Fix.Outcome(FixProgress.Glyphs.Warning, "Warn", result.Summary, null);
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
