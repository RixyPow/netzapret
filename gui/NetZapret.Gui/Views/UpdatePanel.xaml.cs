using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Core.Updates;

namespace NetZapret.Gui.Views;

/// <summary>
/// Установка обновления — на «Главной», с ходом закачки и ошибкой.
/// </summary>
/// <remarks>
/// Перенесено из «Ещё» 23.09 по просьбе владельца: карточка «Вышла новая
/// версия» не уводит в другой раздел, а зовёт установку здесь же —
/// <see cref="Install"/>. С 11.11 «Главная» двух видов (<see cref="ShowView"/>):
/// в подробном карточка стоит всегда с версией и «Проверить», в простом —
/// только во время установки и после сбоя. Проверка руками есть и в «Ещё →
/// Обслуживание» (MoreView) — тем же <see cref="AskAsync"/>.
/// </remarks>
public partial class UpdatePanel : UserControl
{
    /// <summary>Найденное обновление; <c>null</c> — ставить нечего.</summary>
    private ReleaseInfo? _release;

    private CancellationTokenSource? _work;

    /// <summary>Подробный вид «Главной»: карточка всегда, с «Проверить».</summary>
    private bool _advanced;

    /// <summary>Установка начата: карточка стоит в любом виде — ход закачки и ошибка здесь.</summary>
    private bool _installing;

    public UpdatePanel()
    {
        InitializeComponent();

        Unloaded += (_, _) => _work?.Cancel();
    }

    /// <summary>Вид «Главной»: в подробном карточка всегда, в простом — только при установке.</summary>
    public void ShowView(bool advanced)
    {
        _advanced = advanced;
        UpdateButton.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;

        if (advanced && !_installing)
            ShowVersion(AppSettings.Load(AppSettings.DefaultPath));

        Visibility = advanced || _installing ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Версия и что известно об обновлении — для подробного вида.</summary>
    private void ShowVersion(AppSettings settings)
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
            InstallButton.Visibility = Visibility.Collapsed;
            UpdateValue.Text = settings.CheckForUpdates
                ? "Проверяется при запуске программы."
                : "Проверка при запуске выключена.";
        }
    }

    /// <summary>
    /// Спрашивает GitHub о последнем выпуске и запоминает ответ (UpdateNotice).
    /// </summary>
    /// <returns>Ответ GitHub (может быть и не новее) и итог словами.</returns>
    /// <remarks>
    /// Одно на «Проверить» здесь и «Проверить сейчас» в «Ещё»: найденное
    /// запоминается, и «Главная» показывает карточку «Вышла …».
    /// </remarks>
    internal static async Task<(ReleaseInfo? Found, string Text, bool Failed)> AskAsync(CancellationToken cancellationToken)
    {
        try
        {
            var release = await UpdateCheck.LatestAsync(cancellationToken);

            UpdateNotice.Remember(release);

            return (release, release is null
                ? "Не удалось узнать: GitHub не ответил."
                : UpdateCheck.IsNewer(release.Version, UpdateCheck.Current)
                    ? $"Есть новее: {release.Version}."
                    : "Установлена последняя.", false);
        }
        catch (OperationCanceledException)
        {
            return (null, "Проверка прервана.", false);
        }
        catch (Exception ex)
        {
            return (null, "Не удалось узнать: " + ex.GetBaseException().Message, true);
        }
    }

    /// <summary>
    /// Спрашивает GitHub и открывает окно обновления — и когда ставить нечего.
    /// </summary>
    /// <remarks>
    /// Владелец 06.10: кнопка «Проверить» открывает окно обновления с тем же
    /// чейнджлогом, только «Обновить» в нём неактивна. Строка под версией
    /// остаётся: окно закрыли — итог проверки виден и так. GitHub не ответил —
    /// окна нет, открывать его не с чем.
    /// </remarks>
    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        _work?.Cancel();
        _work = new CancellationTokenSource();

        UpdateButton.IsEnabled = false;
        Say("Спрашиваю GitHub…");

        var (found, text, failed) = await AskAsync(_work.Token);

        _release = found is not null && UpdateCheck.IsNewer(found.Version, UpdateCheck.Current) ? found : null;
        InstallButton.Visibility = _release is null ? Visibility.Collapsed : Visibility.Visible;
        Say(text, failed);
        UpdateButton.IsEnabled = true;

        if (found is not null)
            (Window.GetWindow(this) as MainWindow)?.ShowUpdateWindow(found);
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
    /// Ставит найденное обновление — общим путём <see cref="UpdateRun"/>.
    /// </summary>
    /// <remarks>
    /// Качаем при работающих движках, гасим — только перед подменой. До 04.10
    /// движки гасились первыми, и архив в 110 МБ шёл голой сетью без десинка
    /// и туннеля: у Максима (Telegram) за 5–10 минут — 2 %, и всё это время
    /// обхода не было.
    /// </remarks>
    private void OnInstallUpdate(object sender, RoutedEventArgs e) => _ = InstallAsync(askFirst: true);

    private async Task InstallAsync(bool askFirst)
    {
        if (_release is null || UpdateRun.Busy)
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

        // Карточка встаёт на время установки в любом виде: ход закачки и ошибки — здесь.
        _installing = true;
        Visibility = Visibility.Visible;
        VersionValue.Text = $"{UpdateCheck.Current} → {_release.Version}";
        InstallButton.Visibility = Visibility.Visible;
        InstallButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        try
        {
            await UpdateRun.InstallAsync(
                _release,
                new Progress<string>(text => Say(text)),
                new Progress<double>(share => InstallButton.Content = $"{share * 100:0} %"));
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
