using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Updates;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Строка про один движок.</summary>
/// <param name="Word">Отметка в карточке одним-двумя словами (EngineHealth.Word).</param>
/// <param name="Detail">Строка под ней: причина надзора, когда движок не в порядке.</param>
public sealed record EngineRow(string Name, string Word, string Detail, Brush Color, int? ProcessId = null);

/// <summary>Предупреждение, которое стоит прочитать до запуска.</summary>
public sealed record WarningRow(string Title, string Body);

/// <summary>
/// Состояние настройки и движков, плюс запуск и остановка.
/// </summary>
/// <remarks>
/// <para>
/// Читает то же, что читает консоль, и теми же библиотеками:
/// <see cref="AppSettings"/>, <see cref="SupervisorState"/>,
/// <see cref="HostsEditor"/>, <see cref="SecuritySoftware"/>. Ни одна строка
/// в <c>src\</c> ради этого не тронута.
/// </para>
/// <para>
/// Запуск и остановка идут отдельным процессом — этой же программой
/// с ключом супервизора. Это не лень:
/// супервизор должен пережить закрытие окна, а значит быть отдельным
/// процессом. Консоль поступает ровно так же, запуская саму себя с ключом
/// <c>start</c>, — и раз путь один, у окна и меню не разойдётся поведение.
/// </para>
/// </remarks>
public partial class StatusView : UserControl
{
    /// <summary>Обычный период опроса.</summary>
    private static readonly TimeSpan CalmInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Период опроса во время запуска: подпись фазы и счётчик секунд должны
    /// успевать за движками, иначе окно выглядит замершим ровно тогда, когда
    /// на него и смотрят.
    /// </summary>
    private static readonly TimeSpan StartingInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Сколько показывать запуск, прежде чем показать состояние как есть.
    /// </summary>
    /// <remarks>
    /// С запасом больше суммы таймаутов готовности обоих движков и первых
    /// перезапусков: объявить «не поднялось» раньше этого срока значило бы
    /// соврать про ещё идущую работу.
    /// </remarks>
    private static readonly TimeSpan StartupPatience = TimeSpan.FromSeconds(90);

    private readonly DispatcherTimer _refresh = new() { Interval = CalmInterval };

    private DateTimeOffset? _startingSince;

    private void OnTelegram(object sender, RoutedEventArgs e) => OpenLink(About.Telegram);

    private void OnGitHub(object sender, RoutedEventArgs e) => OpenLink(About.Repository);

    /// <summary>Описание — README на GitHub: он и есть документация, другой нет.</summary>
    private void OnDocs(object sender, RoutedEventArgs e) => OpenLink(About.Repository + "#readme");

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет браузера по умолчанию — те же ссылки есть в «Ещё», в карточке «О программе».
        }
    }

    private void OnArtSize(object sender, SizeChangedEventArgs e) => PlaceArt();

    /// <summary>Уже этой колонки персонажа в ней не ставим — картинка ляжет по центру страницы.</summary>
    private const double ArtColumnMin = 220;

    /// <summary>Затемнение над персонажем: текста там нет, читаемость беречь незачем.</summary>
    private const double ArtDim = 0.12;

    /// <summary>
    /// Кладёт картинку темы так, чтобы её середина стояла посередине правой
    /// колонки, и затемняет её под карточками так же, как тема.
    /// </summary>
    /// <remarks>
    /// <para>
    /// По высоте страницы, с сохранением пропорций: персонаж в картинках
    /// стоит по центру, и при вписывании по высоте он целиком в колонке.
    /// Слева картинка может не доставать до края — там сплошная подложка.
    /// Край картинки растворяется в ней (маска прозрачности), и растворение
    /// кончается до колонки с артом. Прежде я рассчитывал, что края у артов
    /// тёмные и шва под затемнением не видно: у «Слойки» так, а у «Trident»
    /// светлая картина обрывалась вертикальной чертой посреди карточек
    /// (владелец, 30.09: «темы обрываются»).
    /// </para>
    /// <para>
    /// Под карточками — затемнение темы (BackdropDim): с ним тема прошла
    /// проверку читаемости, и текст на полупрозрачных карточках читается
    /// так же, как на прочих вкладках. Переход к лёгкому затемнению —
    /// у правого края содержимого, по ширине одной карточки.
    /// </para>
    /// <para>
    /// Колонка уже 220 (окно по умолчанию, 1080): персонажу места нет,
    /// картинка ложится по центру страницы под сплошным затемнением темы —
    /// как на прочих вкладках. Прежде в такой колонке торчала полоска арта
    /// в несколько точек и читалась ошибкой отрисовки (снимок 30.09).
    /// </para>
    /// </remarks>
    private void PlaceArt()
    {
        double width = ArtLayer.ActualWidth, height = ArtLayer.ActualHeight;

        if (TryFindResource("SideArt") is not ImageBrush { ImageSource: { } image }
            || width <= 0 || height <= 0 || image.Height <= 0)
        {
            Art.Fill = null;
            ArtShade.Fill = null;
            return;
        }

        double aspect = image.Width / image.Height;
        double w = height * aspect, h = height;

        // Уже страницы — растягиваем до её ширины: пустая полоса справа хуже обрезки.
        if (w < width)
        {
            w = width;
            h = width / aspect;
        }

        double column = ArtColumn.ActualWidth;
        bool side = column >= ArtColumnMin;
        double centre = side ? width - column / 2 : width / 2;

        double left = centre - w / 2;

        Art.Fill = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            TileMode = TileMode.None,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(left, (height - h) / 2, w, h),
        };

        // Левый край картинки — в подложку, без черты. Растворение кончается
        // до колонки с артом (не позже 80 % пути от края картинки до неё),
        // чтобы персонаж стоял целиком; без колонки — на четверти картинки.
        // Картинка, начинающаяся левее страницы, края не показывает — маска
        // не нужна.
        double fade = side ? Math.Max(120, (width - column - left) * 0.8) : w * 0.25;

        Art.OpacityMask = left <= 0
            ? null
            : new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(left, 0),
                EndPoint = new Point(left + fade, 0),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Colors.Black, 1),
                },
            };

        var backdrop = TryFindResource("BackdropColor") is Color c ? c : Colors.Black;
        double dim = TryFindResource("BackdropDim") is double d ? d : 1.0;

        var under = Color.FromArgb((byte)Math.Round(dim * 255), backdrop.R, backdrop.G, backdrop.B);

        if (!side)
        {
            ArtShade.Fill = new SolidColorBrush(under);
            return;
        }

        double edge = width - column;
        var over = Color.FromArgb((byte)Math.Round(ArtDim * 255), backdrop.R, backdrop.G, backdrop.B);

        // Переход начинается под карточками и тянется на большую часть
        // колонки. Прежде он шёл 300 точек от края карточек: затемнение
        // менялось прямо по волосам персонажа, у края колонки стоит ещё
        // и полоса прокрутки страницы, и картинка читалась обрубленной
        // по вертикальной черте (владелец, 30.09). Середина — не прямая:
        // промежуточная точка держит затемнение дольше у карточек и
        // быстрее отпускает у персонажа.
        var middle = Color.FromArgb((byte)Math.Round((dim * 0.45 + ArtDim * 0.55) * 255), backdrop.R, backdrop.G, backdrop.B);

        ArtShade.Fill = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            StartPoint = new Point(edge - 160, 0),
            EndPoint = new Point(edge + column * 0.7, 0),
            GradientStops =
            {
                new GradientStop(under, 0),
                new GradientStop(middle, 0.4),
                new GradientStop(over, 1),
            },
        };
    }

    private void OnQuickCheck(object sender, RoutedEventArgs e) => Open("check");

    private void OnQuickLog(object sender, RoutedEventArgs e) => Open("log");

    private void OnQuickDoctor(object sender, RoutedEventArgs e) => Open("doctor");

    private void OnDesyncSettings(object sender, RoutedEventArgs e) => Open("desync");

    private void Open(string section) => (Window.GetWindow(this) as MainWindow)?.Open(section);

    /// <summary>
    /// Папка настроек в проводнике, с выделенным файлом.
    /// </summary>
    /// <remarks>
    /// Папку, а не файл в блокноте: окно работает от администратора, и блокнот,
    /// открытый им, тоже был бы администраторским — правка настроек мимо окна
    /// с полными правами без нужды. Проводник открывается от человека.
    /// </remarks>
    private void OnQuickConfig(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = Path.GetFullPath(AppSettings.DefaultPath);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { File.Exists(file) ? "/select," + file : Path.GetDirectoryName(file)! },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            ShowProblem("Проводник не открылся: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>То же окно, что у шестерёнки на вкладке VPN.</summary>
    private void OnTunnelSettings(object sender, RoutedEventArgs e)
    {
        var window = new TunnelSettingsWindow { Owner = Window.GetWindow(this) };

        window.ShowDialog();

        if (window.Changed)
        {
            Update();
            this.Offer("Настройки туннеля изменены");
        }
    }

    public StatusView()
    {
        InitializeComponent();

        // Опрос по времени, а не подписка на события: супервизор — отдельный
        // процесс и пишет своё состояние в файл. Две секунды достаточно,
        // чтобы нажатие «Запустить» отозвалось раньше, чем человек усомнится.
        _refresh.Tick += (_, _) => Update();

        Loaded += (_, _) =>
        {
            Update();

            // Не по времени, в отличие от прочего: режим меняется только
            // отсюда, а автозапуск спрашивается у планировщика запуском
            // schtasks — раз в две секунды это был бы процесс на ровном месте.
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            ShowModes(settings);
            ShowAutostart();
            ShowWidth(settings);

            // Проверка обновлений идёт при запуске окна и может закончиться
            // уже после того, как «Главная» показана, — поэтому и подписка.
            // Отписка обязательна: событие статическое, а раздел пересоздаётся
            // при каждом возврате.
            UpdateNotice.Changed += OnUpdateChanged;
            ShowUpdate();

            // Возврат на вкладку посреди запуска: сам запуск никуда не делся,
            // а анимация была снята при уходе — заводим её обратно.
            if (_startingSince is not null)
                StartAnimations();

            _refresh.Start();
        };

        Unloaded += (_, _) =>
        {
            UpdateNotice.Changed -= OnUpdateChanged;
            _refresh.Stop();

            // Анимация на скрытом виде продолжала бы будить композитор
            // впустую. Само состояние запуска при этом сохраняется.
            StopAnimations();
        };
    }

    private void OnUpdateChanged() => Dispatcher.InvokeAsync(ShowUpdate);

    /// <summary>Карточка «вышла новая версия» — если есть что и о нём не сказали «не сейчас».</summary>
    private void ShowUpdate()
    {
        var release = UpdateNotice.Available;

        if (!UpdateNotice.ShouldOffer(release, AppSettings.Load(AppSettings.DefaultPath)))
        {
            UpdateCard.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateTitle.Text = $"Вышла {release!.Version}";

        var highlights = UpdateNotice.Highlights(release.Notes);

        UpdateWhat.Text = highlights.Count > 0
            ? string.Join(" · ", highlights) + "."
            : "Установлена " + UpdateCheck.Current + ".";

        UpdateCard.Visibility = Visibility.Visible;
    }

    // Установка та же, что в блоке «Обновление» ниже, и на той же странице:
    // прежде кнопка уводила в «Ещё» ради одного вопроса «обновить?».
    private void OnUpdateNow(object sender, RoutedEventArgs e) => Updates.Install();

    private void OnUpdateNotes(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = UpdateNotice.PageOf(release), UseShellExecute = true });
        }
        catch (Exception)
        {
            // Нет браузера — те же примечания видны в «Ещё» при установке.
        }
    }

    /// <summary>
    /// «Не сейчас»: об этой версии больше не напоминать. Точка у «Ещё»
    /// остаётся — она тихая и уходит, только когда версия поставлена.
    /// </summary>
    private void OnUpdateLater(object sender, RoutedEventArgs e)
    {
        if (UpdateNotice.Available is not { } release)
            return;

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { DismissedUpdate = release.Version })
                .Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не сохранилось — напомним при следующем запуске, беды в том нет.
        }

        UpdateCard.Visibility = Visibility.Collapsed;
    }

    private void Update()
    {
        AppSettings settings;

        try
        {
            settings = AppSettings.Load(AppSettings.DefaultPath);
        }
        catch (Exception ex)
        {
            ShowProblem($"Настройки не читаются: {ex.Message}");
            return;
        }

        Problem.Visibility = Visibility.Collapsed;

        PresetValue.Text = settings.DescribePreset();
        ShowPresetNote(settings.PresetName);
        ServerValue.Text = _exit is null ? settings.DescribeServer() : $"{settings.DescribeServer()} · сейчас {_exit}";
        DnsValue.Text = settings.DnsServer;

        // Ссылки на подписки — пароли, и в окне им не место. Показываем лишь
        // счёт: его хватает, чтобы понять, почему нет серверов.
        SubscriptionValue.Text = SubscriptionBook.Load().Describe(settings);

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        ShowState(settings, state, running);
        ShowFooter(state, running);
        ShowWarnings(settings);

        _ = ReadExitAsync(settings, running && state!.Services.Any(s => s.Name == "sing-box"));
    }

    /// <summary>Выход, который держит движок; <c>null</c> — не знаем или туннеля нет.</summary>
    private string? _exit;

    private bool _readingExit;

    /// <summary>
    /// Спрашивает у движка, через какой сервер идёт трафик, — для строки «Сервер».
    /// </summary>
    /// <remarks>
    /// Владелец 26.09: «нигде так и не написано, какой сервер активен». Строка
    /// говорила «авто (по задержке)» — то, что в настройках, а не то, чем идёт
    /// трафик. Спрашиваем движок, как nz status: 23.09 настройки говорили
    /// «авто», а движок держался WARP из кэша. Прежний ответ держится до нового,
    /// чтобы строка не мигала на каждом тике.
    /// </remarks>
    private async Task ReadExitAsync(AppSettings settings, bool tunnel)
    {
        if (!tunnel)
        {
            _exit = null;
            return;
        }

        if (_readingExit)
            return;

        _readingExit = true;

        try
        {
            var (server, _) = await TunnelStatus.CurrentExitAsync(CancellationToken.None);

            if (server is null || server == _exit)
                return;

            _exit = server;
            ServerValue.Text = $"{settings.DescribeServer()} · сейчас {server}";
        }
        finally
        {
            _readingExit = false;
        }
    }

    private void ShowState(AppSettings settings, SupervisorState? state, bool running)
    {
        // Запуск показывается своим чередом: пока он идёт, «остановлено»
        // означает не отказ, а то, что супервизор ещё не дописал состояние.
        if (_startingSince is not null && ShowStarting(settings, state, running))
            return;

        StartButton.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (!running)
        {
            ShowDot("Faint");
            ShowStateBar("Faint");

            StateLine.Text = "Остановлено";

            StateHint.Text = settings.NeedsProxy || settings.NeedsDesync
                ? "Обход не работает: трафик идёт напрямую."
                : "В этом режиме запускать нечего: VPN выключен, пресет не выбран.";

            StartButton.IsEnabled = settings.NeedsProxy || settings.NeedsDesync;

            // Движки остаются на виду и остановленными, просто серыми. Пустое
            // место на их месте читается как «их нет вовсе», тогда как раздел
            // отвечает на другой вопрос: что должно работать и работает ли.
            SetEngines(Planned(settings, "остановлен", "Faint"));

            return;
        }

        var services = state!.Services;
        bool healthy = services.All(s => s.Health == ServiceHealth.Healthy);

        // Три состояния, а не два. «Оговорки» бывают разной тяжести: движок,
        // которому не удалась глубокая проверка, ещё несёт трафик, а умерший
        // не несёт ничего. Одним жёлтым это выглядело одинаково, и разницу
        // приходилось искать в списке ниже.
        bool broken = services.Any(s =>
            s.Health is ServiceHealth.Dead or ServiceHealth.Faulted);

        var colour = healthy ? "Accent" : broken ? "Danger" : "Warn";

        ShowDot(colour);
        ShowStateBar(colour);

        StateLine.Text = healthy
            ? "Работает"
            : broken ? "Движок не работает" : "Работает с оговорками";

        StateHint.Text = healthy
            ? $"Запущено {Ago(state.StartedAt)}."
            : broken
                ? "Один из движков не запущен — обход работает не полностью. Подробности ниже."
                : "Часть движков не в порядке — подробности ниже.";

        SetEngines(services.Select(Row).ToList());
    }

    /// <summary>
    /// Показывает ход запуска. Возвращает <c>false</c>, когда показывать
    /// больше нечего и состояние пора рисовать обычным путём.
    /// </summary>
    /// <remarks>
    /// Умерший движок здесь не считается концом: супервизор его перезапустит,
    /// и до тех пор запуск продолжается. Концом считается только «сдался»
    /// либо исчерпанное терпение — иначе окно объявляло бы отказ, пока
    /// внизу ещё идут попытки.
    /// </remarks>
    private bool ShowStarting(AppSettings settings, SupervisorState? state, bool running)
    {
        var since = _startingSince!.Value;
        var services = running ? state!.Services : [];

        bool ready = services.Count > 0 && services.All(s => s.Health == ServiceHealth.Healthy);
        bool gaveUp = services.Any(s => s.Health == ServiceHealth.Faulted);

        if (ready || gaveUp || DateTimeOffset.Now - since > StartupPatience)
        {
            EndStarting();
            return false;
        }

        StartButton.Visibility = Visibility.Collapsed;
        StopButton.Visibility = Visibility.Visible;
        StopButton.IsEnabled = true;

        ShowDot("Warn");
        ShowStateBar("Warn");

        StateLine.Text = "Запускается…";

        var seconds = (int)(DateTimeOffset.Now - since).TotalSeconds;
        StateHint.Text = $"{DescribePhase(settings, running, services)} — {seconds} с";

        // Пока супервизор не дописал состояние, движки показываются жёлтыми
        // и «запускается». Прежде список опустошался, и они пропадали ровно
        // на те десятки секунд, когда на них и смотрят: раздел отвечал «их
        // нет» на вопрос «поднимаются ли они».
        SetEngines(services.Count > 0
            ? services.Select(Row).ToList()
            : Planned(settings, "запускается", "Warn"));

        return true;
    }

    /// <summary>
    /// Чем занят запуск прямо сейчас.
    /// </summary>
    /// <remarks>
    /// До появления состояния фаза определяется по живым процессам, а не по
    /// файлу: супервизор пишет состояние впервые лишь после того, как поднял
    /// все службы, — то есть ровно после окончания промежутка, который здесь
    /// и показывается. Файл в это время либо отсутствует, либо остался от
    /// прошлого запуска.
    /// </remarks>
    private static string DescribePhase(
        AppSettings settings,
        bool running,
        IReadOnlyList<ServiceState> services)
    {
        if (!running)
        {
            if (!IsRunning("sing-box"))
                return "Собираем конфиг и поднимаем супервизор";

            // Проверка прохода трафика уходит в сеть и занимает основную часть
            // ожидания, поэтому названа отдельно: иначе эти секунды выглядят
            // как необъяснённая пауза.
            return settings.VerifyTraffic
                ? "Туннель поднимается, проверяем проход трафика"
                : "Туннель поднимается";
        }

        var singBox = services.FirstOrDefault(s => s.Name == "sing-box");

        return singBox?.Health switch
        {
            ServiceHealth.Degraded => "Туннель поднят, проверка ещё не прошла",
            ServiceHealth.Dead => "Туннель не поднялся, идёт перезапуск",
            _ => "Движки поднимаются",
        };
    }

    private static bool IsRunning(string processName)
    {
        var found = Process.GetProcessesByName(processName);

        // Каждый Process держит системный дескриптор, а опрос идёт раз
        // в секунду: без освобождения они копятся всё время запуска.
        foreach (var process in found)
            process.Dispose();

        return found.Length > 0;
    }

    private void BeginStarting()
    {
        _startingSince = DateTimeOffset.Now;
        _refresh.Interval = StartingInterval;

        StartAnimations();
        Update();
    }

    private void EndStarting()
    {
        _startingSince = null;
        _refresh.Interval = CalmInterval;

        StopAnimations();
    }

    private void StartAnimations()
    {
        // Анимации выключены — запуск видно цветом полосы, без бега и мигания.
        if (!Motion.Enabled)
        {
            ShowStateBar("Warn");
            return;
        }

        // Дорожка тусклая, засечка яркая. Прежде оба красились в Warn,
        // и бегущая метка была невидима: она ехала по полосе своего же цвета.
        ShowStateBar("Border");
        StartProgressMark.Visibility = Visibility.Visible;

        // Ширину берём измеренную: полоса на экране уже есть, потому что
        // кнопку только что нажали. Запасное значение — на случай, если
        // раскладка почему-то ещё не прошла: метка уехала бы мимо полосы.
        var span = StateBar.ActualWidth > 0 ? StateBar.ActualWidth : 520;

        StartProgressShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = -StartProgressMark.Width,
            To = span,
            Duration = new Duration(TimeSpan.FromSeconds(1.3)),
            RepeatBehavior = RepeatBehavior.Forever,
        });

        Dot.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 1,
            To = 0.2,
            Duration = new Duration(TimeSpan.FromSeconds(0.8)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void StopAnimations()
    {
        StartProgressMark.Visibility = Visibility.Collapsed;

        // Снятие анимации передачей null обязательно: остановленная анимация
        // продолжает удерживать своё последнее значение, и точка осталась бы
        // навсегда полупрозрачной, а «работает» выглядело бы приглушённым.
        StartProgressShift.BeginAnimation(TranslateTransform.XProperty, null);
        Dot.BeginAnimation(OpacityProperty, null);
        Dot.Opacity = 1;
    }

    /// <summary>
    /// Красит полосу состояния в тот же цвет, что и точка.
    /// </summary>
    /// <remarks>
    /// Один источник цвета на оба показа: разойдясь, они дали бы зелёную
    /// точку над красной полосой — и человеку пришлось бы решать, какой
    /// из них верить.
    /// </remarks>
    /// <summary>
    /// Красит точку состояния, её свечение и точку в строке внизу — одним цветом.
    /// </summary>
    /// <remarks>
    /// Серая «стоит» — без свечения: светящаяся серая точка выглядит
    /// включённой лампочкой, а обход в это время не идёт.
    /// </remarks>
    private void ShowDot(string colourKey)
    {
        var brush = (Brush)FindResource(colourKey);

        Dot.Fill = brush;
        FooterDot.Fill = brush;
        DotGlow.Color = colourKey != "Faint" && brush is SolidColorBrush solid ? solid.Color : Colors.Transparent;
    }

    /// <summary>
    /// Строка внизу: состояние с длительностью и время сборки конфига туннеля.
    /// </summary>
    private void ShowFooter(SupervisorState? state, bool running)
    {
        FooterState.Text = running && _startingSince is null
            ? $"{StateLine.Text} · {Span(DateTimeOffset.Now - state!.StartedAt)}"
            : StateLine.Text;

        // Конфиг собирается при каждом запуске, и серверы подписок берутся
        // тогда же, — поэтому время файла и есть время, когда их взяли.
        // Отдельного «подписки обновлены» программа не хранит.
        try
        {
            var config = new FileInfo(Path.Combine("runtime", "singbox.json"));

            FooterConfig.Text = config.Exists
                ? $"Конфиг туннеля собран {config.LastWriteTime:dd.MM.yyyy} в {config.LastWriteTime:HH:mm}"
                : string.Empty;
        }
        catch (Exception)
        {
            FooterConfig.Text = string.Empty;
        }
    }

    /// <summary>«23 дня, 4 часа», «4 ч 12 мин», «7 мин».</summary>
    internal static string Span(TimeSpan span)
    {
        if (span.TotalDays >= 1)
        {
            int days = (int)span.TotalDays;
            return $"{days} {Plural(days, "день", "дня", "дней")}, {span.Hours} {Plural(span.Hours, "час", "часа", "часов")}";
        }

        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours} ч {span.Minutes} мин"
            : $"{Math.Max(0, span.Minutes)} мин";
    }

    private static string Plural(int count, string one, string few, string many)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return many;

        return (tail % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    /// <summary>Пресет, описание которого уже показано, — чтобы не читать файл на каждом тике.</summary>
    private string? _notedPreset = "\0";

    /// <summary>
    /// Первая фраза описания пресета — из его же файла.
    /// </summary>
    /// <remarks>
    /// Читается при смене пресета, а не каждые две секунды: разбор файла —
    /// сотни строк, а меняется выбор раз в неделю.
    /// </remarks>
    private void ShowPresetNote(string? preset)
    {
        if (preset == _notedPreset)
            return;

        _notedPreset = preset;
        PresetNote.Text = preset is null ? "Десинк без пресета не поднимается." : string.Empty;

        if (preset is null)
            return;

        try
        {
            if (NetZapret.Zapret.ZapretPaths.FindPreset(preset) is not { } file)
            {
                PresetNote.Text = "Файла пресета нет в папке presets.";
                return;
            }

            var description = new NetZapret.Zapret.PresetReader().Read([file]).FirstOrDefault()?.Description;

            PresetNote.Text = FirstSentence(description) ?? "Описания в файле пресета нет.";
        }
        catch (Exception)
        {
            PresetNote.Text = string.Empty;
        }
    }

    /// <summary>
    /// До первой точки, двоеточия или тире с пробелом после — дальше в описаниях
    /// пресетов начинаются подробности.
    /// </summary>
    /// <remarks>
    /// С пробелом, а не любая точка: в описаниях стоят версии и имена
    /// («v1.0.3», «www.facebook.com»), и по голой точке фраза рвалась бы на них.
    /// </remarks>
    internal static string? FirstSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        text = text.Trim();

        var end = System.Text.RegularExpressions.Regex.Match(text, @"[.:](\s|$)|\s—\s");

        return end.Success && end.Index > 0 ? text[..end.Index].TrimEnd() + "." : text;
    }

    /// <summary>
    /// Выпадающий список пресетов у «Изменить».
    /// </summary>
    /// <remarks>
    /// Список тот же и в том же порядке, что в «Десинке» (PresetOrder), и запись
    /// та же — PresetName. Последним пунктом — сам раздел: там видно, чем
    /// пресеты отличаются, а здесь только имена.
    /// </remarks>
    private void OnPresetMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PresetMenu, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        string? chosen;

        try
        {
            chosen = AppSettings.Load(AppSettings.DefaultPath).PresetName;

            var names = PresetOrder.Apply(
                NetZapret.Zapret.ZapretPaths.PresetFiles.Select(Path.GetFileName).OfType<string>().ToList(),
                file => file);

            foreach (var file in names)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var item = new MenuItem { Header = name, Tag = name };

                if (string.Equals(name, chosen, StringComparison.OrdinalIgnoreCase))
                    item.Icon = new TextBlock { Text = "", FontFamily = (FontFamily)FindResource("IconFont"), Foreground = (Brush)FindResource("Accent") };

                item.Click += (_, _) => ChoosePreset(name);
                menu.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Пресеты не читаются: " + ex.GetBaseException().Message);
            return;
        }

        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

        var all = new MenuItem { Header = "Все пресеты — раздел «Десинк»" };
        all.Click += (_, _) => Open("desync");
        menu.Items.Add(all);

        menu.IsOpen = true;
    }

    private void ChoosePreset(string name)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (string.Equals(settings.PresetName, name, StringComparison.OrdinalIgnoreCase))
                return;

            (settings with { PresetName = name }).Save(AppSettings.DefaultPath);

            Update();
            this.Offer($"Выбран пресет «{name}»");
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать выбор: " + ex.GetBaseException().Message);
        }
    }

    private void ShowStateBar(string colourKey) =>
        StateBarFill.Fill = (Brush)FindResource(
            // Точки и полосы берут насыщенный жёлтый: текстовый на светлой
            // теме выглядит у мелкой метки не предупреждением, а выцветшей
            // серостью — у неё нет площади, чтобы донести приглушённый оттенок.
            colourKey == "Warn" ? "WarnFill" : colourKey);

    /// <summary>
    /// Движки, которые поднимутся при нынешней настройке.
    /// </summary>
    /// <remarks>
    /// Список тот же, что собирает супервизор: туннель — когда режим ведёт
    /// хоть что-то через VPN, десинк — когда выбран пресет. В режиме, где
    /// не запускается ничего, список пуст, и это верно: запускать нечего.
    /// </remarks>
    private IReadOnlyList<EngineRow> Planned(AppSettings settings, string detail, string colourKey)
    {
        var rows = new List<EngineRow>();
        var colour = (Brush)FindResource(colourKey);

        if (settings.NeedsProxy)
            rows.Add(new EngineRow("sing-box", detail, string.Empty, colour));

        if (settings.NeedsDesync)
            rows.Add(new EngineRow("winws2", detail, string.Empty, colour));

        return rows;
    }

    private EngineRow Row(ServiceState service)
    {
        // Слова те же, что в консоли. Degraded — это «процесс жив, а проверка
        // не проходит»: самый коварный случай, и называть его «работает»
        // нельзя, иначе окно будет уверять в исправности молчащей трубы.
        // Слова — общие с треем и nz status (EngineHealth.Status): причина
        // надзора и время начала, а не «запущен, но не отвечает» (26.09).
        var key = service.Health switch
        {
            ServiceHealth.Healthy => "Accent",
            ServiceHealth.Degraded => "Warn",
            ServiceHealth.Dead or ServiceHealth.Faulted => "Danger",
            _ => "Faint",
        };

        // Причина — только у того, что не работает: у работающего Status
        // говорит «работает, процесс N», а номер и так стоит строкой ниже.
        // У работающего — только примечание надзора («временная замена…»).
        var detail = service.Health == ServiceHealth.Healthy
            ? service.Remark ?? string.Empty
            : EngineHealth.Status(service);

        return new EngineRow(service.Name, EngineHealth.Word(service.Health), detail, (Brush)FindResource(key), service.ProcessId);
    }

    /// <summary>
    /// То, что стоит знать до запуска.
    /// </summary>
    /// <remarks>
    /// Оба случая измерены на живых машинах и оба приходят молча. Защитник
    /// со своим сетевым фильтром обесценивает десинк, ничего об этом
    /// не сообщая; Kaspersky вдобавок возвращает файл hosts к своему
    /// умолчанию, стирая все пины.
    /// </remarks>
    private IReadOnlyList<EngineRow>? _engines;
    private IReadOnlyList<WarningRow>? _warnings;
    private DateTimeOffset _warningsAt = DateTimeOffset.MinValue;
    private bool _warningsBusy;

    /// <summary>Сколько держать проверку антивирусов и hosts, прежде чем повторить.</summary>
    private static readonly TimeSpan WarningsInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Карточки движков — только если что-то в них изменилось.
    /// </summary>
    /// <remarks>
    /// Прежде список собирался заново каждые две секунды: новый ItemsSource —
    /// это новые элементы со всеми шаблонами, и окно дёргалось раз в два
    /// тика даже на RTX 4060 (владелец, 24.09: «интерфейс подвисать может»).
    /// Строки — записи, и сравниваются по значению: одинаковые не трогаем.
    /// </remarks>
    private void SetEngines(IReadOnlyList<EngineRow> rows)
    {
        if (_engines is not null && _engines.SequenceEqual(rows))
            return;

        _engines = rows;

        // Каждый движок — в своей карточке, рядом с выключателем (26.09).
        ShowEngine(rows.FirstOrDefault(r => r.Name == "winws2"), DesyncDot, DesyncState, DesyncPid);
        ShowEngine(rows.FirstOrDefault(r => r.Name == "sing-box"), TunnelDot, TunnelState, TunnelPid);
    }

    /// <summary>Строка состояния движка в его карточке.</summary>
    /// <remarks>
    /// Нет строки — движок в этот запуск не поднимали: выключен или нет
    /// подписки. «Остановлен» здесь соврало бы, что он был.
    /// </remarks>
    private void ShowEngine(EngineRow? row, System.Windows.Shapes.Ellipse dot, TextBlock state, TextBlock pid)
    {
        if (row is null)
        {
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Faint");
            state.Text = "не поднимается";
            pid.Text = string.Empty;
            return;
        }

        dot.Fill = row.Color;
        state.Text = row.Word;

        // Номер процесса — чтобы найти движок в диспетчере задач (владелец,
        // 26.09). У движка не в порядке на его месте причина надзора: она
        // важнее номера, и места под обе строки в карточке нет.
        pid.Text = row.Detail.Length > 0
            ? row.Detail
            : row.ProcessId is { } id ? $"Процесс: {id}" : string.Empty;
    }

    /// <summary>
    /// Предупреждения — в фоне и раз в полминуты, а не в потоке окна каждые две секунды.
    /// </summary>
    /// <remarks>
    /// Проверка антивирусов перебирает все процессы системы, а проверка hosts
    /// читает почти тысячу строк файла. Ни то, ни другое за две секунды
    /// не меняется, а делалось в потоке окна на каждом тике.
    /// </remarks>
    private void ShowWarnings(AppSettings settings)
    {
        if (_warningsBusy || DateTimeOffset.Now - _warningsAt < WarningsInterval)
            return;

        _warningsBusy = true;
        bool needsDesync = settings.NeedsDesync;

        _ = Task.Run(() => CollectWarnings(needsDesync)).ContinueWith(task =>
        {
            _warningsBusy = false;
            _warningsAt = DateTimeOffset.Now;

            if (task.IsCompletedSuccessfully)
                SetWarnings(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void SetWarnings(IReadOnlyList<WarningRow> rows)
    {
        if (_warnings is not null && _warnings.SequenceEqual(rows))
            return;

        _warnings = rows;
        Warnings.ItemsSource = rows;
    }

    private static IReadOnlyList<WarningRow> CollectWarnings(bool needsDesync)
    {
        var rows = new List<WarningRow>();

        if (HostsEditor.WhoReplaced() is { } who)
        {
            rows.Add(new WarningRow(
                $"Файл hosts переписан: {who}",
                "Изменённый hosts он считает признаком заражения и вернул файл к своему "
                + "умолчанию — вместе со всеми пинами. Пин на этой машине не живёт, пока "
                + "файл не внесён в доверенные."));
        }

        var guards = SecuritySoftware.Running();

        if (guards.Count > 0 && needsDesync)
        {
            rows.Add(new WarningRow(
                $"Работает {string.Join(", ", guards.Select(g => g.Name))}",
                "Его сетевой фильтр встаёт на тот же слой, что и наш, проверка защищённых "
                + "соединений переустанавливает TLS своим клиентом, а WinDivert он помечает "
                + "как RiskTool и может увезти в карантин. Мы этого отсюда не видим; если "
                + "обход не помогает без внятной причины — отключите защиту на десять минут "
                + "и повторите."));
        }

        return rows;
    }

    private void ShowProblem(string text)
    {
        Problem.Text = text;
        Problem.Visibility = Visibility.Visible;
    }

    private static string Ago(DateTimeOffset since)
    {
        var passed = DateTimeOffset.Now - since;

        return passed switch
        {
            { TotalMinutes: < 1 } => "только что",
            { TotalHours: < 1 } => $"{(int)passed.TotalMinutes} мин назад",
            { TotalDays: < 1 } => $"{(int)passed.TotalHours} ч назад",
            _ => $"{(int)passed.TotalDays} дн назад",
        };
    }

    /// <summary>
    /// Пять режимов с ценой каждого.
    /// </summary>
    /// <remarks>
    /// Цена названа в самом описании, а не выясняется опытом: «всё через VPN
    /// без исключений» ломает отечественные сервисы, и узнавать об этом
    /// по неработающим госуслугам человек не должен.
    /// </remarks>
    private void ShowModes(AppSettings settings)
    {
        var engines = settings.Engines;

        DesyncSwitch.IsChecked = engines.Desync;
        TunnelSwitch.IsChecked = engines.Tunnel;

        // Третье состояние — включён, а не поднимается: при игнорируемых
        // исключениях всё уходит в туннель. Промолчать значило бы показать
        // включённым то, чего в диспетчере задач не будет.
        DesyncLine.Text = !engines.Desync
            ? "Выключен. Закрытые по имени сайты останутся закрытыми."
            : !engines.DesyncRuns
                ? "Не поднимается: исключения игнорируются, и всё идёт в туннель."
                : engines.Tunnel
                    ? "Чинит имена в рукопожатии. Трафик идёт напрямую."
                    : "Чинит имена в рукопожатии. «Через VPN» без туннеля идёт напрямую.";

        // Туннель говорит и про охват, потому что тот выводится из пары:
        // без десинка он забирает всё, вместе с ним — только названное.
        // Человек, щёлкнувший один выключатель, вправе узнать, что этим
        // изменилось у второго.
        // «Напрямую» при одном туннеле остаётся напрямую — таблица владельца
        // 23.09; «весь трафик» без оговорки обещал бы и его.
        TunnelLine.Text = !engines.Tunnel
            ? "Не поднимается. Адрес остаётся домашним."
            : engines.IgnoreExclusions
                ? "Забирает весь трафик, исключения не действуют."
                : engines.TunnelTakesAll
                    ? "Забирает всё, кроме поставленного «напрямую»."
                    : "Уводит то, что названо в маршрутах.";

        DesyncCard.BorderBrush = (Brush)FindResource(engines.DesyncRuns ? "Accent" : "Border");
        TunnelCard.BorderBrush = (Brush)FindResource(engines.Tunnel ? "Accent" : "Border");

        EnginesLine.Text = engines.Complaint ?? string.Empty;
        EnginesLine.Visibility = engines.Complaint is null ? Visibility.Collapsed : Visibility.Visible;

        // Автозапуск поднимает ровно это. Сказано здесь же, где задано:
        // иначе про связь пришлось бы догадываться, а догадка — источник
        // того самого «трей запускается, а движки нужно поднимать кнопкой».
        AutostartRaises.Text = "При входе в систему под этим пользователем поднимется: " + engines.Describe() + ".";
    }

    /// <summary>
    /// Переключает движок.
    /// </summary>
    /// <remarks>
    /// Через <see cref="AppSettings.With(EngineChoice)"/>: тот пишет заодно
    /// и выведенный режим, на языке которого говорят конфиг движка, отчёты
    /// и консоль. Оставленный отставшим, режим развёл бы показания.
    /// </remarks>
    private void Choose(Func<EngineChoice, EngineChoice> change)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings.With(change(settings.Engines));

            next.Save(AppSettings.DefaultPath);

            ShowModes(next);
            Update();

            this.Offer("Движки: " + next.Engines.Describe());
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать: " + ex.GetBaseException().Message);
        }
    }

    private void OnDesync(object sender, RoutedEventArgs e) =>
        Choose(c => c with { Desync = DesyncSwitch.IsChecked == true });

    private void OnTunnel(object sender, RoutedEventArgs e) =>
        Choose(c => c with { Tunnel = TunnelSwitch.IsChecked == true });

    /// <summary>Что планировщик ответил в прошлый раз — на время работы окна.</summary>
    private static (bool Installed, bool Stale)? _autostart;

    /// <remarks>
    /// Планировщик спрашивается в фоне, а до ответа показывается прошлый.
    /// Замер 28.09: два запуска schtasks — «заведена ли» и «не устарела ли» —
    /// 73–91 мс на главном потоке при каждом открытии «Главной», и это была
    /// большая часть паузы, о которую спотыкалась анимация раздела.
    /// </remarks>
    private async void ShowAutostart()
    {
        if (_autostart is { } known)
        {
            ApplyAutostart(known);
        }
        else
        {
            AutostartValue.Text = "Задача планировщика: проверяю…";
            AutostartValue.Visibility = Visibility.Visible;
            AutostartSwitch.IsEnabled = false;
        }

        try
        {
            var fresh = await Task.Run(() =>
            {
                bool installed = AutostartTask.IsInstalled(AutostartTask.DefaultTaskName);
                return (Installed: installed, Stale: installed && IsStale());
            });

            _autostart = fresh;
            ApplyAutostart(fresh);
        }
        catch (Exception ex)
        {
            AutostartValue.Text = "Задача планировщика не читается.";
            AutostartValue.Visibility = Visibility.Visible;
            AutostartSwitch.IsEnabled = false;
            ShowProblem("Планировщик не отвечает: " + ex.GetBaseException().Message);
        }
    }

    private void ApplyAutostart((bool Installed, bool Stale) state)
    {
        bool installed = state.Installed;

        AutostartValue.Visibility = Visibility.Collapsed;
        AutostartSwitch.IsChecked = installed;
        AutostartSwitch.IsEnabled = true;

        if (state.Stale)
        {
            AutostartValue.Text = "Задача устарела и запускает прежнюю программу — выключите и включите снова.";
            AutostartValue.Visibility = Visibility.Visible;

            ShowProblem(
                "Задача автозапуска осталась от прежней версии и запускает не то, "
                + "что нужно: до 0.5.0 это была консольная программа, которой в поставке "
                + "больше нет. Выключите автозапуск и включите снова — это два щелчка.");
        }
    }

    /// <summary>
    /// Запускает ли задача не ту программу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Задача переживает обновление, а её команда — нет: она записана внутрь
    /// задачи целиком, путём и ключами. До 0.5.0 автозапуск поднимал
    /// netzapret.exe, которого в поставке уже нет, и «заведена» при мёртвой
    /// команде — худший из возможных ответов: обход при входе не поднимется,
    /// а окно скажет, что всё в порядке.
    /// </para>
    /// <para>
    /// Сверяется имя файла, а не путь целиком, и не ключи. Ключи менялись
    /// и ещё будут меняться, а запуск исчезнувшей программы — беда другого
    /// порядка. Путь целиком не годится: он может содержать кириллицу,
    /// а schtasks при перенаправлении отвечает однобайтовой OEM — имя же
    /// латинское и переживает любую из них.
    /// </para>
    /// </remarks>
    private static bool IsStale()
    {
        try
        {
            // Кодировка не задаётся намеренно. Заданная Unicode здесь уже
            // стояла и всё ломала: schtasks пишет однобайтовый текст, а тот,
            // прочитанный парами байт, превращался в кашу, не содержащую
            // вообще ничего, — и всякая задача объявлялась устаревшей.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                ArgumentList = { "/query", "/tn", AutostartTask.DefaultTaskName, "/xml" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var xml = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (xml.Length == 0)
                return false;

            return Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name
                && !xml.Contains(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Не прочиталось — молчим: пугать задачей, о которой мы ничего
            // не выяснили, хуже, чем не сказать.
            return false;
        }
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        // Задача поднимает эту же программу в роли супервизора. Прежде она
        // звала консольную рядом, и без неё автозапуск молча не работал.
        var exe = Environment.ProcessPath;

        if (exe is null)
        {
            ShowProblem("Не удалось определить путь к программе — задача не заведена.");
            return;
        }

        try
        {
            if (AutostartTask.IsInstalled(AutostartTask.DefaultTaskName))
            {
                var (removed, output) = AutostartTask.Remove(AutostartTask.DefaultTaskName);

                if (!removed)
                    ShowProblem("Не удалось убрать автозапуск: " + output);
            }
            else
            {
                var (ok, output) = AutostartTask.Install(new AutostartOptions
                {
                    TaskName = AutostartTask.DefaultTaskName,
                    ExecutablePath = exe,

                    // Задача поднимает интерфейс в трей, а движки он заводит
                    // сам — теми же ключами, что и кнопка «Запустить». Прежде
                    // задача поднимала один супервизор, и при входе не было
                    // ни значка, ни способа остановить обход, кроме как
                    // открыть программу заново.
                    Arguments = TrayIcon.Switch,
                    WorkingDirectory = Path.GetFullPath("."),
                    // SID, а не имя: см. AutostartTask.CurrentUserId.
                    UserId = AutostartTask.CurrentUserId(),
                });

                if (!ok)
                    ShowProblem("Не удалось завести автозапуск: " + output);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Планировщик отказал: " + ex.GetBaseException().Message);
        }

        // Задачу только что завели или убрали — запомненный ответ устарел.
        _autostart = null;
        ShowAutostart();
    }

    /// <summary>
    /// Собирает конфиг и поднимает движки.
    /// </summary>
    /// <remarks>
    /// Сборка идёт при каждом запуске, как в меню консоли. Без неё окно
    /// поднимало движки с тем конфигом, что лежал на диске: смена сервера,
    /// правки маршрутов и переключение режима показывались новыми, а до
    /// туннеля не доходили, пока конфиг не соберут отдельно.
    /// </remarks>
    private async void OnStart(object sender, RoutedEventArgs e)
    {
        StartButton.IsEnabled = false;
        StateLine.Text = "Собираю конфиг…";
        StateHint.Text = "Читаю подписку и правила.";

        var outcome = await EngineControl.StartAsync("кнопка «Запустить» на «Главной»", CancellationToken.None);

        StartButton.IsEnabled = true;

        if (!outcome.Ok)
        {
            ShowProblem(outcome.Message);
            Update();
            return;
        }

        // Прежде кнопки просто гасли на три секунды. Этого хватало, пока
        // запуск был мгновенным; с проверкой прохода трафика он занимает
        // десятки секунд, и кнопки оживали посреди подъёма, показывая
        // «остановлено» у ещё запускающегося движка.
        BeginStarting();
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        EndStarting();

        StopButton.IsEnabled = false;
        StateLine.Text = "Останавливаю…";

        await EngineControl.StopAsync("кнопка «Остановить» на «Главной»", CancellationToken.None);

        StopButton.IsEnabled = true;
        Update();
    }


    /// <summary>
    /// «На всю ширину» (Оформление, 01.10): без предела у содержимого и без колонки под арт.
    /// </summary>
    /// <remarks>
    /// Колонка арта в ноль — и PlaceArt сам кладёт картинку под карточки,
    /// как на узком окне.
    /// </remarks>
    private void ShowWidth(AppSettings settings)
    {
        if (settings.HomeFullWidth)
        {
            ContentColumn.MaxWidth = double.PositiveInfinity;
            ArtColumn.Width = new GridLength(0);
        }
        else
        {
            ContentColumn.MaxWidth = 940;
            ArtColumn.Width = new GridLength(1, GridUnitType.Star);
        }
    }
}
