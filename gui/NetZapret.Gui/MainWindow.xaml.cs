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
        onboarding.Completed += (_, _) => Section.Content = new StatusView();

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
        Toast.Visibility = Visibility.Visible;

        _toastTimer.Stop();
        _toastTimer.Interval = ToastLife;
        _toastTimer.Start();
    }

    private void OnToastLater(object sender, RoutedEventArgs e) => HideToast();

    private void HideToast()
    {
        _toastTimer.Stop();
        Toast.Visibility = Visibility.Collapsed;
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

        var outcome = await EngineControl.RestartAsync(CancellationToken.None);

        if (!outcome.Ok)
        {
            ToastTitle.Text = "Перезапустить не вышло";
            ToastBody.Text = outcome.Message;
            ToastAct.IsEnabled = true;

            return;
        }

        HideToast();
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
