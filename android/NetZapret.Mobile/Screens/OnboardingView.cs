using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Mobile.Shell;

namespace NetZapret.Mobile.Screens;

/// <summary>
/// Мастер первого запуска: что делает программа, режим, разрешение на VPN.
/// </summary>
/// <remarks>
/// <para>
/// Три шага вместо четырёх окна Windows: тем на телефоне нет, а подписка
/// добавляется на вкладке VPN тем же листом — второй путь добавления был бы
/// вторым местом для ошибок (ui.md §4 п. 7b: у окна их два, и это долг).
/// </para>
/// <para>
/// «Пропустить» тоже ставит <c>OnboardingDone</c>: мастер, который приходит
/// снова после отказа, — навязчивость, а повторить его можно из «Ещё».
/// </para>
/// </remarks>
internal sealed class OnboardingView : UserControl
{
    private readonly MainView _shell;
    private readonly Action _done;
    private readonly ContentControl _step = new();
    private readonly TextBlock _counter = Ui.Text("", "muted", "small");
    private readonly Button _next;
    private readonly Button _back;
    private int _index;
    private WorkMode _mode;

    public OnboardingView(MainView shell, Action done)
    {
        _shell = shell;
        _done = done;
        _mode = WorkModes.Of(AppSettings.Load(AppSettings.DefaultPath).Engines) ?? WorkMode.Route;

        Background = Ui.Brush("Backdrop");

        _next = Ui.Button("Дальше", Next, "primary", "big");
        _next.HorizontalAlignment = HorizontalAlignment.Stretch;
        _back = Ui.Button("Назад", () => GoTo(_index - 1), "ghost");
        _back.HorizontalAlignment = HorizontalAlignment.Stretch;

        var skip = Ui.Button("Пропустить", () => Finish(applyMode: false), "link");
        skip.HorizontalAlignment = HorizontalAlignment.Right;

        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _counter.VerticalAlignment = VerticalAlignment.Center;
        top.Children.Add(_counter);
        Grid.SetColumn(skip, 1);
        top.Children.Add(skip);

        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,2*"), ColumnSpacing = 10 };
        buttons.Children.Add(_back);
        Grid.SetColumn(_next, 1);
        buttons.Children.Add(_next);

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(20, 12, 20, 20) };
        layout.Children.Add(top);

        var scroll = new ScrollViewer
        {
            Content = _step,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 12),
        };

        Grid.SetRow(scroll, 1);
        layout.Children.Add(scroll);
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);

        Content = layout;
        GoTo(0);
    }

    private const int Steps = 3;

    internal void GoTo(int index)
    {
        _index = Math.Clamp(index, 0, Steps - 1);
        _counter.Text = $"Шаг {_index + 1} из {Steps}";
        _back.IsVisible = _index > 0;

        // Без «Назад» «Дальше» — во всю ширину: пустое место слева читалось как пропавшая кнопка.
        Grid.SetColumn(_next, _index > 0 ? 1 : 0);
        Grid.SetColumnSpan(_next, _index > 0 ? 1 : 2);
        _next.Content = new TextBlock { Text = _index == Steps - 1 ? "Готово" : "Дальше", TextAlignment = Avalonia.Media.TextAlignment.Center };

        _step.Content = _index switch
        {
            0 => Intro(),
            1 => Mode(),
            _ => Permissions(),
        };
    }

    private void Next()
    {
        if (_index == Steps - 1)
            Finish();
        else
            GoTo(_index + 1);
    }

    private void Finish() => Finish(applyMode: true);

    /// <param name="applyMode">«Пропустить» режима не меняет: человек его не выбирал.</param>
    private void Finish(bool applyMode)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var chosen = applyMode ? WorkModes.Choose(settings, _mode) : settings;
            (chosen with { OnboardingDone = true }).Save(AppSettings.DefaultPath);
        }
        catch (Exception ex)
        {
            _shell.Say("Не записалось: " + ex.GetBaseException().Message);
        }

        _done();
    }

    private static Control Intro() => Ui.Stack(16,
        Ui.Icon(Icons.Shield, 64, Ui.Brush("Accent")),
        Ui.Text("NetZapret", "h1"),
        Ui.Text("Открывает то, что закрыто в сети оператора, — и не трогает остальное.", "h3"),
        Ui.Card(Ui.Stack(10,
            Ui.Text("Десинк", "h3"),
            Ui.Text("Ломает рукопожатие так, что фильтр оператора не узнаёт имя сайта. Трафик идёт напрямую, "
                + "адрес остаётся домашним — российские сервисы работают как обычно.", "muted"))),
        Ui.Card(Ui.Stack(10,
            Ui.Text("Туннель", "h3"),
            Ui.Text("VPN через вашу подписку или бесплатный WARP. Адрес — зарубежный: лечит и отказы по стране.", "muted"))),
        Ui.Card(Ui.Stack(10,
            Ui.Text("Маршруты", "h3"),
            Ui.Text("Что через VPN, что напрямую, что с десинком — по сервисам, сайтам и приложениям.", "muted"))));

    private Control Mode()
    {
        var list = Ui.Stack(12,
            Ui.Text("Режим работы", "h1"),
            Ui.Text("Его можно сменить потом на «Главной».", "muted"));

        var buttons = new List<Button>();

        foreach (var mode in new[] { WorkMode.Route, WorkMode.Desync, WorkMode.Tunnel })
        {
            var button = new Button
            {
                Content = Ui.Stack(4,
                    Ui.Text(WorkModes.Name(mode) + (mode == WorkMode.Route ? " — рекомендуется" : string.Empty), "h3"),
                    Ui.Text(WorkModes.Explain(mode), "muted", "small")),
                Classes = { "option" },
            };

            if (mode == _mode)
                button.Classes.Add("selected");

            var captured = mode;
            button.Click += (_, _) =>
            {
                _mode = captured;

                foreach (var other in buttons)
                    other.Classes.Remove("selected");

                button.Classes.Add("selected");
            };

            buttons.Add(button);
            list.Children.Add(button);
        }

        list.Children.Add(Ui.Text("«Гибрид» без подписки работает как «Десинк» — подписку можно добавить позже, на вкладке VPN.", "muted", "small"));
        return list;
    }

    private Control Permissions()
    {
        var addSubscription = Ui.Button("Добавить подписку сейчас", () =>
        {
            Finish();
            _shell.SelectTab(MainView.VpnTab);

            if (_shell.Visible is VpnSection vpn)
                vpn.ShowAddSheet();
        });

        addSubscription.HorizontalAlignment = HorizontalAlignment.Stretch;

        return Ui.Stack(12,
            Ui.Text("Разрешение на VPN", "h1"),
            Ui.Card(Ui.Stack(8,
                Ui.Text("VPN-подключение", "h3"),
                Ui.Text("При первом запуске Android спросит, можно ли NetZapret завести VPN. Без него трафик "
                    + "не завернуть — ни в туннель, ни в десинк. Значок ключа в строке состояния — это он.", "muted"))),
            Ui.Card(Ui.Stack(8,
                Ui.Text("Батарея", "h3"),
                Ui.Text("Чтобы VPN не падал в фоне, снимите для NetZapret оптимизацию батареи: "
                    + "настройки приложения → Батарея → «Без ограничений».", "muted"))),
            Ui.Card(Ui.Stack(8,
                Ui.Text("Частный DNS", "h3"),
                Ui.Text("Если в настройках сети стоит частный DNS с именем сервера, имена идут мимо NetZapret. "
                    + "Поставьте «Автоматически».", "muted"))),
            addSubscription);
    }
}
