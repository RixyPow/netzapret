using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Сценарий первого запуска: три шага от подписки до результата.
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
/// Шага «что у вас не работает» здесь больше нет — он был первым в исходном
/// варианте и просил выбрать сервис из полусотни, ничего не решая: список
/// такой же длины, как меню слева, от которого мастер и должен избавлять.
/// Результат меряет сеть в целом, а не одно выбранное имя.
/// </para>
/// <para>
/// Показывается вместо «Главной» ровно один раз, пока в настройках не стоит
/// <see cref="AppSettings.OnboardingDone"/>. Не окно, а обычный раздел:
/// отдельное модальное окно нельзя было бы прервать, вернувшись позже
/// к любому другому разделу, а мастер должен позволять это в любой момент —
/// он не единственный путь в программу, а первое, что видно. Повторно его
/// можно открыть из «Ещё».
/// </para>
/// </remarks>
public partial class OnboardingView : UserControl
{
    private const int LastStep = 3;

    /// <summary>Мастер закрыт — завершением или пропуском.</summary>
    public event EventHandler? Completed;

    private int _step = 1;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    private DateTimeOffset? _startedAt;

    public OnboardingView()
    {
        InitializeComponent();

        _poll.Tick += (_, _) => UpdateTrial();

        Unloaded += (_, _) => _poll.Stop();

        Show(1);
    }

    private void Show(int step)
    {
        _step = step;

        Step1.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        StepLabel.Text = $"Шаг {step} из {LastStep}";

        // Подпись меняется по шагу: на последнем «пропустить» пропускать
        // уже нечего, и кнопка честно называется «Готово», а не молчит
        // о том, что делает то же самое другими словами.
        HeaderSkip.Content = step == LastStep ? "Готово" : "Пропустить";

        if (step == 2)
            PrepareTrial();

        if (step == 3)
            _ = RunCheckAsync();
    }

    /// <summary>
    /// Пропускает текущий шаг, а не весь мастер.
    /// </summary>
    /// <remarks>
    /// Раньше эта кнопка стояла одна на весь мастер и закрывала его целиком
    /// с любого шага — человек, нажавший её на первом шаге случайно, терял
    /// пробный запуск и результат, хотя хотел пропустить только подписку.
    /// Теперь она делает ровно то же, что назвал бы следующий шаг сам —
    /// на подписке это «десинк без VPN», на пробном запуске — «не запускать
    /// сейчас», а на последнем шаге пропускать нечего, и кнопка завершает.
    /// </remarks>
    private void OnHeaderSkip(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 1:
                SkipSubscription();
                break;

            case 2:
                _poll.Stop();
                Show(3);
                break;

            default:
                Finish();
                break;
        }
    }

    // --- Шаг 1: подписка на VPN --------------------------------------------

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
    private void SkipSubscription()
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

        Show(2);
    }

    /// <summary>
    /// Проверяет ссылку и подключает подписку — тот же путь, что в разделе VPN.
    /// </summary>
    /// <remarks>
    /// Ссылка на подписку равносильна паролю: поле — <see cref="PasswordBox"/>,
    /// значение нигде не печатается и не остаётся в журнале, а после
    /// использования очищается.
    /// </remarks>
    private async void OnAddSubscription(object sender, RoutedEventArgs e)
    {
        var raw = SubLink.Password.Trim();

        if (raw.Length == 0)
        {
            Step1Status.Text = "Ссылку никто не вставил — нажмите «Пропустить», если подписки нет.";
            return;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            Step1Status.Text = "Это не похоже на ссылку.";
            return;
        }

        parsed = SubscriptionClient.Unwrap(parsed);

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            Step1Status.Text = "Это не похоже на ссылку подписки: нужна http, https "
                + "либо обёртка happ, clash или sn.";

            return;
        }

        Step1Next.IsEnabled = false;
        Step1Status.Text = "Загружаю список серверов…";

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
            Step1Status.Text = $"Подписка подключена: серверов {usable}.";

            Show(2);
        }
        catch (Exception ex)
        {
            Step1Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }
        finally
        {
            Step1Next.IsEnabled = true;
        }
    }

    // --- Шаг 2: пробный запуск ---------------------------------------------

    private void OnStep2Back(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(1);
    }

    private void PrepareTrial()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        var planned = new List<string>();

        if (settings.NeedsDesync)
            planned.Add("десинк — " + settings.DescribePreset());

        if (settings.NeedsProxy)
            planned.Add("VPN");

        Step2Detail.Text = planned.Count == 0
            ? "Запускать пока нечего: ни пресет, ни подписка не заданы. Можно вернуться шагом назад "
              + "или просто посмотреть результат — там же будет сказано, что чинить."
            : "Поднимутся: " + string.Join(" и ", planned) + ". "
              + "Пара секунд на десинк, до полуминуты на туннель.";

        Step2Start.IsEnabled = planned.Count > 0;
        Step2Status.Text = "Ничего ещё не запускалось.";
    }

    /// <summary>
    /// Поднимает движки тем же путём, что кнопка «Запустить» на «Главной».
    /// </summary>
    /// <remarks>
    /// Запускается только по нажатию, не само собой при входе на шаг: решать,
    /// когда обрывать текущий прямой трафик ради обхода, — дело человека,
    /// даже во время мастера.
    /// </remarks>
    private async void OnStep2Start(object sender, RoutedEventArgs e)
    {
        Step2Start.IsEnabled = false;
        Step2Status.Text = "Собираю конфиг…";

        var outcome = await EngineControl.StartAsync(CancellationToken.None);

        if (!outcome.Ok)
        {
            Step2Status.Text = outcome.Message;
            Step2Start.IsEnabled = true;

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
            Step2Status.Text = $"Поднимается… {seconds} с";

            return;
        }

        _poll.Stop();

        bool healthy = state!.Services.All(s => s.Health == ServiceHealth.Healthy);

        Step2Status.Text = healthy
            ? "Движки работают."
            : "Движки запущены, но не все службы в порядке — подробности на «Главной». "
              + "Можно идти дальше: проверка на следующем шаге покажет, помогло ли.";
    }

    private void OnStep2Next(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(3);
    }

    // --- Шаг 3: результат ----------------------------------------------------

    private void OnStep3Back(object sender, RoutedEventArgs e) => Show(2);

    private void OnStep3Recheck(object sender, RoutedEventArgs e) => _ = RunCheckAsync();

    /// <summary>
    /// Меряет саму сеть, а не одно выбранное имя.
    /// </summary>
    /// <remarks>
    /// Тот же замер, что открывает полную «Проверку блокировок» в консоли
    /// и в окне: TLS, HTTP и DoH решают вердикт, ICMP и IPv6 — свойство сети,
    /// а не след вмешательства, и на него не влияют. Без выбранной на первом
    /// шаге цели (которого в мастере больше нет) это единственный результат,
    /// который можно показать, не выдумывая его.
    /// </remarks>
    private async Task RunCheckAsync()
    {
        ResultTitle.Text = "Проверяю сеть…";
        ResultBody.Text = string.Empty;
        ResultVerdict.Visibility = Visibility.Collapsed;

        try
        {
            var baseline = await BlockCheck.MeasureBaselineAsync(CancellationToken.None);
            ShowResult(baseline);
        }
        catch (Exception ex)
        {
            ResultTitle.Text = "Проверка сорвалась";
            ResultBody.Text = ex.GetBaseException().Message;
        }
    }

    private void ShowResult(NetworkBaseline baseline)
    {
        bool ok = baseline.Tls && baseline.Http;

        ResultTitle.Text = ok ? "Сеть отвечает" : "Сеть отвечает не полностью";

        ResultVerdict.Text = string.Join(" · ", new[]
        {
            $"TLS {(baseline.Tls ? "доступен" : "недоступен")}",
            $"HTTP {(baseline.Http ? "доступен" : "недоступен")}",
            $"DoH {(baseline.Doh ? "доступен" : "недоступен")}",
        });

        ResultVerdict.Foreground = (Brush)FindResource(ok ? "Accent" : "Danger");
        ResultVerdict.Visibility = Visibility.Visible;

        ResultBody.Text = ok
            ? "Обход настроен и сеть под ним отвечает. Что именно теперь открывается — "
              + "«Проверка блокировок» покажет разом на сорока с лишним именах. Если что-то "
              + "конкретное всё ещё не работает — «Маршруты» переключают способ для него отдельно."
            : "TLS или HTTP не отвечают даже так — движки могли не подняться. Загляните "
              + "на «Главную»: там видно, что именно не запустилось, и можно попробовать снова.";
    }

    // --- Завершение -----------------------------------------------------------

    private void OnFinish(object sender, RoutedEventArgs e) => Finish();

    /// <summary>
    /// Отмечает мастер закрытым и уступает место «Главной».
    /// </summary>
    /// <remarks>
    /// Ставится и при пропуске, не только при полном прохождении: «пропустить»
    /// тоже решение человека, и показывать мастер второй раз значило бы
    /// не уважать его. Повторно открыть его можно из «Ещё».
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
