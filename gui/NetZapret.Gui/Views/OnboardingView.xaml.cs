using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Themes;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Сценарий первого запуска: знакомство, оформление, подписка, пробный запуск, результат.
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
/// 28.09 по просьбе владельца мастер вырос с трёх шагов до пяти: первым —
/// живая схема того, что программа делает (точки бегут двумя дорогами:
/// сквозь ТСПУ десинком и через туннель), вторым — выбор темы, последним —
/// телеграм-канал. Схема рисуется кодом, а не картинкой: она берёт цвета
/// темы и перекрашивается вместе с ней.
/// </para>
/// <para>
/// Шага «что у вас не работает» здесь нет — он был первым в исходном
/// варианте и просил выбрать сервис из полусотни, ничего не решая.
/// Результат меряет сеть в целом, а не одно выбранное имя.
/// </para>
/// <para>
/// Показывается вместо «Главной» ровно один раз, пока в настройках не стоит
/// <see cref="AppSettings.OnboardingDone"/>. Не окно, а обычный раздел:
/// мастер должен позволять уйти в любой другой раздел в любой момент.
/// Повторно его можно открыть из «Ещё».
/// </para>
/// </remarks>
public partial class OnboardingView : UserControl
{
    private const int LastStep = 5;

    /// <summary>Мастер закрыт — завершением или пропуском.</summary>
    public event EventHandler? Completed;

    private int _step = 1;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    private DateTimeOffset? _startedAt;

    public OnboardingView()
    {
        InitializeComponent();

        _poll.Tick += (_, _) => UpdateTrial();

        Unloaded += (_, _) =>
        {
            _poll.Stop();
            Hero.Children.Clear();
        };

        for (int i = 0; i < LastStep; i++)
        {
            var segment = new Border { CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, i < LastStep - 1 ? 6 : 0, 0) };
            Progress.Children.Add(segment);
        }

        Loaded += (_, _) => Show(_step);
    }

    private StackPanel Panel(int step) => step switch
    {
        1 => Step1,
        2 => Step2,
        3 => Step3,
        4 => Step4,
        _ => Step5,
    };

    private void Show(int step)
    {
        _step = step;

        for (int i = 1; i <= LastStep; i++)
            Panel(i).Visibility = i == step ? Visibility.Visible : Visibility.Collapsed;

        for (int i = 0; i < LastStep; i++)
        {
            ((Border)Progress.Children[i]).SetResourceReference(
                Border.BackgroundProperty, i < step ? "Accent" : "Border");
        }

        StepLabel.Text = $"Шаг {step} из {LastStep}";

        // Подпись меняется по шагу: на последнем «пропустить» пропускать
        // уже нечего, и кнопка честно называется «Готово».
        HeaderSkip.Content = step == LastStep ? "Готово" : "Пропустить";

        Arrive(Panel(step));

        if (step == 1)
            StartHero();
        else
            Hero.Children.Clear();

        if (step == 2)
            ShowThemes();

        if (step == 4)
            PrepareTrial();

        if (step == 5)
            _ = RunCheckAsync();
    }

    /// <summary>
    /// Пропускает текущий шаг, а не весь мастер.
    /// </summary>
    /// <remarks>
    /// Раньше эта кнопка закрывала мастер целиком с любого шага — человек,
    /// нажавший её случайно, терял пробный запуск и результат. Теперь она
    /// делает то же, что назвал бы следующий шаг сам: на подписке это
    /// «десинк без VPN», на пробном запуске — «не запускать сейчас».
    /// </remarks>
    private void OnHeaderSkip(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case 3:
                SkipSubscription();
                break;

            case 4:
                _poll.Stop();
                Show(5);
                break;

            case LastStep:
                Finish();
                break;

            default:
                Show(_step + 1);
                break;
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(Math.Max(1, _step - 1));
    }

    // --- Движение ------------------------------------------------------------

    /// <summary>Анимации включены в системе — иначе всё появляется сразу.</summary>
    /// <remarks>
    /// Тот, кто выключил анимацию в Windows, сделал это не просто так:
    /// бегущие точки ему мешают, а не радуют.
    /// </remarks>
    private static bool Moving => SystemParameters.ClientAreaAnimation;

    /// <summary>Шаг въезжает справа и проявляется.</summary>
    private static void Arrive(FrameworkElement panel)
    {
        if (!Moving)
            return;

        var shift = new TranslateTransform(18, 0);
        panel.RenderTransform = shift;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var time = TimeSpan.FromMilliseconds(320);

        panel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, time) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(18, 0, time) { EasingFunction = ease });
    }

    // --- Шаг 1: знакомство ---------------------------------------------------

    private void OnIntroNext(object sender, RoutedEventArgs e) => Show(2);

    /// <summary>
    /// Рисует схему: компьютер, ТСПУ, туннель и сайт, и пускает по ним точки.
    /// </summary>
    /// <remarks>
    /// Нижняя дорога — десинк: пакет идёт напрямую сквозь ТСПУ, разрезанный
    /// на две части, и поэтому точек две, одна вплотную за другой. Верхняя —
    /// туннель, в обход ТСПУ через VPN. Цвета — ресурсами темы, не числами:
    /// смена темы на следующем шаге перекрасит схему, если вернуться.
    /// </remarks>
    private void StartHero()
    {
        Hero.Children.Clear();

        var pc = new Point(70, 140);
        var site = new Point(550, 140);
        var vpn = new Point(310, 44);
        const double tspu = 310;

        var direct = new PathGeometry([new PathFigure(pc, [new LineSegment(site, true)], false)]);
        var tunnel = new PathGeometry([new PathFigure(pc,
            [new BezierSegment(new Point(150, 44), new Point(220, 44), vpn, true),
             new BezierSegment(new Point(400, 44), new Point(470, 44), site, true)], false)]);

        Road(direct);
        Road(tunnel);

        // ТСПУ — преграда поперёк прямой дороги.
        var wall = new Rectangle { Width = 8, Height = 74, RadiusX = 3, RadiusY = 3 };
        wall.SetResourceReference(Shape.FillProperty, "Danger");
        Place(wall, tspu - 4, 104);
        Label("ТСПУ", tspu, 184, "Danger");

        Node(pc, "Компьютер", "Muted");
        Node(site, "Сайт", "Accent", pulse: true);
        Node(vpn, "VPN", "Text", radius: 17);

        // Под прямой дорогой, между компьютером и ТСПУ: над ней идёт дуга туннеля.
        Label("десинк: пакет разрезан", 200, 150, "Muted", size: 11);
        Label("туннель: то, что закрыто по стране", 310, 10, "Muted", size: 11);

        if (!Moving)
            return;

        var trip = TimeSpan.FromSeconds(2.6);

        for (int i = 0; i < 3; i++)
        {
            var start = TimeSpan.FromSeconds(i * trip.TotalSeconds / 3);

            // Две половины одного пакета — вторая вплотную за первой.
            Runner(direct, trip, start, "Accent", 5);
            Runner(direct, trip, start + TimeSpan.FromMilliseconds(110), "Accent", 5);

            Runner(tunnel, trip + TimeSpan.FromSeconds(0.6), start + TimeSpan.FromMilliseconds(400), "Text", 6);
        }

        // Карточки под схемой проявляются по очереди.
        int n = 0;

        foreach (UIElement card in Features.Children)
        {
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(420))
            {
                BeginTime = TimeSpan.FromMilliseconds(250 + 170 * n++),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
    }

    private void Road(Geometry geometry)
    {
        var road = new Path { Data = geometry, StrokeThickness = 2, StrokeDashArray = [3, 3] };
        road.SetResourceReference(Shape.StrokeProperty, "Border");
        Hero.Children.Add(road);
    }

    private void Node(Point at, string name, string color, double radius = 24, bool pulse = false)
    {
        if (pulse && Moving)
        {
            var ring = new Ellipse { Width = radius * 2, Height = radius * 2, StrokeThickness = 2, RenderTransformOrigin = new Point(0.5, 0.5) };
            ring.SetResourceReference(Shape.StrokeProperty, color);
            var scale = new ScaleTransform();
            ring.RenderTransform = scale;
            Place(ring, at.X - radius, at.Y - radius);

            var grow = new DoubleAnimation(1, 1.7, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, TimeSpan.FromSeconds(1.6)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        var circle = new Ellipse { Width = radius * 2, Height = radius * 2, StrokeThickness = 2.5 };
        circle.SetResourceReference(Shape.FillProperty, "Surface");
        circle.SetResourceReference(Shape.StrokeProperty, color);
        Place(circle, at.X - radius, at.Y - radius);

        Label(name, at.X, at.Y + radius + 6, "Text", bold: true);
    }

    private void Runner(PathGeometry road, TimeSpan trip, TimeSpan start, string color, double radius)
    {
        var dot = new Ellipse { Width = radius * 2, Height = radius * 2, Opacity = 0 };
        dot.SetResourceReference(Shape.FillProperty, color);

        var move = new TranslateTransform();
        dot.RenderTransform = move;
        Place(dot, -radius, -radius);

        move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimationUsingPath
        {
            PathGeometry = road, Source = PathAnimationSource.X, Duration = trip,
            BeginTime = start, RepeatBehavior = RepeatBehavior.Forever,
        });

        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingPath
        {
            PathGeometry = road, Source = PathAnimationSource.Y, Duration = trip,
            BeginTime = start, RepeatBehavior = RepeatBehavior.Forever,
        });

        // Проявляется в начале пути и гаснет у сайта: иначе точка
        // висела бы в углу, пока не пришло её время.
        var fade = new DoubleAnimationUsingKeyFrames { Duration = trip, BeginTime = start, RepeatBehavior = RepeatBehavior.Forever };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.08)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromPercent(0.88)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
        dot.BeginAnimation(OpacityProperty, fade);
    }

    private void Label(string text, double centerX, double top, string color, double size = 12, bool bold = false)
    {
        var label = new TextBlock { Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        label.SetResourceReference(TextBlock.ForegroundProperty, color);
        label.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(label, centerX - label.DesiredSize.Width / 2, top);
    }

    private void Place(UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        Hero.Children.Add(element);
    }

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

    // --- Шаг 3: подписка на VPN ----------------------------------------------

    private void OnNoSubscription(object sender, RoutedEventArgs e) => SkipSubscription();

    /// <summary>
    /// Без подписки режим переключается на «только десинк».
    /// </summary>
    /// <remarks>
    /// Режим по умолчанию — «Выборочно», и он требует туннель для всего,
    /// что в правилах помечено <c>proxy</c>. Без подписки сборка конфига
    /// отказывает целиком фразой «Подписка не задана», и пробный запуск
    /// не поднял бы вообще ничего — ни десинка, который как раз работает
    /// без всякой подписки.
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
        }

        Show(4);
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
            Step3Status.Text = "Ссылку никто не вставил — нажмите «Нет подписки», если её нет.";
            return;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            Step3Status.Text = "Это не похоже на ссылку.";
            return;
        }

        parsed = SubscriptionClient.Unwrap(parsed);

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            Step3Status.Text = "Это не похоже на ссылку подписки: нужна http, https "
                + "либо обёртка happ, clash или sn.";

            return;
        }

        Step3Next.IsEnabled = false;
        Step3Status.Text = "Загружаю список серверов…";

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
            Step3Status.Text = $"Подписка подключена: серверов {usable}.";

            Show(4);
        }
        catch (Exception ex)
        {
            Step3Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }
        finally
        {
            Step3Next.IsEnabled = true;
        }
    }

    // --- Шаг 4: пробный запуск ---------------------------------------------

    private void PrepareTrial()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        var planned = new List<string>();

        if (settings.NeedsDesync)
            planned.Add("десинк — " + settings.DescribePreset());

        if (settings.NeedsProxy)
            planned.Add("VPN");

        Step4Detail.Text = planned.Count == 0
            ? "Запускать пока нечего: ни пресет, ни подписка не заданы. Можно вернуться шагом назад "
              + "или просто посмотреть результат — там же будет сказано, что чинить."
            : "Поднимутся: " + string.Join(" и ", planned) + ". "
              + "Пара секунд на десинк, до полуминуты на туннель.";

        Step4Start.IsEnabled = planned.Count > 0;
        Step4Status.Text = "Ничего ещё не запускалось.";
    }

    /// <summary>
    /// Поднимает движки тем же путём, что кнопка «Запустить» на «Главной».
    /// </summary>
    /// <remarks>
    /// Только по нажатию, не само при входе на шаг: решать, когда обрывать
    /// текущий трафик ради обхода, — дело человека, даже во время мастера.
    /// </remarks>
    private async void OnStep4Start(object sender, RoutedEventArgs e)
    {
        Step4Start.IsEnabled = false;
        Step4Status.Text = "Собираю конфиг…";

        var outcome = await EngineControl.StartAsync("мастер первого запуска", CancellationToken.None);

        if (!outcome.Ok)
        {
            Step4Status.Text = outcome.Message;
            Step4Start.IsEnabled = true;

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
            Step4Status.Text = $"Поднимается… {seconds} с";

            return;
        }

        _poll.Stop();

        bool healthy = state!.Services.All(s => s.Health == ServiceHealth.Healthy);

        Step4Status.Text = healthy
            ? "Движки работают."
            : "Движки запущены, но не все службы в порядке — подробности на «Главной». "
              + "Можно идти дальше: проверка на следующем шаге покажет, помогло ли.";
    }

    private void OnStep4Next(object sender, RoutedEventArgs e)
    {
        _poll.Stop();
        Show(5);
    }

    // --- Шаг 5: результат ----------------------------------------------------

    private void OnRecheck(object sender, RoutedEventArgs e) => _ = RunCheckAsync();

    /// <summary>
    /// Меряет саму сеть, а не одно выбранное имя.
    /// </summary>
    /// <remarks>
    /// Тот же замер, что открывает полную «Проверку блокировок»: TLS, HTTP
    /// и DoH решают вердикт, ICMP и IPv6 — свойство сети, а не след
    /// вмешательства, и на него не влияют.
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

        ResultVerdict.SetResourceReference(TextBlock.ForegroundProperty, ok ? "Accent" : "Danger");
        ResultVerdict.Visibility = Visibility.Visible;

        ResultBody.Text = ok
            ? "Обход настроен и сеть под ним отвечает. Что именно теперь открывается — "
              + "«Проверка блокировок» покажет разом на сотне с лишним имён. Если что-то "
              + "конкретное всё ещё не работает — «Маршруты» переключают способ для него отдельно."
            : "TLS или HTTP не отвечают даже так — движки могли не подняться. Загляните "
              + "на «Главную»: там видно, что именно не запустилось, и можно попробовать снова.";
    }

    private void OnTelegram(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = About.Telegram, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ResultBody.Text = "Не удалось открыть канал: " + ex.GetBaseException().Message;
        }
    }

    // --- Завершение -----------------------------------------------------------

    private void OnFinish(object sender, RoutedEventArgs e) => Finish();

    /// <summary>
    /// Отмечает мастер закрытым и уступает место «Главной».
    /// </summary>
    /// <remarks>
    /// Ставится и при пропуске: «пропустить» тоже решение человека,
    /// и показывать мастер второй раз значило бы не уважать его.
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
        }

        Completed?.Invoke(this, EventArgs.Empty);
    }
}
