using System.Drawing;
using System.IO;
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

    /// <summary>Значок, пока он жив: через него предлагают перезапуск, когда окна не видно.</summary>
    public static TrayIcon? Current { get; private set; }

    /// <summary>Что предложено уведомлением; <c>null</c> — уведомление не про перезапуск.</summary>
    private string? _offered;

    /// <summary>Страница, которую открывает нажатие на уведомление (<see cref="Notify"/>); <c>null</c> — никакая.</summary>
    private string? _link;

    public TrayIcon()
    {
        Current = this;

        // Меню — своё окно WPF (TrayMenu), а не ContextMenuStrip. Системное
        // меню WinForms не видит словаря ресурсов, и цвета ему приходилось
        // зашивать второй раз — первой тёмной палитрой, так что при любой
        // другой теме меню оставалось чужим (владелец 24.09).
        _icon = new NotifyIcon
        {
            Icon = ThemedIcon() ?? OwnIcon(),
            Text = "NetZapret",
            Visible = true,
        };

        // Значок — в цветах эмблемы темы (21.10), и сменилась тема — сменился он.
        Themes.Changed += OnThemeChanged;

        // Любой кнопкой — меню: в нём и состояние, и путь к окну. Двойной
        // щелчок по-прежнему открывает окно сразу.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button is MouseButtons.Left or MouseButtons.Right)
                OpenMenu();
        };

        _icon.DoubleClick += (_, _) => Show();

        _icon.BalloonTipClicked += (_, _) =>
        {
            if (_link is { } link)
            {
                _link = null;
                OpenLink(link);
                return;
            }

            RestartFromNotice();
        };

        _icon.BalloonTipClosed += (_, _) =>
        {
            _offered = null;
            _link = null;
        };

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
    /// в папку лишнее. Значок вшивается при сборке (Assets\app.ico), так что
    /// новый виден только после build.cmd. Не нашёлся — стандартный значок
    /// приложений Windows: некрасиво, но работает.
    /// </remarks>
    /// <summary>
    /// Эмблема в цветах темы (владелец 21.10: «3 цвета для иконки» у каждой темы);
    /// <c>null</c> — не собралась, тогда вшитый в exe значок.
    /// </summary>
    /// <remarks>
    /// Из .ico со всеми размерами (<see cref="Emblem.Ico"/>) берётся размер
    /// трея этой системы — 20 при масштабе 125 %, — а не уменьшенный большой.
    /// </remarks>
    private static Icon? ThemedIcon()
    {
        try
        {
            var (top, band, bottom) = Emblem.Current();
            using var ico = new MemoryStream(Emblem.Ico(top, band, bottom));
            return new Icon(ico, SystemInformation.SmallIconSize);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void OnThemeChanged()
    {
        if (ThemedIcon() is not { } next)
            return;

        var was = _icon.Icon;
        _icon.Icon = next;
        was?.Dispose();
    }

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

    private string? _subscriptionNotice;

    private void Update()
    {
        var notice = SubscriptionRefreshNotice.Load();
        if (notice is not null && notice.Id != _subscriptionNotice
            && SupervisorState.Load(SupervisorState.DefaultPath) is { } live
            && live.IsSupervisorAlive() && live.SupervisorProcessId == notice.SupervisorProcessId)
        {
            _subscriptionNotice = notice.Id;
            // Заголовок уведомления Windows режет на 63 знаках — прежний текст
            // в 85 знаков обрывался на полуслове.
            RestartOffer.Show("Подписка перечитана: серверы в ней сменились");
        }

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

    /// <summary>
    /// Предлагает перезапуск уведомлением Windows: нажатие перезапускает движки.
    /// </summary>
    /// <remarks>
    /// Для того, что выучено в фоне (<see cref="RestartOffer"/>): адреса игры
    /// из «Наблюдения», адреса голоса Discord. Плашка в окне такого не доносила:
    /// окно в трее, человек в игре — плашку не видит никто, а в режиме
    /// <c>--tray</c> окна нет вовсе. Игрок в Rainbow Six (чат 14.10) перезапускал
    /// руками, не зная, что предложение было.
    /// </remarks>
    public void OfferRestart(string what)
    {
        if (!EngineControl.IsRunning)
            return;

        _offered = what;
        _link = null;

        // Заголовок уведомления Windows режет на 63 знаках.
        _icon.ShowBalloonTip(
            15_000,
            what.Length > 63 ? what[..62] + "…" : what,
            "Движки работают со старой настройкой. Нажмите, чтобы перезапустить: "
                + "соединения оборвутся на пару секунд.",
            ToolTipIcon.Info);
    }

    /// <summary>
    /// Уведомление Windows с делом, а не с перезапуском: нажатие открывает <paramref name="link"/>.
    /// </summary>
    /// <remarks>
    /// Для конца срока подписки (15.10): напоминание должно дойти и тогда, когда
    /// окно в трее, — как предложение перезапуска. Нажатие ведёт на страницу
    /// подписки, если поставщик её назвал: там и продлевают.
    /// </remarks>
    public void Notify(string title, string text, string? link) =>
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _offered = null;
            _link = link;

            _icon.ShowBalloonTip(
                15_000,
                title.Length > 63 ? title[..62] + "…" : title,
                text,
                ToolTipIcon.Info);
        });

    private static void OpenLink(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет браузера по умолчанию — та же ссылка есть в карточке подписки.
        }
    }

    private async void RestartFromNotice()
    {
        if (_offered is not { } what || _busy)
            return;

        // До ответа — снять: уведомление об итоге тоже нажимается, и второе
        // нажатие перезапустило бы ещё раз.
        _offered = null;
        _busy = true;
        Update();

        try
        {
            var outcome = await EngineControl.RestartAsync($"уведомление в трее: {what}", CancellationToken.None);

            _icon.ShowBalloonTip(
                8_000,
                outcome.Ok ? "Движки перезапущены" : "Перезапустить не вышло",
                outcome.Ok
                    ? "Уже открытые соединения ходят по-старому: игру или вкладку перезайдите."
                    : outcome.Message,
                outcome.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
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
    /// <summary>
    /// Выход тем же путём, что в меню трея, — для меню у «Остановить» на «Главной».
    /// </summary>
    /// <remarks>
    /// Значка может не быть (окно запущено без трея) — тогда выход тот же,
    /// только без строки «Останавливаю…» в меню трея.
    /// </remarks>
    public static async void Exit(bool stopEngines)
    {
        if (Current is { } tray)
        {
            tray.Quit(stopEngines);
            return;
        }

        if (stopEngines)
        {
            try
            {
                await EngineControl.StopAsync("меню у «Остановить»: выход с остановкой", CancellationToken.None);
            }
            catch (Exception)
            {
                // Как и в трее: выходим всё равно.
            }
        }

        App.Exiting = true;
        Application.Current.Shutdown();
    }

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
        if (Current == this)
            Current = null;

        Themes.Changed -= OnThemeChanged;
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
