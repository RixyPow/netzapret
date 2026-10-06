using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Core.Updates;

namespace NetZapret.Gui.Views;

/// <summary>
/// Версия, проверка и установка обновления — на «Главной».
/// </summary>
/// <remarks>
/// Перенесено из «Ещё» 23.09 по просьбе владельца. Код тот же; разница
/// в том, что карточка «Вышла новая версия» больше не уводит в другой
/// раздел, а зовёт установку здесь же — <see cref="Install"/>.
/// </remarks>
public partial class UpdatePanel : UserControl
{
    private CancellationTokenSource? _work;

    /// <summary>Найденное обновление; <c>null</c> — ставить нечего.</summary>
    private ReleaseInfo? _release;

    public UpdatePanel()
    {
        InitializeComponent();

        Loaded += (_, _) => ShowVersion(AppSettings.Load(AppSettings.DefaultPath));
        Unloaded += (_, _) => _work?.Cancel();
    }

    public void ShowVersion(AppSettings settings)
    {
        VersionValue.Text = "Текущая версия: " + UpdateCheck.Current;

        // Найденное при запуске окна (UpdateNotice) показывается сразу —
        // вместе с кнопкой установки, без повторного вопроса GitHub.
        if (UpdateNotice.Available is { } found)
        {
            _release = found;
            InstallButton.Visibility = Visibility.Visible;
            UpdateValue.Text = $"Есть новее: {found.Version}.";
        }
        else
        {
            UpdateValue.Text = settings.CheckForUpdates
                ? "Проверяется при запуске программы."
                : "Проверка при запуске выключена.";
        }
    }

    /// <summary>Установка с карточки «Вышла новая версия» или из окна обновления — та же.</summary>
    /// <param name="askFirst">
    /// <c>false</c> — из окна «Доступно обновление»: «Обновить» там и есть
    /// согласие, а что произойдёт, сказано в его «Подробностях».
    /// </param>
    public void Install(bool askFirst = true)
    {
        if (_release is null && UpdateNotice.Available is { } found)
            _release = found;

        _ = InstallAsync(askFirst);
    }

    /// <summary>
    /// Ход обновления — строкой под номером версии, рядом с кнопками.
    /// </summary>
    /// <remarks>
    /// Прежде писался отдельной строкой внизу карточки, а карточка — внизу
    /// «Главной»: человек нажимал «Обновить», и загрузка шла там, куда он
    /// не смотрел, вместе с ошибками вроде 404 и 503 (отзыв пользователя
    /// на 0.8.1, 25.09). Теперь и строка у кнопок, и карточка прокручивается
    /// в видимое.
    /// </remarks>
    private void Say(string text, bool failed = false)
    {
        UpdateValue.Text = text;

        if (failed)
            UpdateValue.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        else
            UpdateValue.ClearValue(TextBlock.ForegroundProperty);

        BringIntoView();
    }

    /// <summary>
    /// Спрашивает GitHub и открывает окно обновления — и когда ставить нечего.
    /// </summary>
    /// <remarks>
    /// Владелец 06.10: «пусть кнопка проверить открывает окно обновления, там
    /// такой же чейнджлог, только кнопка обновить некликабельная». Строка
    /// под версией остаётся: окно закрыли — итог проверки виден и так.
    /// GitHub не ответил — окна нет, открывать его не с чем.
    /// </remarks>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();

        UpdateButton.IsEnabled = false;
        Say("Спрашиваю GitHub…");

        ReleaseInfo? found = null;

        try
        {
            var release = await UpdateCheck.LatestAsync(_work.Token);
            found = release;

            UpdateNotice.Remember(release);

            _release = release is not null && UpdateCheck.IsNewer(release.Version, UpdateCheck.Current)
                ? release
                : null;

            InstallButton.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;

            UpdateValue.Text = release is null
                ? "Не удалось узнать: GitHub не ответил."
                : _release is not null
                    ? $"Есть новее: {release.Version}."
                    : "Установлена последняя.";
        }
        catch (OperationCanceledException)
        {
            UpdateValue.Text = "Проверка прервана.";
        }
        catch (Exception ex)
        {
            Say("Не удалось узнать: " + ex.GetBaseException().Message, failed: true);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }

        if (found is not null)
            (Window.GetWindow(this) as MainWindow)?.ShowUpdateWindow(found);
    }

    /// <summary>
    /// Вход в туннель для закачки — если туннель поднят и ему есть куда вести.
    /// </summary>
    /// <remarks>
    /// Вход проверки движка (health-in) ведёт в селектор выхода первым
    /// правилом. В «Десинке» движок туннеля поднят только ради DNS, и такой
    /// вход вёл бы напрямую — поэтому смотрим, нужен ли туннель настройкам.
    /// Пароль входа — из конфига работающего движка; нет его — без туннеля.
    /// </remarks>
    private static System.Net.IWebProxy? TunnelForDownload()
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (!settings.NeedsProxy || !settings.HasTunnelExit || !EngineControl.IsRunning)
                return null;

            return Proxy.EngineKeys.Current()?.Proxy(Proxy.SingBoxOptions.DefaultHealthPort);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Скачивает обновление и передаёт подмену внешнему сценарию.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Движки останавливаются до подмены, а не после. С 0.5.0 супервизор — это та же
    /// программа с ключом, и пока он работает, Windows держит её файл: подмена
    /// сорвалась бы на самом главном файле, а сценарий сообщил бы об этом уже
    /// после того, как окно закрылось.
    /// Но после закачки, а не до неё (04.10): закачка идёт с обходом,
    /// а без обхода человек остаётся на секунды подмены.
    /// </para>
    /// <para>
    /// Подменяет внешний сценарий, потому что заменить нужно и себя. Кто-то
    /// обязан пережить наше завершение, и это не костыль, а единственный
    /// вариант.
    /// </para>
    /// </remarks>
    private void OnInstallUpdate(object sender, RoutedEventArgs e) => _ = InstallAsync(askFirst: true);

    private async Task InstallAsync(bool askFirst)
    {
        if (_release is null)
            return;

        if (askFirst)
        {
            var answer = MessageBox.Show(
                $"Обновить до {_release.Version}?\n\n"
                + "Сперва скачается архив — обход в это время работает. Потом движки "
                + "остановятся, программа закроется, "
                + "файлы заменятся и она откроется снова.\n\n"
                + "Настройки, свои маршруты и подставленные адреса сохранятся.",
                "NetZapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
                return;
        }

        InstallButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        try
        {
            // Качаем при работающих движках, гасим — только перед подменой.
            // До 04.10 движки гасились первыми, и архив в 110 МБ шёл голой
            // сетью без десинка и туннеля: у Максима (Telegram) за 5–10 минут —
            // 2 %, и всё это время обхода не было. Теперь без обхода человек
            // остаётся на секунды подмены, а не на время закачки.
            string via = string.Empty;

            var progress = new Progress<double>(fraction =>
            {
                Say($"Скачиваю {_release.Version}{via}… {fraction * 100:0} %");
                InstallButton.Content = $"{fraction * 100:0} %";
            });

            var note = new Progress<string>(_ => via = " через туннель");

            var plan = await UpdateInstaller.StageAsync(_release, progress, CancellationToken.None, TunnelForDownload(), note);

            Say("Останавливаю движки…");
            await EngineControl.StopAsync("обновление программы", CancellationToken.None);

            var script = UpdateInstaller.WriteApplyScript(plan, Path.GetFullPath("."));

            // Что дописано руками в наши списки, переносится в новые — сказать
            // об этом сейчас: после подмены окна, которое могло бы сказать, нет.
            var carried = plan.CarriedLists.Count == 0
                ? string.Empty
                : " Ваши дописки в списки перенесены: "
                    + string.Join(", ", plan.CarriedLists.Select(c => $"{c.Key} — {c.Value}")) + ".";

            Say($"Скачано {plan.Files} файлов.{carried} Закрываюсь для подмены…");

            // Окно сейчас закроется, и строка выше проживёт секунду — в журнале она останется.
            if (carried.Length > 0)
                Journal.Write("обновление", $"{_release.Version}:{carried}");

            Process.Start(new ProcessStartInfo
            {
                FileName = script,
                Arguments = Environment.ProcessId.ToString(),
                UseShellExecute = true,
            });

            App.Exiting = true;
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Say("Обновиться не вышло: " + ex.GetBaseException().Message, failed: true);
            InstallButton.Content = "Обновить";

            InstallButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
        }
    }
}
