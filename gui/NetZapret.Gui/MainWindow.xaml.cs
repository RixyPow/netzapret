using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Updates;
using NetZapret.Gui.Views;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Стекло карточек берёт кусок размытого фона по месту в этой сетке:
        // на ней лежит сам фон, и координаты у них общие.
        Glass.Root = Root;

        VersionLabel.Text = "версия " + Version();
        ShowOnboardingIfNeeded();

        SourceInitialized += (_, _) =>
        {
            DarkenTitleBar();
            AllowDropFromExplorer();
        };

        _toastTimer.Tick += (_, _) => HideToast();

        UpdateNotice.Changed += () => Dispatcher.InvokeAsync(ShowUpdateBadge);
        _ = CheckForUpdateAsync();
        _ = WarmRoutesAsync();
        _ = RefreshPinsLoopAsync();
    }

    /// <summary>
    /// Меняет умершие адреса пинов на живые (PinRefresh) — раз на запуск движков.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Просьба владельца 28.09: посредники XBOX DNS и Comss меняют адреса,
    /// и в тот день умерший 87.228.47.204 положил Claude и ChatGPT, пока новый
    /// адрес не нашёлся руками.
    /// </para>
    /// <para>
    /// Раз после каждого запуска движков и раз в сутки, если их не выключают.
    /// Адреса посредников живут неделями и месяцами (владелец), и проверять
    /// чаще незачем — так же делает Zapret GUI 21.1.6.26, обновляя адреса
    /// при запуске. Первая версия проверяла раз в три часа.
    /// </para>
    /// <para>
    /// Только при поднятых движках: часть посредников отвечает напрямую лишь
    /// с десинком. Через минуту после подъёма — пусть автоподбор и десинк
    /// улягутся. Раз в две минуты окно лишь читает файл состояния надзора,
    /// в сеть не ходит. Окно живёт в трее, так что работает и скрытым.
    /// </para>
    /// </remarks>
    private async Task RefreshPinsLoopAsync()
    {
        DateTimeOffset? checkedRun = null;
        DateTime lastCheck = DateTime.MinValue;

        while (true)
        {
            await Task.Delay(TimeSpan.FromMinutes(2));

            try
            {
                var state = SupervisorState.Load(SupervisorState.DefaultPath);

                if (state is null || !state.IsSupervisorAlive())
                    continue;

                bool newRun = checkedRun != state.StartedAt
                    && DateTimeOffset.Now - state.StartedAt > TimeSpan.FromMinutes(1);

                if (newRun || DateTime.Now - lastCheck > TimeSpan.FromDays(1))
                {
                    checkedRun = state.StartedAt;
                    lastCheck = DateTime.Now;

                    var result = await Task.Run(() => NetZapret.Proxy.PinRefresh.RunAsync(CancellationToken.None));

                    if (result.Changes.Count > 0)
                    {
                        Journal.Write("пин", "адрес умер, заменён живым: " + string.Join("; ", result.Changes)
                            + (result.Backup is null ? string.Empty : $"; копия hosts: {result.Backup}"));

                        Notify("Пины обновлены",
                            "Посредник сменил адрес, прежний не отвечал — прибито к новому: "
                            + string.Join("; ", result.Changes.Take(2)) + ".");
                    }

                    if (result.Dead.Count > 0)
                        Journal.Write("пин", $"мёртвые без живой замены: {string.Join(", ", result.Dead.Take(8))}"
                            + (result.Dead.Count > 8 ? $" и ещё {result.Dead.Count - 8}" : string.Empty));

                    if (result.Error is { } error)
                        Journal.Write("пин", "обновление пинов: " + error);
                }
            }
            catch (Exception ex)
            {
                Journal.Write("пин", "обновление пинов не удалось: " + ex.GetBaseException().Message);
            }
        }
    }

    /// <summary>Уведомление без действия: сказать о том, что уже сделано.</summary>
    public void Notify(string title, string body)
    {
        ToastTitle.Text = title;
        ToastBody.Text = body;

        ToastAct.Visibility = Visibility.Collapsed;
        ToastLater.Content = "Понятно";

        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Visibility = Visibility.Visible;
        Motion.Arrive(Toast, dy: 18);

        _toastTimer.Stop();
        _toastTimer.Interval = ToastLife;
        _toastTimer.Start();
    }

    /// <summary>
    /// Один раз в фоне проходит то, что «Маршруты» читают при открытии.
    /// </summary>
    /// <remarks>
    /// Замер 28.09: первое открытие «Маршрутов» — ~380 мс на главном потоке,
    /// из них 231 мс строки по каталогу (холодное чтение сотен списков
    /// и первая компиляция кода); второе — ~95 мс. Прогрев делает первое
    /// открытие вторым. Через три секунды после запуска, чтобы не спорить
    /// с самим запуском окна; неудача молчит — раздел просто прочитает сам.
    /// </remarks>
    private static async Task WarmRoutesAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));

            await Task.Run(() =>
            {
                var settings = AppSettings.Load(AppSettings.DefaultPath);
                var zapretRoot = NetZapret.Zapret.ZapretPaths.Discover()?.Root;
                var userRules = NetZapret.Core.Rules.UserRulesFile.Load();

                var engine = NetZapret.Core.Rules.RuleSetLoader.LoadLayered(
                    settings.RulesPath, NetZapret.Core.Rules.UserRulesFile.DefaultPath, settings.Mode);

                NetZapret.Zapret.RuleSetExpander.Expand(engine.RuleSet, zapretRoot);

                foreach (var service in NetZapret.Core.Services.ServiceCatalog.All)
                    NetZapret.Zapret.ServiceRouting.Describe(service, engine, zapretRoot, userRules);
            });
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Тихо спрашивает GitHub о новой версии — если это разрешено.
    /// </summary>
    /// <remarks>
    /// Из окна, а не из супервизора: тот запускается автозапуском без окна,
    /// и сказать о находке ему некому. Неудача молчит — нет сети сейчас,
    /// спросим при следующем запуске.
    /// </remarks>
    private static async Task CheckForUpdateAsync()
    {
        try
        {
            await UpdateNotice.CheckAsync(AppSettings.Load(AppSettings.DefaultPath), CancellationToken.None);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Точка у «Главной», пока новая версия не поставлена: обновление живёт там.</summary>
    private void ShowUpdateBadge() =>
        RailBadge.SetShown(RailStatus, UpdateNotice.Available is not null);

    /// <summary>
    /// Подменяет «Главную» мастером первого запуска, пока он не пройден.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Разметка ставит в <c>Section</c> <see cref="StatusView"/> статически —
    /// это верно для всех, кто уже настроился. Здесь она перекрывается ровно
    /// для тех, у кого <see cref="AppSettings.OnboardingDone"/> ещё не стоит:
    /// свежая установка либо файл настроек, который ещё не читали.
    /// </para>
    /// <para>
    /// Заменяется в конструкторе, а не в обработчике «Главной»: пункт «Главная»
    /// уже отмечен выбранным в разметке, и его переключатель за время
    /// разбора XAML событий не поднимает — <c>OnSection</c> в этот момент
    /// видит <c>Section</c> ещё не созданным и ничего не делает.
    /// </para>
    /// <para>
    /// Уходит навсегда, стоит уйти на любой другой раздел и вернуться: клик
    /// по «Главной» снова показывает обычную <see cref="StatusView"/>.
    /// Мастер — это то, что видно один раз при входе, а не отдельный
    /// постоянный режим «Главной». Открыть его заново можно из «Ещё».
    /// </para>
    /// </remarks>
    private void ShowOnboardingIfNeeded()
    {
        try
        {
            if (AppSettings.Load(AppSettings.DefaultPath).OnboardingDone)
                return;
        }
        catch (Exception)
        {
            // Настройки не читаются — не повод не показать мастер. Он же
            // и заведёт файл настроек первым сохранением.
        }

        ShowOnboarding();
    }

    private void ShowOnboarding()
    {
        var onboarding = new OnboardingView();
        onboarding.Completed += (_, _) =>
        {
            var status = new StatusView();
            Section.Content = status;
            Motion.Page(status);
        };

        Section.Content = onboarding;
    }

    /// <summary>
    /// Открывает мастер первого запуска заново — по кнопке из «Ещё».
    /// </summary>
    /// <remarks>
    /// Отмечает «Главную» выбранной в меню прежде, чем подменить содержимое
    /// мастером: без этого пункт меню продолжал бы показывать «Ещё»
    /// выбранным, хотя видно уже другое, — тот же разнобой, что был бы
    /// у любого раздела, подменённого в обход <c>OnSection</c>.
    /// </remarks>
    public void RestartOnboarding()
    {
        RailStatus.IsChecked = true;
        ShowOnboarding();
    }

    /// <summary>
    /// Крестик прячет окно в трей, а не закрывает программу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Движки живут отдельным процессом и переживают закрытие окна: закрыв
    /// его по-настоящему, человек остался бы с работающим обходом и без
    /// единого признака этого на экране. Значок в трее и есть такой признак.
    /// </para>
    /// <para>
    /// Сворачивание при этом остаётся обычным — окно уходит на панель задач,
    /// а не в трей. Прятать его и туда, и туда значит отобрать привычное
    /// поведение ради второго способа сделать то же самое.
    /// </para>
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.Exiting)
        {
            e.Cancel = true;
            Hide();

            return;
        }

        base.OnClosing(e);
    }

    private const int WmCopyGlobalData = 0x0049;
    private const int WmCopyData = 0x004A;
    private const int WmDropFiles = 0x0233;

    /// <summary>Пропустить сообщение сквозь защиту уровней.</summary>
    private const int MessageAllow = 1;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeWindowMessageFilterEx(
        nint window, int message, int action, nint info);

    /// <summary>
    /// Разрешает ронять файлы из проводника в это окно.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Без этого перетаскивание не работает вовсе, и не по нашей вине: окно
    /// поднято администратором, проводник — нет, а Windows не пропускает
    /// сообщения снизу вверх между уровнями. Со стороны это выглядит так,
    /// будто файл просто не берётся: ни отказа, ни объяснения.
    /// </para>
    /// <para>
    /// Снимается фильтр ровно на три сообщения переноса, а не на все:
    /// открывать окно с полными правами всему подряд ради удобства
    /// не стоит. Отказ не проверяется — перетаскивание удобство, а не
    /// единственный путь: рядом есть кнопка «Открыть папку».
    /// </para>
    /// </remarks>
    private void AllowDropFromExplorer()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;

            foreach (int message in new[] { WmDropFiles, WmCopyData, WmCopyGlobalData })
                ChangeWindowMessageFilterEx(handle, message, MessageAllow, nint.Zero);
        }
        catch (Exception)
        {
            // На системах, где вызова нет, останется кнопка «Открыть папку».
        }
    }

    /// <summary>
    /// Показывает выбранный раздел.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Каждый раз новый объект, а не сохранённый. Разделы читают состояние
    /// системы — движки, подписку, hosts, — и оно меняется, пока окно открыто.
    /// Возвращать сохранённый вид значило бы показывать снимок прошлого
    /// захода, не сказав об этом.
    /// </para>
    /// <para>
    /// Цена известна: замер серверов, начатый в одном разделе, прервётся при
    /// уходе в другой. Это честнее, чем оставлять его гоняться в невидимом
    /// разделе, тратя трафик подписки.
    /// </para>
    /// </remarks>
    private void OnSection(object sender, RoutedEventArgs e)
    {
        // Отрабатывает и при разборе разметки, когда Section ещё не создан:
        // IsChecked="True" у первого пункта поднимает событие раньше времени.
        if (Section is null || sender is not RadioButton { Tag: string name })
            return;

        // Для сторожа подвисаний: к разделу, который открывают, и привязывается
        // задержка — создание раздела и есть частая её причина.
        UiStallWatch.Section = (sender as RadioButton)?.Content as string ?? name;

        Section.Content = name switch
        {
            "vpn" => new VpnView(),
            "desync" => new DesyncView(),
            "routes" => new RoutesView(),
            "check" => new CheckView(),
            "dns" => new DnsView(),
            "hosts" => new HostsView(),
            "watch" => new WatchView(),
            "log" => new LogView(),
            "doctor" => new DoctorView(),
            "look" => new AppearanceView(),
            "more" => new MoreView(),
            _ => new StatusView(),
        };

        // «Маршруты» — без анимации (владелец, 28.09): список из сотни строк,
        // и его появление по частям только мешает искать.
        if (name != "routes" && Section.Content is FrameworkElement page)
            Motion.Page(page);
    }

    /// <summary>Сколько уведомление висит, прежде чем уйти само.</summary>
    private static readonly TimeSpan ToastLife = TimeSpan.FromSeconds(14);

    private readonly DispatcherTimer _toastTimer = new();

    /// <summary>
    /// Предлагает перезапустить движки — если им есть что перезапускать.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Почти всё, что меняется в окне, вступает в силу перезапуском: маршрут,
    /// пресет, резолвер, сервер. Сказать об этом строкой в глубине раздела
    /// значит не сказать вовсе — её прочитают после того, как решат, что
    /// программа не работает.
    /// </para>
    /// <para>
    /// При остановленных движках уведомление не показывается: перезапускать
    /// нечего, и предложение сделать это было бы предложением без смысла.
    /// </para>
    /// <para>
    /// Само не перезапускает никогда. Движки несут весь трафик машины, и решать
    /// за человека, когда его оборвать, программа не вправе.
    /// </para>
    /// </remarks>
    public void OfferRestart(string what)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
            return;

        ToastTitle.Text = what;
        ToastBody.Text = "Движки работают со старой настройкой. Перезапуск оборвёт соединения "
            + "на пару секунд — всё, что качается, придётся начать заново.";

        ToastAct.IsEnabled = true;
        ToastAct.Visibility = Visibility.Visible;
        ToastLater.Content = "Позже";

        // Угасание от прошлого «Позже» могло ещё идти — снимаем, иначе оно
        // спрятало бы только что показанное.
        Toast.BeginAnimation(OpacityProperty, null);
        Toast.Visibility = Visibility.Visible;
        Motion.Arrive(Toast, dy: 18);

        _toastTimer.Stop();
        _toastTimer.Interval = ToastLife;
        _toastTimer.Start();
    }

    private void OnToastLater(object sender, RoutedEventArgs e) => HideToast();

    private void HideToast()
    {
        _toastTimer.Stop();
        Motion.Leave(Toast);
    }

    /// <summary>
    /// Останавливает движки и поднимает их заново.
    /// </summary>
    /// <remarks>
    /// Пауза между остановкой и запуском не для красоты: супервизор
    /// освобождает TUN и снимает фильтр не мгновенно, и запуск, начатый
    /// сразу, наткнулся бы на ещё живой адаптер.
    /// </remarks>
    private async void OnToastRestart(object sender, RoutedEventArgs e)
    {
        ToastAct.IsEnabled = false;
        ToastTitle.Text = "Перезапускаю движки…";
        _toastTimer.Stop();

        var outcome = await EngineControl.RestartAsync("«Перезапустить» во всплывающем сообщении", CancellationToken.None);

        if (!outcome.Ok)
        {
            ToastTitle.Text = "Перезапустить не вышло";
            ToastBody.Text = outcome.Message;
            ToastAct.IsEnabled = true;

            return;
        }

        // Перезапуск применил настройку, но не к уже открытой вкладке:
        // браузер держит прежние соединения (HTTP/2, QUIC) и прежние ответы
        // DNS, и страница, открытая до перезапуска, ходит старой дорогой.
        // Человек смотрит на неё и решает, что смена маршрута не сработала.
        // Совет из вики Zapret GUI, 27.09.
        ToastTitle.Text = "Движки перезапущены";
        ToastBody.Text = "Уже открытые вкладки ходят по старым соединениям. Чтобы увидеть "
            + "изменение, обновите страницу через Ctrl+F5 или откройте её в окне инкогнито.";
        ToastAct.Visibility = Visibility.Collapsed;
        ToastLater.Content = "Понятно";

        _toastTimer.Interval = ToastLife;
        _toastTimer.Start();
    }

    internal static string Version()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
            ?? "—";

        return Build() is { } build ? $"{version} ({build})" : version;
    }

    /// <summary>
    /// Номер сборки — только у собранного своими руками.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Нужен затем, что версия при разработке не меняется днями, а собирают
    /// по десять раз за вечер: глядя на «0.5.8», не отличить свежую сборку
    /// от той, что осталась с прошлого захода. Отсюда и вопросы вроде
    /// «я пересобрал, а изменилось ли что-нибудь».
    /// </para>
    /// <para>
    /// Файл пишет <c>build.cmd</c> и только он. В поставку номер попасть
    /// не может по построению: <c>pack.cmd</c> собирает архив из свежей
    /// публикации, где этого файла нет, — то есть скрывать его отдельно
    /// не требуется, достаточно не создавать.
    /// </para>
    /// </remarks>
    private static string? Build()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "build-number.txt");

            if (!File.Exists(path))
                return null;

            var number = File.ReadAllText(path).Trim();

            return number.Length is > 0 and < 12 ? number : null;
        }
        catch (Exception)
        {
            // Номер сборки — удобство разработчика. Его отсутствие
            // не повод не показать окно.
            return null;
        }
    }

    /// <summary>Идентификатор атрибута тёмного оформления рамки.</summary>
    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    /// <summary>
    /// Просит систему нарисовать заголовок окна тёмным.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Само окно тёмное, а рамку и заголовок рисует Windows, и по умолчанию
    /// они светлые. Белая полоса над тёмным содержимым — первое, что видно
    /// при запуске, и выглядит она поломкой, а не задумкой.
    /// </para>
    /// <para>
    /// Отказ не проверяется намеренно: на сборках Windows старше 2020 года
    /// атрибута нет, вызов вернёт ошибку, и заголовок останется светлым.
    /// Это некрасиво, но работать не мешает, а падать из-за оформления
    /// не стоит ничего.
    /// </para>
    /// </remarks>
    private void DarkenTitleBar()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int enabled = 1;

            DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref enabled, sizeof(int));
        }
        catch (Exception)
        {
            // Оформление не стоит того, чтобы из-за него не открылось окно.
        }
    }
}
