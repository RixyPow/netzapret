using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NetZapret.Core;
using NetZapret.Mobile.Screens;

namespace NetZapret.Mobile.Shell;

/// <summary>
/// Корень интерфейса телефона: шапка, раздел, нижнее меню и слой листов поверх.
/// </summary>
/// <remarks>
/// <para>
/// Нижнее меню из пяти — Главная, VPN, Маршруты, Проверка, Ещё (ui.md §3):
/// на Windows разделов дюжина в боковой колонке, телефону столько не уместить,
/// и всё реже нужное — DNS, замер скорости, диагностика, настройки — под «Ещё».
/// </para>
/// <para>
/// У каждой вкладки свой стек страниц: «Ещё → Настройки» остаётся открытым,
/// пока человек сходил на «Главную» и вернулся. Кнопка «Назад» Android
/// снимает лист, потом страницу стека, а на корне вкладки отдаётся системе.
/// </para>
/// </remarks>
public sealed class MainView : UserControl
{
    private readonly Func<Section>[] _factories;
    private readonly List<Section>?[] _stacks;
    private readonly Button[] _tabs;
    private readonly Avalonia.Controls.Shapes.Path[] _icons;
    private readonly Border _pageHost = new();
    private readonly TextBlock _title = new() { Classes = { "h2" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _back;
    private readonly ContentControl _headerAction = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly Panel _overlay = new();
    private readonly Border _snack;
    private readonly TextBlock _snackText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _snackAction;
    private readonly Grid _root;
    private Action? _snackDo;
    private DispatcherTimer? _snackTimer;
    private int _tab;
    private readonly List<Sheet> _sheets = [];
    private bool _engineBusy;
    private CancellationTokenSource? _starting;
    private bool _stopRequested;
    private readonly Action _statusHandler;

    internal const int HomeTab = 0, VpnTab = 1, RoutesTab = 2, CheckTab = 3, MoreTab = 4;

    public MainView(IMobileHost host)
    {
        Host = host;
        FontSize = 15;
        Background = Ui.Brush("Backdrop");
        Foreground = Ui.Brush("Text");

        _factories =
        [
            () => new HomeSection(this),
            () => new VpnSection(this),
            () => new RoutesSection(this),
            () => new CheckSection(this),
            () => new MoreSection(this),
        ];

        _stacks = new List<Section>?[_factories.Length];

        _back = new Button { Content = Ui.Icon(Icons.Back, 18), Width = 48, Classes = { "ghost" }, IsVisible = false };
        _back.Click += (_, _) => Back();
        Avalonia.Automation.AutomationProperties.SetName(_back, "Назад");

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(8, 6, 8, 2),
            MinHeight = 52,
        };

        header.Children.Add(_back);
        _title.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        Grid.SetColumn(_headerAction, 2);
        header.Children.Add(_headerAction);

        (string Name, Geometry Icon)[] items =
        [
            ("Главная", Icons.Home),
            ("VPN", Icons.Shield),
            ("Маршруты", Icons.Routes),
            ("Проверка", Icons.Check),
            ("Ещё", Icons.More),
        ];

        var nav = new Grid { Margin = new Thickness(4, 2) };
        _tabs = new Button[items.Length];
        _icons = new Avalonia.Controls.Shapes.Path[items.Length];

        for (int i = 0; i < items.Length; i++)
        {
            nav.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            int index = i;

            var label = new TextBlock
            {
                Text = items[i].Name,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var icon = new Avalonia.Controls.Shapes.Path
            {
                Data = items[i].Icon,
                Width = 22,
                Height = 22,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            // Цвет значка — цвет надписи кнопки (SelectTab): выбранная вкладка зеленеет целиком.
            _icons[i] = icon;

            var button = new Button
            {
                Content = Ui.Stack(3, icon, label),
                Classes = { "nav" },
            };

            button.Click += (_, _) => SelectTab(index);
            Grid.SetColumn(button, i);
            nav.Children.Add(button);
            _tabs[i] = button;
        }

        _snackAction = new Button { Classes = { "link" }, IsVisible = false, VerticalAlignment = VerticalAlignment.Center };
        _snackAction.Click += (_, _) =>
        {
            var act = _snackDo;
            HideSnack();
            act?.Invoke();
        };

        var snackGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, MinHeight = 44 };
        snackGrid.Children.Add(_snackText);
        Grid.SetColumn(_snackAction, 1);
        snackGrid.Children.Add(_snackAction);

        _snack = new Border
        {
            Classes = { "snack" },
            Child = snackGrid,
            Margin = new Thickness(12, 0, 12, 10),
            VerticalAlignment = VerticalAlignment.Bottom,
            IsVisible = false,
        };

        var content = new Grid();
        content.Children.Add(_pageHost);
        content.Children.Add(_snack);

        _root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _root.Children.Add(header);
        Grid.SetRow(content, 1);
        _root.Children.Add(content);

        var navBar = new Border { Classes = { "navbar" }, Child = nav };
        Grid.SetRow(navBar, 2);
        _root.Children.Add(navBar);

        var layers = new Grid();
        layers.Children.Add(_root);
        layers.Children.Add(_overlay);
        Content = layers;

        _statusHandler = () => Dispatcher.UIThread.Post(() => EngineChanged?.Invoke());

        SelectTab(HomeTab);

        if (NeedsOnboarding())
            ShowOnboarding();
    }

    internal IMobileHost Host { get; }

    /// <summary>Состояние движка сменилось — уже в потоке окна.</summary>
    internal event Action? EngineChanged;

    internal bool EngineBusy => _engineBusy;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Подписка на хозяина — пока вид на экране: Android пересоздаёт
        // активность при повороте, и хозяин держал бы старые виды живыми.
        Host.StatusChanged -= _statusHandler;
        Host.StatusChanged += _statusHandler;
        EngineChanged?.Invoke();

        if (TopLevel.GetTopLevel(this) is not { } top)
            return;

        top.BackRequested += OnBackRequested;

        // Край к краю: система рисует свои полосы поверх приложения, и без
        // отступа шапка уходила бы под часы, а меню — под жесты.
        if (top.InsetsManager is { } insets)
        {
            ApplySafeArea(insets.SafeAreaPadding);
            insets.SafeAreaChanged += (_, args) => ApplySafeArea(args.SafeAreaPadding);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Host.StatusChanged -= _statusHandler;

        if (TopLevel.GetTopLevel(this) is { } top)
            top.BackRequested -= OnBackRequested;

        base.OnDetachedFromVisualTree(e);
    }

    private void ApplySafeArea(Thickness padding) => Padding = padding;

    private void OnBackRequested(object? sender, RoutedEventArgs e)
    {
        if (Back())
            e.Handled = true;
    }

    /// <summary>Шаг назад; <c>false</c> — назад некуда, пусть решает система.</summary>
    internal bool Back()
    {
        if (_sheets.Count > 0)
        {
            _sheets[^1].Close();
            return true;
        }

        var stack = _stacks[_tab];

        if (stack is { Count: > 1 })
        {
            var leaving = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            leaving.Hide();
            ShowPage(stack[^1]);
            return true;
        }

        if (_tab != HomeTab)
        {
            SelectTab(HomeTab);
            return true;
        }

        return false;
    }

    internal void SelectTab(int index)
    {
        Current?.Hide();
        _tab = index;

        for (int i = 0; i < _tabs.Length; i++)
        {
            if (i == index)
                _tabs[i].Classes.Add("selected");
            else
                _tabs[i].Classes.Remove("selected");

            _icons[i].Fill = Ui.Brush(i == index ? "Accent" : "Muted");
        }

        var stack = _stacks[index] ??= [_factories[index]()];
        ShowPage(stack[^1]);
    }

    /// <summary>Открыть страницу поверх текущей вкладки (Ещё → Настройки).</summary>
    internal void Open(Section page)
    {
        Current?.Hide();
        var stack = _stacks[_tab] ??= [_factories[_tab]()];
        stack.Add(page);
        ShowPage(page);
    }

    /// <summary>Перейти на вкладку и открыть на ней страницу — с «Главной» в диагностику.</summary>
    internal void OpenIn(int tab, Section page)
    {
        SelectTab(tab);

        // С «Главной» в диагностику — поверх корня «Ещё», а не поверх
        // открытого там прежде: иначе стек копил бы одинаковые страницы.
        var stack = _stacks[tab]!;

        while (stack.Count > 1)
        {
            stack[^1].Hide();
            stack.RemoveAt(stack.Count - 1);
        }

        Open(page);
    }

    private Section? Current => _stacks[_tab] is { Count: > 0 } stack ? stack[^1] : null;

    /// <summary>Слой поверх разделов: мастер и листы — для снимков экрана.</summary>
    internal Controls Overlay => _overlay.Children;

    /// <summary>Открытая сейчас страница — для снимков экрана.</summary>
    internal Section? Visible => Current;

    private void ShowPage(Section page)
    {
        _pageHost.Child = page;
        _title.Text = page.Title;
        _back.IsVisible = _stacks[_tab] is { Count: > 1 };
        _headerAction.Content = page.HeaderAction;
        page.Show();
    }

    // ── Сообщения ─────────────────────────────────────────────────────────

    /// <summary>Короткая строка внизу экрана, сама пропадает.</summary>
    internal void Say(string text) => ShowSnack(text, null, null);

    /// <summary>Строка с действием: «Применится при перезапуске · Перезапустить».</summary>
    internal void Offer(string text, string action, Action act) => ShowSnack(text, action, act);

    /// <summary>
    /// После записи настроек: движки работают — предложить перезапуск, сами не перезапускаем.
    /// </summary>
    /// <remarks>
    /// Правило Windows (ui.md §3, «Common rules»): движки несут весь трафик
    /// телефона, и ронять их в ответ на выбор в списке — не та цена, на
    /// которую человек соглашался.
    /// </remarks>
    internal void OfferRestart(string what)
    {
        if (Host.Status.IsRunning)
            Offer($"{what}. Применится при перезапуске движков.", "Перезапустить", () => _ = RestartAsync());
        else
            Say($"{what}. Применится при запуске.");
    }

    private void ShowSnack(string text, string? action, Action? act)
    {
        _snackText.Text = text;
        _snackAction.Content = action;
        _snackAction.IsVisible = action is not null;
        _snackDo = act;
        _snack.IsVisible = true;

        _snackTimer?.Stop();
        _snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(action is null ? 4 : 8) };
        _snackTimer.Tick += (_, _) => HideSnack();
        _snackTimer.Start();
    }

    private void HideSnack()
    {
        _snackTimer?.Stop();
        _snack.IsVisible = false;
        _snackDo = null;
    }

    // ── Движки ────────────────────────────────────────────────────────────

    /// <summary>Поднять движки; повторное нажатие, пока идёт прошлое, не запускает второй раз.</summary>
    internal async Task StartAsync()
    {
        if (_engineBusy)
            return;

        _engineBusy = true;
        _starting = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = _starting.Token;
        EngineChanged?.Invoke();

        try
        {
            var problem = await Host.StartAsync(token);

            if (problem is not null)
                Say(problem);
        }
        catch (OperationCanceledException)
        {
            Say(_stopRequested ? "Запуск отменён." : "Запуск не уложился в две минуты — остановлено.");
        }
        catch (Exception ex)
        {
            Say("Не запустилось: " + ex.GetBaseException().Message);
        }
        finally
        {
            _starting?.Dispose();
            _starting = null;
            _stopRequested = false;
            _engineBusy = false;
            EngineChanged?.Invoke();
        }
    }

    /// <summary>Остановить; посреди запуска — отменить его и остановить то, что успело подняться.</summary>
    /// <remarks>
    /// Запуск ждёт до двух минут (подписка, разрешение на VPN), и всё это время
    /// «Остановить» обязано работать: иначе человек с зависшим запуском мог бы
    /// только убить приложение.
    /// </remarks>
    internal async Task StopAsync()
    {
        if (_starting is not null)
        {
            _stopRequested = true;
            _starting.Cancel();

            try
            {
                await Host.StopAsync();
            }
            catch (Exception ex)
            {
                Say("Не остановилось: " + ex.GetBaseException().Message);
            }

            return;
        }

        if (_engineBusy)
            return;

        _engineBusy = true;
        EngineChanged?.Invoke();

        try
        {
            await Host.StopAsync();
        }
        catch (Exception ex)
        {
            Say("Не остановилось: " + ex.GetBaseException().Message);
        }
        finally
        {
            _engineBusy = false;
            EngineChanged?.Invoke();
        }
    }

    internal async Task RestartAsync()
    {
        await StopAsync();
        await StartAsync();
    }

    // ── Листы ─────────────────────────────────────────────────────────────

    /// <summary>Лист снизу поверх раздела — выбор пути, добавление подписки.</summary>
    internal Sheet ShowSheet(string title, Control content)
    {
        var sheet = new Sheet(this, title, content);
        _sheets.Add(sheet);
        _overlay.Children.Add(sheet.Root);
        return sheet;
    }

    internal void Remove(Sheet sheet)
    {
        _sheets.Remove(sheet);
        _overlay.Children.Remove(sheet.Root);
    }

    /// <summary>Спросить «да или нет» перед необратимым — удалением подписки.</summary>
    internal Task<bool> ConfirmAsync(string title, string text, string yes)
    {
        var done = new TaskCompletionSource<bool>();
        Sheet? sheet = null;

        var yesButton = Ui.Button(yes, () => { done.TrySetResult(true); sheet?.Close(); }, "stop");
        var noButton = Ui.Button("Отмена", () => { done.TrySetResult(false); sheet?.Close(); });
        yesButton.HorizontalAlignment = HorizontalAlignment.Stretch;
        noButton.HorizontalAlignment = HorizontalAlignment.Stretch;

        sheet = ShowSheet(title, Ui.Stack(12, Ui.Text(text, "muted"), yesButton, noButton));
        sheet.Closed += () => done.TrySetResult(false);
        return done.Task;
    }

    // ── Первый запуск ─────────────────────────────────────────────────────

    private static bool NeedsOnboarding()
    {
        // Нечитаемые настройки — не первый запуск: мастер поверх них
        // переписал бы выбор человека значениями по умолчанию (так же окно
        // Windows, MainWindow.ShowOnboardingIfNeeded).
        var settings = AppSettings.TryLoad(AppSettings.DefaultPath, out var result);
        return result != AppSettings.ReadResult.Unreadable && !settings.OnboardingDone;
    }

    internal void ShowOnboarding()
    {
        Control? wizard = null;
        wizard = new OnboardingView(this, () =>
        {
            _overlay.Children.Remove(wizard!);

            // Мастер мог сменить режим — раздел под ним перечитывает настройки.
            if (Current is { } page)
            {
                page.Hide();
                ShowPage(page);
            }
        });
        _overlay.Children.Add(wizard);
    }
}

/// <summary>Лист снизу: затемнение, заголовок, содержимое с прокруткой.</summary>
internal sealed class Sheet
{
    private readonly MainView _shell;

    public Sheet(MainView shell, string title, Control content)
    {
        _shell = shell;

        var scrim = new Border { Background = Ui.Brush("Scrim") };
        scrim.PointerPressed += (_, _) => Close();

        var grip = new Border
        {
            Width = 40,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = Ui.Brush("Line"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(grip);
        body.Children.Add(Ui.Text(title, "h2"));
        body.Children.Add(content);

        var panel = new Border
        {
            Classes = { "sheet" },
            Child = new ScrollViewer
            {
                Content = body,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            },
            VerticalAlignment = VerticalAlignment.Bottom,

            // Лист не закрывает экран целиком: сверху видно затемнённый
            // раздел, и понятно, куда вернёт касание мимо листа.
            MaxHeight = shell.Bounds.Height > 0 ? shell.Bounds.Height * 0.88 : 760,
        };

        Root = new Grid();
        Root.Children.Add(scrim);
        Root.Children.Add(panel);
    }

    public Grid Root { get; }

    public event Action? Closed;

    public void Close()
    {
        _shell.Remove(this);
        Closed?.Invoke();
        Closed = null;
    }
}
