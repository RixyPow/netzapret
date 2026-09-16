using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Сценарий первого запуска: четыре шага от «что не работает» до результата.
/// </summary>
/// <remarks>
/// <para>
/// До этой правки программа не вела нового человека вовсе — он попадал
/// на «Главную» с кнопкой запуска и остальным «найдите сами». Разбор
/// в <c>docs\design-brief.md</c>, раздел 6: человек не знает, что для VPN
/// нужна своя подписка, что после смены маршрута нужен перезапуск и что
/// проверка рецептов требует остановленных движков. Мастер закрывает ровно
/// это — называет вслух то, что раньше узнавали методом тыка.
/// </para>
/// <para>
/// Показывается вместо «Главной» ровно один раз, пока в настройках не стоит
/// <see cref="AppSettings.OnboardingDone"/>. Не окно, а обычный раздел:
/// отдельное модальное окно нельзя было бы прервать, вернувшись позже
/// к любому другому разделу, а мастер должен позволять это в любой момент —
/// он не единственный путь в программу, а первое, что видно.
/// </para>
/// </remarks>
public partial class OnboardingView : UserControl
{
    /// <summary>Мастер закрыт — завершением или пропуском.</summary>
    public event EventHandler? Completed;

    private int _step = 1;

    private ServiceDefinition? _target;

    /// <summary>Имя, на котором проверяется результат; <c>null</c> — проверять нечего.</summary>
    private string? _probeHost;

    /// <summary>Подпись цели для отчёта проверки.</summary>
    private string? _probeService;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    private DateTimeOffset? _startedAt;

    public OnboardingView()
    {
        InitializeComponent();

        Targets.ItemsSource = ServiceCatalog.All.Select(s => s.Name).ToList();
        Targets.SelectionChanged += (_, _) => Step1Next.IsEnabled = Targets.SelectedItem is not null;

        _poll.Tick += (_, _) => UpdateTrial();

        Unloaded += (_, _) => _poll.Stop();
    }

    private void Show(int step)
    {
        _step = step;

        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = step == 4 ? Visibility.Visible : Visibility.Collapsed;

        StepLabel.Text = $"Шаг {step} из 4";

        if (step == 3)
            PrepareTrial();

        if (step == 4)
            _ = RunCheckAsync();
    }

    // --- Шаг 1: что не работает -------------------------------------------

    private void OnStep1Next(object sender, RoutedEventArgs e)
    {
        if (Targets.SelectedItem is not string name)
            return;

        _target = ServiceCatalog.All.FirstOrDefault(s => s.Name == name);
        (_probeHost, _probeService) = ResolveProbe(_target);

        Show(2);
    }

    /// <summary>
    /// «Не знаю, включите всё» — мастер не настаивает на выборе.
    /// </summary>
    /// <remarks>
    /// Список из полусотни сервисов способен отпугнуть не хуже одиннадцати
    /// разделов, от которых мастер и должен избавлять. Без цели шаг
    /// результата не проверяет ничего конкретного и говорит об этом прямо.
    /// </remarks>
    private void OnSkipTarget(object sender, RoutedEventArgs e)
    {
        _target = null;
        _probeHost = null;
        _probeService = null;

        Show(2);
    }

    /// <summary>
    /// Имя, на котором можно измерить результат для выбранного сервиса.
    /// </summary>
    /// <remarks>
    /// Берётся первая часть не по адресу: часть по адресу (Telegram и
    /// подобные) проверить пробой на имени нечем — фильтр там смотрит
    /// не на SNI. Внутри части — своё пробное имя, если оно задано
    /// (голос Discord так и устроен, апекс зоны из блокировки выпадает),
    /// иначе первое имя из списка части.
    /// </remarks>
    private static (string? Host, string? Service) ResolveProbe(ServiceDefinition? target)
    {
        if (target is null)
            return (null, null);

        var zapretRoot = ZapretPaths.Discover()?.Root;

        foreach (var part in target.Parts)
        {
            if (part.ByAddress)
                continue;

            if (!string.IsNullOrWhiteSpace(part.Probe))
                return (part.Probe, $"{target.Name} · {part.Name}");

            IReadOnlyList<string> domains;

            try
            {
                domains = HostListReader.Read(part.List, zapretRoot, out _);
            }
            catch (Exception)
            {
                continue;
            }

            if (domains.Count > 0)
                return (domains[0].TrimStart('*', '.'), $"{target.Name} · {part.Name}");
        }

        return (null, target.Name);
    }

    // --- Шаг 2: подписка на VPN --------------------------------------------

    private void OnStep2Back(object sender, RoutedEventArgs e) => Show(1);

    /// <summary>
    /// Без подписки режим переключается на «только десинк».
    /// </summary>
    /// <remarks>
    /// Режим по умолчанию — «Выборочно», и он требует туннель для всего,
    /// что в правилах помечено <c>proxy</c>. Без подписки сборка конфига
    /// отказывает целиком фразой «Подписка не задана», и пробный запуск
    /// на следующем шаге не поднял бы вообще ничего — ни десинка, который
    /// как раз работает без всякой подписки. «Только десинк» — тот же
    /// режим, что предлагает раздел «Главная» на этот самый случай.
    /// </remarks>
    private void OnStep2Skip(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl) && settings.PresetName is not null)
            {
                (settings with { Mode = OperatingMode.DesyncOnly }).Save(AppSettings.DefaultPath);
            }
        }
        catch (Exception)
        {
            // Режим — удобство пробного запуска, а не условие мастера.
            // Не сохранился — шаг всё равно идёт дальше.
        }

        Show(3);
    }

    /// <summary>
    /// Проверяет ссылку и подключает подписку — тот же путь, что в разделе VPN.
    /// </summary>
    /// <remarks>
    /// Ссылка на подписку равносильна паролю: поле — <see cref="PasswordBox"/>,
    /// значение нигде не печатается и не остаётся в журнале, а после
    /// использования очищается.
    /// </remarks>
    private async void OnStep2Next(object sender, RoutedEventArgs e)
    {
        var raw = SubLink.Password.Trim();

        if (raw.Length == 0)
        {
            Step2Status.Text = "Ссылку никто не вставил — нажмите «Пропустить», если подписки нет.";
            return;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            Step2Status.Text = "Это не похоже на ссылку.";
            return;
        }

        parsed = SubscriptionClient.Unwrap(parsed);

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            Step2Status.Text = "Это не похоже на ссылку подписки: нужна http, https "
                + "либо обёртка happ, clash или sn.";

            return;
        }

        Step2Next.IsEnabled = false;
        Step2Status.Text = "Загружаю список серверов…";

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

            int usable = info.Servers.Count(s => s.IsSupportedBySingBox);
            Step2Status.Text = $"Подписка подключена: серверов {usable}.";

            Show(3);
        }
        catch (Exception ex)
        {
            Step2Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }
        finally
        {
            Step2Next.IsEnabled = true;
        }
    }

    // --- Шаг 3: пробный запуск ---------------------------------------------

    private void OnStep3Back(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(2);
    }

    private void PrepareTrial()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        var planned = new List<string>();

        if (settings.NeedsDesync)
            planned.Add("десинк — " + settings.DescribePreset());

        if (settings.NeedsProxy)
            planned.Add("VPN");

        Step3Detail.Text = planned.Count == 0
            ? "Запускать пока нечего: ни пресет, ни подписка не заданы. Можно вернуться шагом назад "
              + "или просто посмотреть результат — там же будет сказано, что чинить."
            : "Поднимутся: " + string.Join(" и ", planned) + ". "
              + "Пара секунд на десинк, до полуминуты на туннель.";

        Step3Start.IsEnabled = planned.Count > 0;
        Step3Next.IsEnabled = true;
        Step3Status.Text = string.Empty;
    }

    /// <summary>
    /// Поднимает движки тем же путём, что кнопка «Запустить» на «Главной».
    /// </summary>
    /// <remarks>
    /// Запускается только по нажатию, не само собой при входе на шаг: решать,
    /// когда обрывать текущий прямой трафик ради обхода, — дело человека,
    /// даже во время мастера.
    /// </remarks>
    private async void OnStep3Start(object sender, RoutedEventArgs e)
    {
        Step3Start.IsEnabled = false;
        Step3Status.Text = "Собираю конфиг…";

        var outcome = await EngineControl.StartAsync(CancellationToken.None);

        if (!outcome.Ok)
        {
            Step3Status.Text = outcome.Message;
            Step3Start.IsEnabled = true;

            return;
        }

        _startedAt = DateTimeOffset.Now;
        _poll.Start();
        UpdateTrial();
    }

    private void UpdateTrial()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        if (!running)
        {
            var seconds = _startedAt is { } since ? (int)(DateTimeOffset.Now - since).TotalSeconds : 0;
            Step3Status.Text = $"Поднимается… {seconds} с";

            return;
        }

        _poll.Stop();

        bool healthy = state!.Services.All(s => s.Health == ServiceHealth.Healthy);

        Step3Status.Text = healthy
            ? "Движки работают."
            : "Движки запущены, но не все службы в порядке — подробности на «Главной». "
              + "Можно идти дальше: проверка на следующем шаге покажет, помогло ли.";
    }

    private void OnStep3Next(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(4);
    }

    // --- Шаг 4: результат ----------------------------------------------------

    private void OnStep4Back(object sender, RoutedEventArgs e) => Show(3);

    private void OnStep4Recheck(object sender, RoutedEventArgs e) => _ = RunCheckAsync();

    /// <summary>
    /// Меряет ровно то же имя, что выбор сервиса на первом шаге.
    /// </summary>
    /// <remarks>
    /// Через тот же <see cref="BlockCheck"/>, что и полная проверка блокировок:
    /// два разных измерения одного и того же имени, дай они разный ответ,
    /// подорвали бы доверие к обоим.
    /// </remarks>
    private async Task RunCheckAsync()
    {
        if (_probeHost is null)
        {
            ResultTitle.Text = _target is null ? "Проверять нечего" : "Эту часть проверить нечем";

            ResultBody.Text = _target is null
                ? "Вы не выбирали, что не работает. Откройте то, что чинили, и посмотрите глазами — "
                  + "либо загляните в «Проверка блокировок», там сорок с лишним имён разом."
                : $"«{_target.Name}» определяется по адресу, а не по имени сайта — измерить пробой "
                  + "нечем. Устроен так Telegram и похожие: клиент ходит по IP, минуя DNS. "
                  + "Проверить его можно в «Наблюдении», по тому, идут ли соединения.";

            ResultVerdict.Visibility = Visibility.Collapsed;
            return;
        }

        ResultTitle.Text = $"Проверяю «{_probeService}»…";
        ResultBody.Text = string.Empty;
        ResultVerdict.Visibility = Visibility.Collapsed;

        RuleEngine? engine = null;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            engine = RuleSetLoader.LoadLayered(settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            var state = SupervisorState.Load(SupervisorState.DefaultPath);
            bool tunnelInUse = state is not null && state.IsSupervisorAlive() && settings.NeedsProxy;

            bool tunnelled = tunnelInUse && Proxied(engine, _probeHost);

            var report = await BlockCheck.CheckAsync(_probeHost, _probeService, CancellationToken.None, tunnelled);

            ShowResult(report);
        }
        catch (Exception ex)
        {
            ResultTitle.Text = "Проверка сорвалась";
            ResultBody.Text = ex.GetBaseException().Message;
        }
    }

    private static bool Proxied(RuleEngine engine, string host) =>
        engine.Evaluate(new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemoteAddress = null,
            RemotePort = 443,
            Hostname = host,
        }).Mode == RoutingMode.Proxy;

    private void ShowResult(TargetReport report)
    {
        ResultTitle.Text = report.Kind == BlockKind.None
            ? $"«{_probeService}» открывается"
            : $"«{_probeService}» не открывается";

        var key = report.Kind switch
        {
            BlockKind.None => "Accent",
            BlockKind.GeoBlock => "Warn",
            _ => "Danger",
        };

        ResultVerdict.Text = report.Describe();
        ResultVerdict.Foreground = (Brush)FindResource(key);
        ResultVerdict.Visibility = Visibility.Visible;

        ResultBody.Text = report.Kind == BlockKind.None
            ? "Похоже, обход справился. Если что-то другое всё ещё не открывается — "
              + "«Проверка блокировок» покажет остальное разом."
            : (report.Why is { Length: > 0 } why ? why + " " : string.Empty)
              + "Чем лечится: " + report.Remedy()
              + ". Это настраивается в «Маршрутах» — выбором маршрута для этого сервиса.";
    }

    // --- Завершение -----------------------------------------------------------

    private void OnSkipAll(object sender, RoutedEventArgs e) => Finish();

    private void OnFinish(object sender, RoutedEventArgs e) => Finish();

    /// <summary>
    /// Отмечает мастер закрытым и уступает место «Главной».
    /// </summary>
    /// <remarks>
    /// Ставится и при пропуске, не только при полном прохождении: «пропустить»
    /// тоже решение человека, и показывать мастер второй раз значило бы
    /// не уважать его.
    /// </remarks>
    private void Finish()
    {
        _poll.Stop();

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            (settings with { OnboardingDone = true }).Save(AppSettings.DefaultPath);
        }
        catch (Exception)
        {
            // Не записалось — мастер покажется снова при следующем запуске.
            // Не идеально, но не повод держать человека здесь силой.
        }

        Completed?.Invoke(this, EventArgs.Empty);
    }
}
