using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Строка про один движок.</summary>
public sealed record EngineRow(string Name, string Detail, Brush Color);

/// <summary>Предупреждение, которое стоит прочитать до запуска.</summary>
public sealed record WarningRow(string Title, string Body);

/// <summary>Режим работы в списке выбора.</summary>
public sealed record ModeRow(OperatingMode Key, string Name, string Note)
{
    public bool Chosen { get; set; }

    public Brush Edge => (Brush)Application.Current.FindResource(Chosen ? "Accent" : "Border");

    public Visibility MarkShown => Chosen ? Visibility.Visible : Visibility.Collapsed;
}

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
            ShowModes(AppSettings.Load(AppSettings.DefaultPath));
            ShowAutostart();

            // Возврат на вкладку посреди запуска: сам запуск никуда не делся,
            // а анимация была снята при уходе — заводим её обратно.
            if (_startingSince is not null)
                StartAnimations();

            _refresh.Start();
        };

        Unloaded += (_, _) =>
        {
            _refresh.Stop();

            // Анимация на скрытом виде продолжала бы будить композитор
            // впустую. Само состояние запуска при этом сохраняется.
            StopAnimations();
        };
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
        ServerValue.Text = settings.DescribeServer();
        DnsValue.Text = settings.DnsServer;

        // Ссылки на подписки — пароли, и в окне им не место. Показываем лишь
        // счёт: его хватает, чтобы понять, почему нет серверов.
        SubscriptionValue.Text = SubscriptionBook.Load().Describe(settings);

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        ShowState(settings, state, running);
        ShowWarnings(settings);
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
            Dot.Fill = (Brush)FindResource("Faint");
            StateLine.Text = "Остановлено";

            StateHint.Text = settings.NeedsProxy || settings.NeedsDesync
                ? "Обход не работает: трафик идёт напрямую."
                : "В этом режиме запускать нечего: VPN выключен, пресет не выбран.";

            StartButton.IsEnabled = settings.NeedsProxy || settings.NeedsDesync;

            // Движки остаются на виду и остановленными, просто серыми. Пустое
            // место на их месте читается как «их нет вовсе», тогда как раздел
            // отвечает на другой вопрос: что должно работать и работает ли.
            Engines.ItemsSource = Planned(settings, "остановлен", "Faint");

            return;
        }

        var services = state!.Services;
        bool healthy = services.All(s => s.Health == ServiceHealth.Healthy);

        Dot.Fill = (Brush)FindResource(healthy ? "Accent" : "Warn");

        StateLine.Text = healthy ? "Работает" : "Работает с оговорками";

        StateHint.Text = healthy
            ? $"Запущено {Ago(state.StartedAt)}."
            : "Часть движков не в порядке — подробности ниже.";

        Engines.ItemsSource = services.Select(Row).ToList();
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

        Dot.Fill = (Brush)FindResource("Warn");
        StateLine.Text = "Запускается…";

        var seconds = (int)(DateTimeOffset.Now - since).TotalSeconds;
        StateHint.Text = $"{DescribePhase(settings, running, services)} — {seconds} с";

        // Пока супервизор не дописал состояние, движки показываются жёлтыми
        // и «запускается». Прежде список опустошался, и они пропадали ровно
        // на те десятки секунд, когда на них и смотрят: раздел отвечал «их
        // нет» на вопрос «поднимаются ли они».
        Engines.ItemsSource = services.Count > 0
            ? services.Select(Row).ToList()
            : Planned(settings, "запускается", "Warn");

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
        StartProgress.Visibility = Visibility.Visible;

        // Ширину берём измеренную: карточка на экране уже есть, потому что
        // кнопку только что нажали. Запасное значение — на случай, если
        // раскладка почему-то ещё не прошла: метка уехала бы мимо полосы.
        var span = StartProgress.ActualWidth > 0 ? StartProgress.ActualWidth : 520;

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
        StartProgress.Visibility = Visibility.Collapsed;

        // Снятие анимации передачей null обязательно: остановленная анимация
        // продолжает удерживать своё последнее значение, и точка осталась бы
        // навсегда полупрозрачной, а «работает» выглядело бы приглушённым.
        StartProgressShift.BeginAnimation(TranslateTransform.XProperty, null);
        Dot.BeginAnimation(OpacityProperty, null);
        Dot.Opacity = 1;
    }

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
            rows.Add(new EngineRow("sing-box", detail, colour));

        if (settings.NeedsDesync)
            rows.Add(new EngineRow("winws2", detail, colour));

        return rows;
    }

    private EngineRow Row(ServiceState service)
    {
        // Слова те же, что в консоли. Degraded — это «процесс жив, а проверка
        // не проходит»: самый коварный случай, и называть его «работает»
        // нельзя, иначе окно будет уверять в исправности молчащей трубы.
        var (detail, key) = service.Health switch
        {
            ServiceHealth.Healthy => ("работает", "Accent"),
            ServiceHealth.Degraded => ("запущен, но не отвечает", "Warn"),
            ServiceHealth.Dead => ("процесс умер", "Danger"),
            _ => ("остановлен", "Faint"),
        };

        if (service.ProcessId is { } pid && service.Health == ServiceHealth.Healthy)
            detail += $", процесс {pid}";

        return new EngineRow(service.Name, detail, (Brush)FindResource(key));
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
    private void ShowWarnings(AppSettings settings)
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

        if (guards.Count > 0 && settings.NeedsDesync)
        {
            rows.Add(new WarningRow(
                $"Работает {string.Join(", ", guards.Select(g => g.Name))}",
                "Его сетевой фильтр встаёт на тот же слой, что и наш, проверка защищённых "
                + "соединений переустанавливает TLS своим клиентом, а WinDivert он помечает "
                + "как RiskTool и может увезти в карантин. Мы этого отсюда не видим; если "
                + "обход не помогает без внятной причины — отключите защиту на десять минут "
                + "и повторите."));
        }

        Warnings.ItemsSource = rows;
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
        var rows = new List<ModeRow>
        {
            new(OperatingMode.Selective,
                "Выборочно",
                "Рабочий режим. По умолчанию всё лечится десинком, а через VPN уходит только "
                + "то, что названо в маршрутах."),

            new(OperatingMode.DesyncOnly,
                "Только десинк",
                "Туннель не поднимается вовсе. Для случая, когда подписка кончилась или сервер "
                + "лёг, а десинка хватает: поднимать TUN ради ничего значит без причины путать "
                + "поиск неисправностей."),

            new(OperatingMode.ProxyAll,
                "Всё через VPN, кроме РФ",
                "В туннель уходит всё, кроме выведенного напрямую. Отечественные сервисы "
                + "остаются на прямом пути: через зарубежный адрес банки и госуслуги "
                + "не работают вовсе."),

            new(OperatingMode.ProxyStrict,
                "Всё через VPN без исключений",
                "Включая отечественные сервисы, которые от этого ломаются. Режим для проверки: "
                + "убедиться, что дело не в правилах."),

            new(OperatingMode.Off,
                "Выключено",
                "Ни один движок не запускается, весь трафик идёт напрямую."),
        };

        foreach (var row in rows)
            row.Chosen = row.Key == settings.Mode;

        Modes.ItemsSource = rows;
    }

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OperatingMode mode })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { Mode = mode };
            settings.Save(AppSettings.DefaultPath);

            ShowModes(settings);
            Update();

            this.Offer($"Режим: {settings.DescribeMode()}");
        }
        catch (Exception ex)
        {
            ShowProblem("Не удалось записать режим: " + ex.GetBaseException().Message);
        }
    }

    private void ShowAutostart()
    {
        bool installed;

        try
        {
            installed = AutostartTask.IsInstalled(AutostartTask.DefaultTaskName);
        }
        catch (Exception ex)
        {
            AutostartValue.Text = "не читается";
            AutostartButton.IsEnabled = false;
            ShowProblem("Планировщик не отвечает: " + ex.GetBaseException().Message);

            return;
        }

        AutostartValue.Text = installed ? "заведена" : "не заведена";
        AutostartButton.Content = installed ? "Убрать" : "Завести";
        AutostartButton.IsEnabled = true;

        if (installed && IsStale())
        {
            AutostartValue.Text = "заведена, но устарела";

            ShowProblem(
                "Задача автозапуска осталась от прежней версии и запускает не то, "
                + "что нужно: до 0.5.0 это была консольная программа, которой в поставке "
                + "больше нет. Уберите и заведите заново — это две кнопки.");
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
                    UserId = Environment.UserName,
                });

                if (!ok)
                    ShowProblem("Не удалось завести автозапуск: " + output);
            }
        }
        catch (Exception ex)
        {
            ShowProblem("Планировщик отказал: " + ex.GetBaseException().Message);
        }

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

        var outcome = await EngineControl.StartAsync(CancellationToken.None);

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

        await EngineControl.StopAsync(CancellationToken.None);

        StopButton.IsEnabled = true;
        Update();
    }

}
