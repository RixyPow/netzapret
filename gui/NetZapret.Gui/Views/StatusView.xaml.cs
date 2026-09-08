using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
/// Запуск и остановка отданы <c>netzapret.exe</c> рядом. Это не лень:
/// супервизор должен пережить закрытие окна, а значит быть отдельным
/// процессом. Консоль поступает ровно так же, запуская саму себя с ключом
/// <c>start</c>, — и раз путь один, у окна и меню не разойдётся поведение.
/// </para>
/// </remarks>
public partial class StatusView : UserControl
{
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(2) };

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

            _refresh.Start();
        };

        Unloaded += (_, _) => _refresh.Stop();
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
            Engines.ItemsSource = null;

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
    }

    private void OnAutostart(object sender, RoutedEventArgs e)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "netzapret.exe");

        if (!File.Exists(exe))
        {
            ShowProblem($"Не найдена консольная программа: {exe}. Задача запускает именно её.");
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

                    // Те же ключи, что у кнопки «Запустить»: иначе автозапуск
                    // поднимал бы не то, что человек проверил руками.
                    Arguments = BuildStartArguments(),
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

    private void OnStart(object sender, RoutedEventArgs e) => Run(BuildStartArguments());

    private void OnStop(object sender, RoutedEventArgs e) => Run("stop");

    /// <summary>
    /// Ключи запуска супервизора.
    /// </summary>
    /// <remarks>
    /// Повторяет то, что собирает меню консоли. Туннель поднимается только
    /// там, где он куда-то ведёт: в режиме «только десинк» sing-box был бы
    /// вхолостую поднятым TUN — адаптер есть, маршруты стоят, трафика нет,
    /// и первая же неисправность ищется вдвое дольше.
    /// </remarks>
    internal static string BuildStartArguments()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        var arguments = settings.LogsEnabled
            ? $"start --log \"{Path.GetFullPath(Path.Combine("runtime", "supervisor.log"))}\""
            : "start";

        arguments += settings.NeedsProxy
            ? $" --proxy-config \"{Path.GetFullPath(settings.ProxyConfigPath)}\""
            : " --no-proxy";

        if (settings.NeedsDesync)
            arguments += $" --preset \"{settings.PresetName}\"";

        if (settings.VerifyTraffic)
            arguments += " --verify-traffic";

        return arguments;
    }

    /// <summary>
    /// Зовёт консольную программу рядом.
    /// </summary>
    /// <remarks>
    /// Супервизор обязан пережить закрытие окна, а значит быть отдельным
    /// процессом. Окно и так работает от администратора, поэтому запуск идёт
    /// без повышения — оно уже есть.
    /// </remarks>
    private void Run(string arguments)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "netzapret.exe");

        if (!File.Exists(exe))
        {
            ShowProblem($"Не найдена консольная программа: {exe}. Окно кладётся рядом с ней.");
            return;
        }

        try
        {
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;

            // Кнопки оживают через опрос: состояние читается из файла, который
            // супервизор пишет не мгновенно, и мигание «остановлено —
            // работает — остановлено» выглядело бы сбоем.
            Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                StartButton.IsEnabled = true;
                StopButton.IsEnabled = true;
                Update();
            }));
        }
        catch (Exception ex)
        {
            ShowProblem($"Не удалось: {ex.Message}");
        }
    }
}
