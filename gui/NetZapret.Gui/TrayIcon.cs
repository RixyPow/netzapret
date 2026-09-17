using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using NetZapret.Supervisor;
using Application = System.Windows.Application;

namespace NetZapret.Gui;

/// <summary>
/// Значок в трее: состояние обхода и две кнопки к нему.
/// </summary>
/// <remarks>
/// <para>
/// Отвечает на вопрос, ради которого окно чаще всего и открывали, — идёт ли
/// обход, — не открывая окна вовсе. Состояние читается из того же файла,
/// что показывает «Главная», поэтому разойтись им негде.
/// </para>
/// <para>
/// «Выйти» закрывает только интерфейс: движки живут отдельным процессом
/// и переживают закрытие окна. Это сказано прямо в самом пункте — иначе
/// человек уверен, что выключил обход, а тот работает.
/// </para>
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>Ключ, по которому программа запускается сразу в трей.</summary>
    public const string Switch = "--tray";

    // Те же цвета, что в Theme/Palette.xaml. Второй раз названы потому, что
    // меню трея рисует WinForms, а он словаря ресурсов WPF не видит; расходиться
    // им нельзя — это одно и то же меню в глазах человека.
    private static readonly Color Surface = ColorTranslator.FromHtml("#161B22");
    private static readonly Color Raised = ColorTranslator.FromHtml("#1C2128");
    private static readonly Color Edge = ColorTranslator.FromHtml("#30363D");
    private static readonly Color Text = ColorTranslator.FromHtml("#E6EDF3");
    private static readonly Color Muted = ColorTranslator.FromHtml("#8B949E");

    /// <summary>Тёмное меню: WinForms по умолчанию рисует светлое, системное.</summary>
    private sealed class DarkMenu : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Surface;
        public override Color MenuBorder => Edge;
        public override Color MenuItemBorder => Edge;
        public override Color MenuItemSelected => Raised;
        public override Color MenuItemSelectedGradientBegin => Raised;
        public override Color MenuItemSelectedGradientEnd => Raised;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color SeparatorDark => Edge;
        public override Color SeparatorLight => Edge;
    }

    /// <summary>
    /// Сообщение, которым оболочка объявляет, что область уведомлений создана.
    /// </summary>
    /// <remarks>
    /// Рассылается всем окнам верхнего уровня — и при первом создании панели
    /// задач, и после перезапуска проводника.
    /// </remarks>
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    private static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    /// <summary>
    /// Окно, слушающее рассылку о создании панели задач.
    /// </summary>
    /// <remarks>
    /// Своё, потому что в режиме <c>--tray</c> никакого другого окна у нас нет
    /// вовсе: программа поднимается значком и живёт без него.
    /// </remarks>
    private sealed class ShellWatcher : NativeWindow
    {
        private readonly Action _restored;

        public ShellWatcher(Action restored)
        {
            _restored = restored;
            CreateHandle(new CreateParams());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == TaskbarCreated)
                _restored();

            base.WndProc(ref m);
        }
    }

    private readonly NotifyIcon _icon;
    private readonly ToolStripLabel _state;
    private readonly ToolStripMenuItem _toggle;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private ShellWatcher? _shell;

    /// <summary>Сколько раз ещё перевыставить значок после запуска.</summary>
    /// <remarks>
    /// Считается вниз по тикам опроса; ноль означает, что попытки исчерпаны
    /// и значок больше не трогаем.
    /// </remarks>
    private int _readds = 2;

    private bool _busy;

    public TrayIcon()
    {
        // Подписью, а не выключенным пунктом: выключенный рисуется системным
        // серым, который на тёмном фоне почти не читается, и подсвечивается
        // при наведении, обещая нажатие, которого не будет.
        _state = new ToolStripLabel("Проверяю…") { ForeColor = Muted };

        _toggle = new ToolStripMenuItem("Запустить", null, (_, _) => Toggle());

        var menu = new ContextMenuStrip
        {
            BackColor = Surface,
            ForeColor = Text,
            ShowImageMargin = false,
            Renderer = new ToolStripProfessionalRenderer(new DarkMenu()) { RoundedEdges = false },
        };

        menu.Items.Add(_state);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Показать окно", null, (_, _) => Show()));
        menu.Items.Add(_toggle);
        menu.Items.Add(new ToolStripSeparator());

        // Два выхода, а не один с оговоркой. Одним пунктом пришлось бы
        // угадывать намерение: «убрать значок с глаз» и «выключить обход» —
        // разные желания, и ошибка в любую сторону дорогая. Либо человек
        // уверен, что выключил, а трафик идёт через туннель; либо он терял
        // связь посреди работы, всего лишь закрыв интерфейс.
        menu.Items.Add(new ToolStripMenuItem(
            "Выйти, оставить движки работать", null, (_, _) => Quit(stopEngines: false)));

        menu.Items.Add(new ToolStripMenuItem(
            "Выйти и остановить движки", null, (_, _) => Quit(stopEngines: true)));

        _icon = new NotifyIcon
        {
            Icon = OwnIcon(),
            Text = "NetZapret",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _icon.DoubleClick += (_, _) => Show();

        // Оболочка объявляет о создании области уведомлений один раз. Мы
        // запускаемся автозапуском в ту же секунду, что и проводник, и если
        // наше окно появилось позже рассылки, сообщения мы не получим никогда —
        // а повторять добавление значка нечему. Так он и пропадал: при запуске
        // руками виден, при входе в систему нет.
        try
        {
            _shell = new ShellWatcher(Readd);
        }
        catch (Exception)
        {
            // Без наблюдателя значок переживёт всё, кроме перезапуска
            // проводника. Это хуже, чем с ним, но лучше, чем не открыться.
        }

        _refresh.Tick += (_, _) => Update();
        _refresh.Start();

        Update();
    }

    /// <summary>
    /// Добавляет значок заново.
    /// </summary>
    /// <remarks>
    /// Через снятие и возврат видимости: это заставляет систему удалить
    /// запись и создать её снова. Просто выставить <c>Visible = true</c>
    /// у уже видимого значка не делает ничего, а нам нужно именно повторное
    /// добавление — первое могло не дойти.
    /// </remarks>
    private void Readd()
    {
        try
        {
            _icon.Visible = false;
            _icon.Visible = true;
        }
        catch (Exception)
        {
            // Значок — удобство. Из-за него не падаем.
        }
    }

    /// <summary>
    /// Значок программы.
    /// </summary>
    /// <remarks>
    /// Берётся из самой программы, а не из файла рядом: поставка — один
    /// исполняемый файл, и класть возле него отдельный .ico значит вернуть
    /// в папку лишнее. Пока своего значка нет, приезжает стандартный —
    /// это некрасиво, но работает.
    /// </remarks>
    private static Icon OwnIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } own)
                return own;
        }
        catch (Exception)
        {
            // Значок обязателен: без него в трее остаётся пустое место,
            // и это выглядит как поломка, а не как отсутствие картинки.
        }

        return SystemIcons.Application;
    }

    private void Update()
    {
        // Две попытки в первые шесть секунд, и только они.
        //
        // Рассылки от оболочки может не быть вовсе: при входе в систему она
        // уходит раньше, чем мы успеваем завести окно. Узнать, дошёл ли значок,
        // система не даёт — NotifyIcon не отдаёт наружу ответ Shell_NotifyIcon,
        // — поэтому добавляем вслепую и ровно дважды. Больше нельзя: каждое
        // добавление гасит и зажигает значок, и десяток таких подряд человек
        // увидит миганием.
        if (_readds > 0)
        {
            _readds--;
            Readd();
        }

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        var running = state is not null && state.IsSupervisorAlive();

        var text = !running
            ? "Остановлено"
            : state!.Services.All(s => s.Health == ServiceHealth.Healthy)
                ? "Работает"
                : "Работает с оговорками";

        _state.Text = text;
        _toggle.Text = running ? "Остановить" : "Запустить";
        _toggle.Enabled = !_busy;

        // Подсказка ограничена шестьюдесятью тремя знаками: Windows режет
        // длиннее молча, поэтому здесь коротко и по делу.
        _icon.Text = "NetZapret — " + text;
    }

    private async void Toggle()
    {
        if (_busy)
            return;

        _busy = true;
        _toggle.Enabled = false;

        try
        {
            var state = SupervisorState.Load(SupervisorState.DefaultPath);

            if (state is not null && state.IsSupervisorAlive())
                await EngineControl.StopAsync(CancellationToken.None);
            else
                await EngineControl.StartAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Разбор — в окне: там есть куда написать причину.
        }
        finally
        {
            _busy = false;
            Update();
        }
    }

    /// <summary>Показывает окно, создавая его, если программа поднялась в трей.</summary>
    public static void Show() => Application.Current.Dispatcher.Invoke(() =>
    {
        var window = Application.Current.MainWindow ??= new MainWindow();

        window.Show();

        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;

        window.Activate();
    });

    /// <summary>
    /// Закрывает интерфейс, по просьбе — вместе с движками.
    /// </summary>
    /// <remarks>
    /// Остановка ждётся до конца, а не пускается вдогонку: программа,
    /// закрывшаяся раньше, чем погасли движки, оставила бы супервизор
    /// без присмотра, и следующий запуск упёрся бы в занятый TUN.
    /// </remarks>
    private async void Quit(bool stopEngines)
    {
        if (stopEngines)
        {
            _state.Text = "Останавливаю…";

            try
            {
                await EngineControl.StopAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // Не погасли — выходим всё равно: держать интерфейс открытым
                // ради несостоявшейся остановки незачем, а движки видны
                // в диспетчере задач.
            }
        }

        App.Exiting = true;
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        _refresh.Stop();

        // Скрыть до удаления обязательно: иначе значок остаётся висеть
        // в панели до наведения мышью, и программа выглядит незакрытой.
        _icon.Visible = false;
        _icon.Dispose();

        // Окно наблюдателя — тоже окно, и оставленное висеть оно держит
        // процесс живым после того, как всё остальное закрыто.
        _shell?.DestroyHandle();
        _shell = null;
    }
}
