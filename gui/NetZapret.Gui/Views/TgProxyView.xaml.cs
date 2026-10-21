using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Вкладка «TG Proxy»: прокси для Telegram Desktop (<see cref="TgWsProxy"/>).
/// </summary>
/// <remarks>
/// Выключатель пишет настройку и предлагает перезапуск движков: прокси — служба
/// надзора, и поднимается вместе с ними. Секрет заводится при первом включении
/// и дальше не меняется — иначе Telegram пришлось бы подключать заново.
/// </remarks>
public partial class TgProxyView : UserControl
{
    public TgProxyView()
    {
        InitializeComponent();
        Loaded += (_, _) => ShowState();

        // Вкладку сменили — мерцание незачем: окно перерисовывалось бы ради невидимой точки.
        Unloaded += (_, _) => Pulse(false);
    }

    private void ShowState()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var port = settings.TelegramProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Enabled.IsChecked = settings.TelegramProxy;

        PortText.Text = port;
        AddressText.Text = "127.0.0.1:" + port;
        var link = Link();
        LinkText.Text = link ?? "появится при первом включении";
        LinkText.ToolTip = link;

        bool ready = settings.TelegramProxy && TgWsProxy.IsSecret(settings.TelegramProxySecret);
        ConnectButton.IsEnabled = ready;
        CopyButton.IsEnabled = ready;
        LinkCopy.IsEnabled = ready;

        ShowLook(Describe(settings));

        Always.IsChecked = settings.TelegramProxyAlways;
        Always.IsEnabled = settings.TelegramProxy;
        AlwaysWord.Text = settings.TelegramProxyAlways ? "вкл." : "выкл.";

        DirectMain.IsChecked = settings.TelegramProxyDirect;
        DirectMainWord.Text = settings.TelegramProxyDirect ? "вкл." : "выкл.";
        DirectOther.IsChecked = settings.TelegramProxyDirectOther;
        DirectOtherWord.Text = settings.TelegramProxyDirectOther ? "вкл." : "выкл.";

        _showingRoutes = true;
        var timeout = TgWsProxy.CfTimeout(settings.TelegramProxyCfTimeout).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var items = CfTimeoutChoice.Items.OfType<ComboBoxItem>().ToList();
        CfTimeoutChoice.SelectedItem = items.FirstOrDefault(i => i.Tag as string == timeout) ?? items[^1];
        _showingRoutes = false;

        // Без десинка прокси молча не работает: его соединения к Telegram
        // и доменам за Cloudflare режет DPI (жалоба пользователя 11.10 —
        // «включаю только его, Telegram не работает, нужен ещё десинк»).
        // Поднимать десинк ради него самим владелец не стал — говорим прямо.
        DesyncWarning.Visibility = settings.TelegramProxy && !settings.NeedsDesync
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private enum Tone { Good, Wait, Bad, Off }

    /// <summary>
    /// Что с прокси: плашка (<paramref name="Word"/>), строка под заголовком
    /// (<paramref name="Line"/>) и плитка «Статус подключения» (<paramref name="Health"/>,
    /// <paramref name="Note"/>).
    /// </summary>
    private sealed record Look(Tone Tone, string Word, string Line, string Health, string Note);

    /// <summary>Что с прокси сейчас — по настройке, наличию файла и состоянию надзора.</summary>
    /// <remarks>
    /// «Работает» — когда прокси слушает порт (так его проверяет и надзор,
    /// <see cref="TgWsProxyService"/>), а не «трафик проходит», как в макете:
    /// доходит ли Telegram, отсюда не видно.
    /// </remarks>
    private static Look Describe(AppSettings settings)
    {
        string address = $"127.0.0.1:{settings.TelegramProxyPort}";

        if (!File.Exists(TgWsProxy.Executable()))
        {
            return new Look(Tone.Bad, "Нет файла", $"Прокси нет в поставке: {TgWsProxy.Executable()}.",
                "Нет файла прокси", "Его нет в папке программы — переустановите её.");
        }

        if (!settings.TelegramProxy)
        {
            return new Look(Tone.Off, "Выключен", "Включите — и он поднимется со следующим запуском движков.",
                "Выключен", "Включите прокси выключателем выше.");
        }

        // Держит окно само (TgWsProxyKeeper) — движки тут ни при чём.
        if (settings.TelegramProxyAlways)
        {
            return TgWsProxyKeeper.Running
                ? new Look(Tone.Good, "Работает", $"{address} — работает без движков, пока открыта программа.",
                    "Принимает соединения", "Держит программа — и без движков, пока она открыта.")
                : new Look(Tone.Wait, "Не запущен",
                    "Включён без движков, но сейчас не запущен: ждёт, пока движки, запущенные до этой настройки, отпустят порт, — перезапустите их.",
                    "Ждёт порт", "Порт держат движки, запущенные до этой настройки.");
        }

        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
        {
            return new Look(Tone.Off, "Ждёт движков", "Включён; поднимется вместе с движками — сейчас они остановлены.",
                "Ждёт движков", "Поднимется вместе с ними.");
        }

        var service = state.Services.FirstOrDefault(s => s.Name == TgWsProxyService.ServiceName);

        if (service is null)
        {
            return new Look(Tone.Wait, "Нужен перезапуск", "Включён, но движки запущены ещё без него — перезапустите их.",
                "Ждёт перезапуска движков", "Они запущены ещё без прокси.");
        }

        string status = EngineHealth.Status(service);
        string line = $"{address} — {status}.";

        return service.Health switch
        {
            ServiceHealth.Healthy => new Look(Tone.Good, "Работает", line, "Принимает соединения", "Поднят вместе с движками."),
            ServiceHealth.Degraded => new Look(Tone.Wait, "Не отвечает", line, "Не отвечает", "Процесс жив, порт не слушает."),
            ServiceHealth.Stopped => new Look(Tone.Off, "Остановлен", line, "Остановлен", "Надзор его не держит."),
            _ => new Look(Tone.Bad, "Не работает", line, "Не работает", "Надзор не смог его поднять — причина в строке выше."),
        };
    }

    /// <summary>Плашка, строка состояния и плитка — цветами темы, чтобы смена темы их перекрашивала.</summary>
    private void ShowLook(Look look)
    {
        string brush = look.Tone switch
        {
            Tone.Good => "Accent",
            Tone.Wait => "Warn",
            Tone.Bad => "Danger",
            _ => "Muted",
        };

        PillBack.SetResourceReference(Border.BackgroundProperty, brush);
        PillFrame.SetResourceReference(Border.BorderBrushProperty, brush);
        PillGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
        PillWord.SetResourceReference(TextBlock.ForegroundProperty, brush);
        HealthDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        HealthText.SetResourceReference(TextBlock.ForegroundProperty, brush);

        PillGlyph.Text = look.Tone switch
        {
            Tone.Good => "",
            Tone.Wait => "",
            Tone.Bad => "",
            _ => string.Empty,
        };
        PillGlyph.Visibility = PillGlyph.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PillWord.Text = look.Word;

        StateText.Text = look.Line;
        HealthText.Text = look.Health;
        HealthNote.Text = look.Note;

        // Мерцает живое — работает или ждёт; выключенный и сломанный стоят.
        Pulse(look.Tone is Tone.Good or Tone.Wait);
    }

    private bool _pulsing;

    /// <summary>
    /// Точка статуса мерцает, как точка хода запуска на «Главной» (09.09):
    /// прозрачность от 1 до 0,2 и обратно за 0,8 с.
    /// </summary>
    /// <remarks>
    /// Уже мерцающую не перезапускаем: состояние перечитывается после каждого
    /// щелчка, и мерцание дёргалось бы. 30 кадров — как у движения на «Главной».
    /// </remarks>
    private void Pulse(bool on)
    {
        on &= Motion.Enabled;

        if (on == _pulsing)
            return;

        _pulsing = on;

        if (!on)
        {
            HealthDot.BeginAnimation(OpacityProperty, null);
            HealthDot.Opacity = 1;
            return;
        }

        var blink = new System.Windows.Media.Animation.DoubleAnimation(1, 0.2, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(blink, 30);
        HealthDot.BeginAnimation(OpacityProperty, blink);
    }

    /// <summary>Пояснение «Когда он нужен» — по кнопке шапки.</summary>
    private void OnHow(object sender, RoutedEventArgs e)
    {
        bool open = HowCard.Visibility != Visibility.Visible;

        HowCard.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        HowButton.Content = open ? "Скрыть пояснение" : "Как это работает?";
    }

    private bool? _narrow;

    /// <summary>
    /// Узкое окно (~1000 точек вместе с меню, страница ~680): плитки в одну
    /// колонку, подключение и ручная настройка — одна под другой. Втроём
    /// в ряд подписи плиток ломались бы на каждом слове.
    /// </summary>
    private void OnPageSize(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 820;

        if (narrow == _narrow)
            return;

        _narrow = narrow;

        Tiles.Rows = narrow ? 0 : 1;
        Tiles.Columns = narrow ? 1 : 0;
        HealthTile.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 7, 0);
        AddressTile.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(7, 0, 7, 0);
        TypeTile.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(7, 0, 0, 0);

        Grid.SetColumn(ManualCard, narrow ? 0 : 1);
        Grid.SetRow(ManualCard, narrow ? 1 : 0);
        Grid.SetColumnSpan(ConnectCard, narrow ? 2 : 1);
        Grid.SetColumnSpan(ManualCard, narrow ? 2 : 1);
        ConnectCard.Margin = narrow ? new Thickness(0) : new Thickness(0, 0, 7, 0);
        ManualCard.Margin = narrow ? new Thickness(0, 12, 0, 0) : new Thickness(7, 0, 0, 0);

        // Ссылка с секретом — около 80 знаков: в колонке узкого окна от неё
        // оставалось «tg:/…» (снимок 21.10).
        LinkColumn.Width = narrow ? new GridLength(0) : new GridLength(2.8, GridUnitType.Star);
        LinkLine.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(LinkField, narrow ? 1 : 0);
        Grid.SetColumn(LinkField, narrow ? 0 : 4);
        Grid.SetColumnSpan(LinkField, narrow ? 7 : 1);
        LinkField.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(0);
    }

    /// <summary>
    /// «Держать включённым без движков»: прокси переезжает из надзора движков
    /// в само окно (<see cref="TgWsProxyKeeper"/>) или обратно.
    /// </summary>
    private void OnAlways(object sender, RoutedEventArgs e)
    {
        bool on = Always.IsChecked == true;

        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { TelegramProxyAlways = on }).Save(AppSettings.DefaultPath);

            // Окно поднимает свой прокси сразу, если порт свободен; а надзор
            // движков держит или отпускает свой только с перезапуском.
            EngineControl.SyncTelegramProxy();

            Status.Text = on
                ? "Прокси будет работать и без движков. Если движки запущены, перезапустите их — тогда прокси перейдёт к программе."
                : "Прокси снова поднимается вместе с движками — перезапустите их.";

            if (EngineControl.IsRunning)
                (Window.GetWindow(this) as MainWindow)?.OfferRestart(on ? "Прокси Telegram — без движков" : "Прокси Telegram — с движками");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }

        ShowState();

        // Прокси окна поднимается в пуле — строка состояния догонит его через секунду.
        _ = Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(1500);
            ShowLook(Describe(AppSettings.Load(AppSettings.DefaultPath)));
        });
    }

    /// <summary>Выбор показывается, а не делается — события выбора тогда не в счёт.</summary>
    private bool _showingRoutes;

    private void OnDirectMain(object sender, RoutedEventArgs e)
    {
        bool on = DirectMain.IsChecked == true;
        SaveRoutes(s => s with { TelegramProxyDirect = on },
            on ? "DC2 и DC4 — напрямую." : "DC2 и DC4 — только через Cloudflare.");
    }

    private void OnDirectOther(object sender, RoutedEventArgs e)
    {
        bool on = DirectOther.IsChecked == true;
        SaveRoutes(s => s with { TelegramProxyDirectOther = on },
            on ? "DC1, DC3 и DC5 — напрямую, на пробу." : "DC1, DC3 и DC5 — через Cloudflare.");
    }

    private void OnCfTimeout(object sender, SelectionChangedEventArgs e)
    {
        if (_showingRoutes || CfTimeoutChoice.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !int.TryParse(tag, out var seconds))
        {
            return;
        }

        if (TgWsProxy.CfTimeout(AppSettings.Load(AppSettings.DefaultPath).TelegramProxyCfTimeout) == seconds)
            return;

        SaveRoutes(s => s with { TelegramProxyCfTimeout = seconds }, $"Домен Cloudflare прокси ждёт {seconds} с.");
    }

    /// <summary>
    /// Пишет пути и применяет их: прокси окна перезапускается сразу, прокси
    /// движков — с их перезапуском.
    /// </summary>
    /// <remarks>
    /// Ключи прокси читает только при запуске: без перезапуска сменённый путь
    /// не действует, а вкладка говорила бы о том, чего нет.
    /// </remarks>
    private void SaveRoutes(Func<AppSettings, AppSettings> change, string done)
    {
        try
        {
            var settings = change(AppSettings.Load(AppSettings.DefaultPath));
            settings.Save(AppSettings.DefaultPath);

            bool byWindow = TgWsProxyKeeper.Running;
            bool byEngines = !byWindow && settings.TelegramProxyWithEngines && EngineControl.IsRunning;

            // Остановка ждёт ухода процесса до 10 с — не на потоке окна.
            if (byWindow)
                _ = Task.Run(() => TgWsProxyKeeper.Restart(settings, enginesHoldIt: false));

            Status.Text = done + (byWindow
                ? " Прокси перезапущен."
                : byEngines ? " Применится после перезапуска движков." : string.Empty);

            if (byEngines)
                (Window.GetWindow(this) as MainWindow)?.OfferRestart("Прокси Telegram — пути");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }

        ShowState();
    }

    private void OnEnabled(object sender, RoutedEventArgs e)
    {
        bool on = Enabled.IsChecked == true;

        try
        {
            TgWsProxy.Switch(AppSettings.Load(AppSettings.DefaultPath), on).Save(AppSettings.DefaultPath);

            // Прокси без движков держит окно — включается и гасится сразу.
            EngineControl.SyncTelegramProxy();

            Status.Text = on
                ? "Прокси включён: поднимется после перезапуска движков. Потом — «Подключить Telegram»."
                : "Прокси выключен. Выключите его и в Telegram («Тип подключения» → «Без прокси»), иначе он не подключится.";

            (Window.GetWindow(this) as MainWindow)?.OfferRestart(on ? "Прокси Telegram включён" : "Прокси Telegram выключен");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }

        ShowState();
    }

    private string? Link()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        return TgWsProxy.IsSecret(settings.TelegramProxySecret)
            ? TgWsProxy.Link(settings.TelegramProxyPort, settings.TelegramProxySecret!)
            : null;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        if (Link() is not { } link)
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = link, UseShellExecute = true });
            Status.Text = "Ссылка отправлена Telegram — подтвердите подключение в нём.";
        }
        catch (Exception)
        {
            Status.Text = "Telegram не открыл ссылку — скопируйте её и нажмите в «Избранном».";
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (Link() is { } link)
            Copy(link, "Ссылка скопирована.");
    }

    private void OnCopyServer(object sender, RoutedEventArgs e) => Copy("127.0.0.1", "Сервер скопирован.");

    private void OnCopyPort(object sender, RoutedEventArgs e) => Copy(PortText.Text, "Порт скопирован.");

    private void Copy(string text, string done)
    {
        try
        {
            Clipboard.SetText(text);
            Status.Text = done;
        }
        catch (Exception ex)
        {
            Status.Text = "Не скопировалось: " + ex.GetBaseException().Message;
        }
    }
}
