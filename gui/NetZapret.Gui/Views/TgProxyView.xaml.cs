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
