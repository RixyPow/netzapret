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
        VersionValue.Text = UpdateCheck.Current;

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

    /// <summary>Установка с карточки «Вышла новая версия» — та же, со своим вопросом.</summary>
    public void Install()
    {
        if (_release is null && UpdateNotice.Available is { } found)
            _release = found;

        OnInstallUpdate(this, new RoutedEventArgs());
    }

    private void Say(string text)
    {
        UpdateStatus.Text = text;
        UpdateStatus.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();

        UpdateButton.IsEnabled = false;
        UpdateValue.Text = "Спрашиваю GitHub…";

        try
        {
            var release = await UpdateCheck.LatestAsync(_work.Token);

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
            UpdateValue.Text = "Не удалось узнать: " + ex.GetBaseException().Message;
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Скачивает обновление и передаёт подмену внешнему сценарию.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Движки останавливаются до, а не после. С 0.5.0 супервизор — это та же
    /// программа с ключом, и пока он работает, Windows держит её файл: подмена
    /// сорвалась бы на самом главном файле, а сценарий сообщил бы об этом уже
    /// после того, как окно закрылось.
    /// </para>
    /// <para>
    /// Подменяет внешний сценарий, потому что заменить нужно и себя. Кто-то
    /// обязан пережить наше завершение, и это не костыль, а единственный
    /// вариант.
    /// </para>
    /// </remarks>
    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_release is null)
            return;

        var answer = MessageBox.Show(
            $"Обновить до {_release.Version}?\n\n"
            + "Движки будут остановлены, соединения оборвутся. Программа закроется, "
            + "файлы заменятся и она откроется снова.\n\n"
            + "Настройки, свои маршруты и подставленные адреса сохранятся.",
            "NetZapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            return;

        InstallButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        try
        {
            Say("Останавливаю движки…");
            await EngineControl.StopAsync(CancellationToken.None);

            var progress = new Progress<double>(fraction => Say($"Скачиваю… {fraction * 100:0}%"));

            var plan = await UpdateInstaller.StageAsync(_release, progress, CancellationToken.None);
            var script = UpdateInstaller.WriteApplyScript(plan, Path.GetFullPath("."));

            Say($"Скачано {plan.Files} файлов. Закрываюсь для подмены…");

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
            Say("Обновиться не вышло: " + ex.GetBaseException().Message);

            InstallButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
        }
    }
}
