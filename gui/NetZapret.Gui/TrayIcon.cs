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
    private TrayMenu? _menu;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    private ShellWatcher? _shell;

    /// <summary>Сколько раз ещё перевыставить значок после запуска.</summary>
    /// <remarks>
    /// Считается вниз по тикам опроса; ноль означает, что попытки исчерпаны
    /// и значок больше не трогаем.
    /// </remarks>
    private int _readds = 2;

    private bool _busy;

    /// <summary>Трей сам гасит движки — «Останавливается…», а не «Работает» до последней секунды.</summary>
    private bool _stopping;

    public TrayIcon()
    {
        // Меню — своё окно WPF (TrayMenu), а не ContextMenuStrip. Системное
        // меню WinForms не видит словаря ресурсов, и цвета ему приходилось
        // зашивать второй раз — первой тёмной палитрой, так что при любой
        // другой теме меню оставалось чужим (владелец 24.09).
        _icon = new NotifyIcon
        {
            Icon = OwnIcon(),
            Text = "NetZapret",
            Visible = true,
        };

        // Любой кнопкой — меню: в нём и состояние, и путь к окну. Двойной
        // щелчок по-прежнему открывает окно сразу.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
                OpenMenu();
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

        var status = TrayStatus.Read(_busy, _stopping);

        // Меню перерисовывается, только пока его видно: спрятанному
        // незачем, а открытое обязано показывать живое состояние.
        if (_menu is { IsVisible: true })
            _menu.Render(status);

        // Подсказка ограничена шестьюдесятью тремя знаками: Windows режет
        // длиннее молча, поэтому здесь коротко и по делу.
        _icon.Text = "NetZapret — " + status.Headline;
    }

    /// <summary>Меню сейчас открывается — второй щелчок ждёт, а не открывает его ещё раз.</summary>
    private bool _opening;

    private void OpenMenu()
    {
        // Show() у окна прокачивает сообщения, пока создаёт его, и щелчок
        // по значку посреди этого входил сюда второй раз — повторный Show()
        // того же окна. Так ли упало у пользователя 02.10, не проверено.
        if (_opening)
            return;

        _opening = true;

        try
        {
            _menu ??= new TrayMenu(Toggle, ChooseMode, Show, Quit);
            _menu.Render(TrayStatus.Read(_busy, _stopping));

            try
            {
                _menu.PopUp();
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // 02.10, у пользователя 0.11.1: «Корневой элемент Visual для
                // VisualTarget не может иметь родительский элемент» из
                // Window.Show() — окно меню, живущее всё время работы, WPF
                // заново показать отказался, и программа падала целиком.
                // Почему окно так испортилось, не выяснено; меню без
                // состояния, так что его не жалко собрать заново.
                Discard(_menu);

                _menu = new TrayMenu(Toggle, ChooseMode, Show, Quit);
                _menu.Render(TrayStatus.Read(_busy, _stopping));
                _menu.PopUp();
            }
        }
        finally
        {
            _opening = false;
        }
    }

    private static void Discard(TrayMenu menu)
    {
        try
        {
            menu.Close();
        }
        catch (Exception)
        {
            // Окно и так сломано — закрыть его не обязательно.
        }
    }

    private async void Toggle()
    {
        if (_busy)
            return;

        // Не по одному файлу состояния: он появляется, лишь когда движки
        // подняты, и посреди подъёма трей предлагал запустить второй раз.
        _busy = true;
        _stopping = EngineControl.IsRunning;
        Update();

        try
        {
            if (_stopping)
                await EngineControl.StopAsync("меню трея", CancellationToken.None);
            else
                await EngineControl.StartAsync("меню трея", CancellationToken.None);
        }
        catch (Exception)
        {
            // Разбор — в окне: там есть куда написать причину.
        }
        finally
        {
            _busy = false;
            _stopping = false;
            Update();
        }
    }

    /// <summary>
    /// Режим из меню: записать его и сразу перезапустить движки.
    /// </summary>
    /// <remarks>
    /// Перезапуском, а не просьбой перезапустить: переключатель в трее
    /// и просили (обсуждение №17), чтобы семь шагов через окно свести к одному.
    /// Остановленные движки поднимаются: человек выбрал, как работать сейчас,
    /// а не «запомни на потом». Тот же режим ещё раз — ничего не трогает.
    /// </remarks>
    private async void ChooseMode(NetZapret.Core.Rules.WorkMode mode)
    {
        if (_busy)
            return;

        _busy = true;
        Update();

        try
        {
            var settings = NetZapret.Core.AppSettings.Load(NetZapret.Core.AppSettings.DefaultPath);

            if (NetZapret.Core.Rules.WorkModes.Of(settings.Engines) == mode
                || !NetZapret.Core.Rules.WorkModes.CanChoose(settings, mode))
                return;

            NetZapret.Core.Rules.WorkModes.Choose(settings, mode).Save(NetZapret.Core.AppSettings.DefaultPath);

            var who = $"меню трея: режим «{NetZapret.Core.Rules.WorkModes.Name(mode)}»";

            if (EngineControl.IsRunning)
                await EngineControl.RestartAsync(who, CancellationToken.None);
            else
                await EngineControl.StartAsync(who, CancellationToken.None);
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
        // По типу, а не по MainWindow: в режиме --tray первым окном создаётся
        // меню трея, и WPF сам назначает главным его. Тогда «Открыть окно»
        // показывало бы то же меню.
        var window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault() ?? new MainWindow();
        Application.Current.MainWindow = window;

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
            _busy = true;
            _stopping = true;
            Update();

            try
            {
                await EngineControl.StopAsync("меню трея: выход с остановкой", CancellationToken.None);
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
        _menu?.Close();

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
