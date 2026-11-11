using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Themes;
using NetZapret.Subscriptions;

namespace NetZapret.Gui.Views;

/// <summary>
/// Сценарий первого запуска: знакомство, оформление, режим работы, подписка.
/// </summary>
/// <remarks>
/// <para>
/// До этой правки программа не вела нового человека вовсе — он попадал
/// на «Главную» с кнопкой запуска и остальным «найдите сами». Разбор
/// был в брифе для дизайнеров (удалён 12.10): человек не знает, что для VPN
/// нужна своя подписка, что после смены маршрута нужен перезапуск и что
/// проверка рецептов требует остановленных движков. Мастер закрывает ровно
/// это — называет вслух то, что раньше узнавали методом тыка.
/// </para>
/// <para>
/// 28.09 по просьбе владельца мастер вырос с трёх шагов до пяти: первым —
/// живая схема того, что программа делает (точки бегут двумя дорогами:
/// сквозь ТСПУ десинком и через туннель), вторым — выбор темы, последним —
/// телеграм-канал. Схема рисуется кодом, а не картинкой: она берёт цвета
/// темы и перекрашивается вместе с ней.
/// </para>
/// <para>
/// 05.10 (владелец) — шестой шаг, четвёртым: режим работы NZ с объяснением
/// каждого. После подписки — «Туннелю» нужен выход, — до пробного запуска,
/// который поднимает ровно выбранное.
/// </para>
/// <para>
/// 07.10 (владелец: «шаги 5 и 6 в мастере стоит объединить») — снова пять:
/// пробный запуск и результат стали одним шагом «Запуск и проверка».
/// </para>
/// <para>
/// 21.10 (владелец: «3 вкладку есть ли впн перенеси на 4 вкладку, а 5 вкладку
/// удали») — четыре: режим третьим, подписка последней, «Запуск и проверка»
/// убрана вместе с карточкой телеграм-канала. Движки поднимаются на «Главной».
/// Режим тогда выбирали до подписки, и «Туннель» без неё был доступен.
/// </para>
/// <para>
/// 03.11 (решение владельца) — три: подписка и режим на одном шаге,
/// подписка сверху. Без выхода «Туннель» и «Гибрид» погашены и оживают,
/// как только подписка загрузилась.
/// </para>
/// <para>
/// 08.11 (макеты владельца) — те же три шага, но на всё окно (меню прячет
/// MainWindow), крупным шрифтом и для новичка: «Добро пожаловать» с кнопкой
/// «Начать» вместо живой схемы путей, крупные плитки тем, режимы одним
/// сравнением — Zapret, VPN, оба вместе — и заметный прокси Telegram. Выбора
/// DNS в мастере больше нет: стоит «авто», сменить — на вкладке DNS.
/// «Пропустить» — только на первом шаге и закрывает мастер целиком.
/// </para>
/// <para>
/// Шага «что у вас не работает» здесь нет — он был первым в исходном
/// варианте и просил выбрать сервис из полусотни, ничего не решая.
/// </para>
/// <para>
/// Показывается вместо «Главной» ровно один раз, пока в настройках не стоит
/// <see cref="AppSettings.OnboardingDone"/>. Повторно его можно открыть из «Ещё».
/// </para>
/// </remarks>
public partial class OnboardingView : UserControl
{
    private const int LastStep = 3;

    /// <summary>Мастер закрыт — завершением или пропуском.</summary>
    public event EventHandler? Completed;

    private int _step = 1;

    public OnboardingView()
    {
        InitializeComponent();

        for (int i = 0; i < LastStep; i++)
            Progress.Children.Add(new Ellipse { Width = 14, Height = 14, StrokeThickness = 2, Margin = new Thickness(7, 0, 7, 0) });

        Loaded += (_, _) => Show(_step);
    }

    private StackPanel Panel(int step) => step switch
    {
        1 => Step1,
        2 => Step2,
        _ => StepMode,
    };

    /// <summary>Кнопки шага — внизу, вне прокрутки, чтобы не уходили под край.</summary>
    private StackPanel Buttons(int step) => step switch
    {
        1 => Step1Buttons,
        2 => Step2Buttons,
        _ => StepModeButtons,
    };

    private void Show(int step)
    {
        _step = step;

        for (int i = 1; i <= LastStep; i++)
        {
            Panel(i).Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
            Buttons(i).Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;
        }

        // Текущий — залит акцентом, прочие — пустым кольцом.
        for (int i = 0; i < LastStep; i++)
        {
            var dot = (Ellipse)Progress.Children[i];
            bool now = i == step - 1;

            dot.SetResourceReference(Shape.StrokeProperty, now ? "Accent" : "Muted");

            if (now)
                dot.SetResourceReference(Shape.FillProperty, "Accent");
            else
                dot.Fill = Brushes.Transparent;
        }

        StepLabel.Text = $"Шаг {step} из {LastStep}";

        Arrive(Panel(step));

        if (step == 2)
            ShowThemes();

        if (step == 3)
            ShowModeStep();
    }

    /// <summary>
    /// «Пропустить» на первом шаге — мастер целиком: настройки остаются как есть.
    /// </summary>
    /// <remarks>
    /// До 08.11 «Пропустить» стояла в шапке каждого шага и пропускала только
    /// текущий: закрывавшая весь мастер кнопка, нажатая случайно, теряла
    /// следующие шаги. Теперь она одна, на «Добро пожаловать», рядом с
    /// крупной «Начать» — случайно её не нажмут, а кто не хочет мастер,
    /// уходит сразу. Повторить его можно из «Ещё».
    /// </remarks>
    private void OnSkipAll(object sender, RoutedEventArgs e)
    {
        Journal.Write("окно", "мастер первого запуска пропущен с первого шага");
        Finish();
    }

    private void OnBack(object sender, RoutedEventArgs e) => Show(Math.Max(1, _step - 1));

    // --- Движение ------------------------------------------------------------

    /// <summary>Шаг въезжает справа и проявляется.</summary>
    private static void Arrive(FrameworkElement panel) => Motion.Arrive(panel, dx: 18, dy: 0, ms: 320);

    // --- Шаг 1: знакомство ---------------------------------------------------

    private void OnIntroNext(object sender, RoutedEventArgs e) => Show(2);

    // --- Шаг 2: оформление ---------------------------------------------------

    private void ShowThemes()
    {
        try
        {
            var current = Themes.Current;

            ThemeList.ItemsSource = ThemeLoader.LoadAll()
                .Select(load => ThemeTile.From(load, load.Id == current, key => (Brush)FindResource(key)))
                .ToList();
        }
        catch (Exception ex)
        {
            ThemeStatus.Text = "Темы не читаются: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Та же смена темы, что в «Оформлении»: сразу и с записью в настройки.</summary>
    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        try
        {
            var result = Themes.Apply(id);

            if (result.Ok)
            {
                (AppSettings.Load(AppSettings.DefaultPath) with { Theme = id }).Save(AppSettings.DefaultPath);
                ThemeStatus.Text = string.Empty;
            }
            else
            {
                ThemeStatus.Text = $"Тема «{id}» не применена: {string.Join("; ", result.Problems.Take(3))}.";
            }

            ShowThemes();
        }
        catch (Exception ex)
        {
            ThemeStatus.Text = "Не удалось сменить тему: " + ex.GetBaseException().Message;
        }
    }

    private void OnThemeNext(object sender, RoutedEventArgs e) => Show(3);

    // --- Шаг 3: режим работы и подписка ---------------------------------------

    /// <summary>
    /// Подписка сверху, под ней три режима; выбран тот, что стоит сейчас.
    /// </summary>
    /// <remarks>
    /// Выхода нет (ни подписки, ни WARP) — «Туннель» и «Гибрид» погашены:
    /// <see cref="WorkModes.CanChoose"/> их всё равно не записал бы, и выбор
    /// молча стал бы «Десинком». Загрузилась подписка — карточки оживают
    /// (<see cref="ShowLocks"/>).
    /// </remarks>
    private void ShowModeStep()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        PickDesyncName.Text = WorkModes.Name(WorkMode.Desync);
        PickTunnelName.Text = WorkModes.Name(WorkMode.Tunnel);
        PickRouteName.Text = WorkModes.Name(WorkMode.Route);

        _hasExit = settings.HasTunnelExit;
        VpnStatus.Text = string.Empty;

        var current = WorkModes.Of(settings.Engines) is { } mode && WorkModes.CanChoose(settings, mode)
            ? mode
            : WorkMode.Desync;

        PickDesync.IsChecked = current == WorkMode.Desync;
        PickTunnel.IsChecked = current == WorkMode.Tunnel;
        PickRoute.IsChecked = current == WorkMode.Route;

        // Прокси нет в поставке (сборка без tools\…\tg-ws-proxy.exe) — включать
        // нечего; уже включённый можно выключить.
        TgPick.IsChecked = settings.TelegramProxy;
        TgPick.IsEnabled = settings.TelegramProxy || System.IO.File.Exists(TgWsProxy.Executable());

        TgPickState.Text = TgPick.IsEnabled
            ? "Telegram заработает без VPN"
            : "Прокси нет в этой сборке программы";

        ShowLocks();
    }

    private WorkMode? PickedMode() =>
        Enum.TryParse<WorkMode>(new[] { PickDesync, PickTunnel, PickRoute }
            .FirstOrDefault(p => p.IsChecked == true)?.Tag as string, out var mode)
            ? mode
            : null;

    /// <summary>Выход есть — подписка или WARP; после «Загрузить» — тоже.</summary>
    private bool _hasExit;

    /// <summary>
    /// Без выхода «Туннель» и «Гибрид» погашены, а выбранный из них уступает
    /// «Десинку»: записать его всё равно нельзя.
    /// </summary>
    private void ShowLocks()
    {
        PickTunnel.IsEnabled = _hasExit;
        PickRoute.IsEnabled = _hasExit;
        ModesLocked.Visibility = _hasExit ? Visibility.Collapsed : Visibility.Visible;

        if (!_hasExit && (PickTunnel.IsChecked == true || PickRoute.IsChecked == true))
            PickDesync.IsChecked = true;
    }

    /// <summary>
    /// Записывает режим и прокси; вставленная и ещё не загруженная
    /// ссылка грузится тут же, и мастер закрывается, только если она подключилась.
    /// </summary>
    /// <remarks>
    /// Не загрузилась — человек остаётся на шаге с причиной под полем:
    /// уйти молча значило бы потерять вставленную подписку. Без ссылки — сразу
    /// на «Главную», режим уже записан.
    /// </remarks>
    private async void OnModeDone(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings;

            if (PickedMode() is { } mode && WorkModes.CanChoose(next, mode))
                next = WorkModes.Choose(next, mode);

            if (TgPick.IsChecked is bool telegram && telegram != next.TelegramProxy)
                next = TgWsProxy.Switch(next, telegram);

            if (next != settings)
                next.Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не записалось — мастер закрывается со старым режимом и прокси;
            // они видны на «Главной» и на вкладке «TG Proxy».
        }

        if (SubLink.Password.Trim().Length > 0)
        {
            ModeDone.IsEnabled = false;

            try
            {
                if (!await LoadSubscriptionAsync())
                    return;
            }
            finally
            {
                ModeDone.IsEnabled = true;
            }
        }

        Finish();
    }

    /// <summary>
    /// «Загрузить» у поля: подписка подключается сразу, мастер остаётся —
    /// число серверов видно, и «Туннель» с «Гибридом» оживают.
    /// </summary>
    private async void OnAddSubscription(object sender, RoutedEventArgs e)
    {
        if (await LoadSubscriptionAsync())
        {
            _hasExit = true;
            ShowLocks();
        }
    }

    /// <summary>
    /// Проверяет ссылку и подключает подписку — тот же путь, что в разделе VPN.
    /// </summary>
    /// <remarks>
    /// Ссылка на подписку равносильна паролю: поле — <see cref="PasswordBox"/>,
    /// значение нигде не печатается и не остаётся в журнале, а после
    /// использования очищается.
    /// </remarks>
    private async Task<bool> LoadSubscriptionAsync()
    {
        var raw = SubLink.Password.Trim();

        if (raw.Length == 0)
        {
            VpnStatus.Text = "Ссылку никто не вставил. Нет подписки — оставьте поле пустым и нажмите «Готово».";
            return false;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            VpnStatus.Text = "Это не похоже на ссылку.";
            return false;
        }

        parsed = SubscriptionClient.Unwrap(parsed);

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            VpnStatus.Text = "Это не похоже на ссылку подписки: нужна http, https "
                + "либо обёртка happ, clash или sn.";

            return false;
        }

        VpnLoad.IsEnabled = false;
        VpnStatus.Text = "Загружаю список серверов…";

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(parsed, CancellationToken.None);

            var book = SubscriptionBook.Load();

            var entry = new SubscriptionEntry { Name = book.FreeName(), Url = parsed.ToString() };
            book.Entries.Add(entry);
            book.Save();

            SubscriptionBook.MakeActive(entry);

            SubLink.Clear();

            int usable = info.Servers.Count(s => s.IsUsableOutbound);
            VpnStatus.Text = $"Подписка подключена: серверов {usable}.";
            return true;
        }
        catch (Exception ex)
        {
            VpnStatus.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
            return false;
        }
        finally
        {
            VpnLoad.IsEnabled = true;
        }
    }

    // --- Завершение -----------------------------------------------------------

    /// <summary>
    /// Отмечает мастер закрытым и уступает место «Главной».
    /// </summary>
    /// <remarks>
    /// Ставится и при пропуске: «пропустить» тоже решение человека,
    /// и показывать мастер второй раз значило бы не уважать его.
    /// </remarks>
    private void Finish()
    {
        MarkDone();
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Мастер покинут уходом в другой раздел — это тоже его закрытие.
    /// </summary>
    /// <remarks>
    /// До 07.10 отметку ставили только «Готово» и «Пропустить», и ушедший
    /// в другой раздел встречал мастер при каждом следующем запуске, хотя
    /// с экрана он уходил «навсегда». Открыть его снова можно из «Ещё».
    /// </remarks>
    public void Leave()
    {
        if (MarkDone())
            Journal.Write("окно", "мастер первого запуска закрыт уходом в другой раздел");
    }

    /// <summary>Ставит отметку «пройден»; <c>true</c> — её не было и она записана.</summary>
    private static bool MarkDone()
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (settings.OnboardingDone)
                return false;

            (settings with { OnboardingDone = true }).Save(AppSettings.DefaultPath);

            // Мастер закрыт впервые — значит, человек новый: автозапуск у новых
            // включён по умолчанию (12.11). В фоне — schtasks идёт десятки мс,
            // а выключить его можно в «Ещё».
            _ = Task.Run(() => Journal.Write("окно", Autostart.TurnOn() is { } failure
                ? "автозапуск по умолчанию не заведён: " + failure
                : "автозапуск по умолчанию: задача планировщика есть"));

            return true;
        }
        catch (Exception)
        {
            // Не записалось — мастер покажется снова при следующем запуске.
            return false;
        }
    }
}
