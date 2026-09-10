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

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _state;
    private readonly ToolStripMenuItem _toggle;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };

    private bool _busy;

    public TrayIcon()
    {
        _state = new ToolStripMenuItem("Проверяю…") { Enabled = false };
        _toggle = new ToolStripMenuItem("Запустить", null, (_, _) => Toggle());

        var menu = new ContextMenuStrip();

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

        _refresh.Tick += (_, _) => Update();
        _refresh.Start();

        Update();
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
    }
}
