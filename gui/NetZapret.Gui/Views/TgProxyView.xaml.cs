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
    }

    private void ShowState()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        Enabled.IsChecked = settings.TelegramProxy;
        StateWord.Text = settings.TelegramProxy ? "вкл." : "выкл.";

        PortText.Text = settings.TelegramProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        SecretText.Text = TgWsProxy.IsSecret(settings.TelegramProxySecret)
            ? "dd" + settings.TelegramProxySecret
            : "появится при первом включении";

        bool ready = settings.TelegramProxy && TgWsProxy.IsSecret(settings.TelegramProxySecret);
        ConnectButton.IsEnabled = ready;
        CopyButton.IsEnabled = ready;

        StateText.Text = Describe(settings);

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

    /// <summary>Что с прокси сейчас — по настройке, наличию файла и состоянию надзора.</summary>
    private static string Describe(AppSettings settings)
    {
        if (!File.Exists(TgWsProxy.Executable()))
            return $"Прокси нет в поставке: {TgWsProxy.Executable()}.";

        if (!settings.TelegramProxy)
            return "Выключен. Включите — и он поднимется со следующим запуском движков.";

        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        // Держит окно само (TgWsProxyKeeper) — движки тут ни при чём.
        if (settings.TelegramProxyAlways)
        {
            return TgWsProxyKeeper.Running
                ? $"127.0.0.1:{settings.TelegramProxyPort} — работает без движков, пока открыта программа."
                : "Включён без движков, но сейчас не запущен: ждёт, пока движки, запущенные до этой настройки, отпустят порт, — перезапустите их.";
        }

        if (state is null || !state.IsSupervisorAlive())
            return "Включён; поднимется вместе с движками — сейчас они остановлены.";

        var service = state.Services.FirstOrDefault(s => s.Name == TgWsProxyService.ServiceName);

        return service is null
            ? "Включён, но движки запущены ещё без него — перезапустите их."
            : $"127.0.0.1:{settings.TelegramProxyPort} — {EngineHealth.Status(service)}.";
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
            StateText.Text = Describe(AppSettings.Load(AppSettings.DefaultPath));
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
        if (Link() is not { } link)
            return;

        try
        {
            Clipboard.SetText(link);
            Status.Text = "Ссылка скопирована.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не скопировалось: " + ex.GetBaseException().Message;
        }
    }
}
