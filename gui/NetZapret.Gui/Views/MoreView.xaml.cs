using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Updates;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Режим работы в списке выбора.</summary>
public sealed record ModeRow(OperatingMode Key, string Name, string Note)
{
    public bool Chosen { get; set; }

    public Brush Edge => (Brush)Application.Current.FindResource(Chosen ? "Accent" : "Border");

    public Visibility MarkShown => Chosen ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Выключатель без своего раздела.</summary>
public sealed record FlagRow(string Key, string Name, string Note)
{
    public bool On { get; set; }

    public string State => On ? "включено" : "выключено";

    public Brush Color => (Brush)Application.Current.FindResource(On ? "Accent" : "Muted");
}

/// <summary>
/// Всё, что не заслужило своего раздела, но нужно.
/// </summary>
/// <remarks>
/// <para>
/// Режим здесь не по остаточному принципу: он задаёт рамку, внутри которой
/// вообще имеют смысл маршруты, и до этого раздела менять его можно было
/// только из меню консоли.
/// </para>
/// <para>
/// Ссылка подписки вводится скрытым полем и обратно не показывается. По ней
/// выдаются серверы — это пароль, и место ему в файле настроек, а не на
/// экране, откуда его унесёт первый же снимок.
/// </para>
/// </remarks>
public partial class MoreView : UserControl
{
    /// <summary>Имя задачи в планировщике; то же, что заводит консоль.</summary>
    private const string TaskName = AutostartTask.DefaultTaskName;

    private CancellationTokenSource? _work;

    public MoreView()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _work?.Cancel();
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowModes(settings);
        ShowSubscription(settings);
        ShowAutostart();
        ShowVersion(settings);
        ShowFlags(settings);

        RootValue.Text = Path.GetFullPath(".");

        Status.Text = "Изменения записываются сразу, а действовать начинают при следующем "
            + "запуске движков.";

        StartDefenderCheck();
    }

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
            Status.Text = $"Режим: {settings.DescribeMode()}. Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать режим: " + ex.GetBaseException().Message;
        }
    }

    private void ShowSubscription(AppSettings settings)
    {
        bool set = !string.IsNullOrWhiteSpace(settings.SubscriptionUrl);

        SubValue.Text = set ? "задана" : "не задана";
        SubClear.IsEnabled = set;
    }

    private void OnSaveSubscription(object sender, RoutedEventArgs e)
    {
        var url = SubBox.Password.Trim();

        if (url.Length == 0)
        {
            Status.Text = "Поле пустое. Чтобы убрать подписку, есть кнопка «Убрать».";
            return;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            // Ссылку не показываем даже в жалобе: она уже в поле, и повторять
            // её в подписи значит вынести на экран ровно то, что мы прячем.
            Status.Text = "Это не похоже на ссылку http или https.";
            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { SubscriptionUrl = url };
            settings.Save(AppSettings.DefaultPath);

            SubBox.Clear();
            ShowSubscription(settings);

            Status.Text = "Подписка сохранена. Серверы появятся в разделе «Серверы».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сохранить: " + ex.GetBaseException().Message;
        }
    }

    private void OnClearSubscription(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { SubscriptionUrl = null };
            settings.Save(AppSettings.DefaultPath);

            ShowSubscription(settings);
            Status.Text = "Подписка убрана. Серверов больше нет — десинк при этом работает.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось убрать: " + ex.GetBaseException().Message;
        }
    }

    private void ShowAutostart()
    {
        bool installed;

        try
        {
            installed = AutostartTask.IsInstalled(TaskName);
        }
        catch (Exception ex)
        {
            AutostartValue.Text = "не читается";
            AutostartButton.IsEnabled = false;
            Status.Text = "Планировщик не отвечает: " + ex.GetBaseException().Message;

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
            Status.Text = $"Не найдена консольная программа: {exe}. Задача запускает именно её.";
            return;
        }

        try
        {
            if (AutostartTask.IsInstalled(TaskName))
            {
                var (removed, output) = AutostartTask.Remove(TaskName);

                Status.Text = removed
                    ? "Автозапуск убран."
                    : "Не удалось убрать: " + output;
            }
            else
            {
                var (ok, output) = AutostartTask.Install(new AutostartOptions
                {
                    TaskName = TaskName,
                    ExecutablePath = exe,

                    // Те же ключи, что у кнопки «Запустить»: иначе автозапуск
                    // поднимал бы не то, что человек проверил руками.
                    Arguments = StatusView.BuildStartArguments(),
                    WorkingDirectory = Path.GetFullPath("."),
                    UserId = Environment.UserName,
                });

                Status.Text = ok
                    ? "Автозапуск заведён: поднимется при следующем входе в систему."
                    : "Не удалось завести: " + output;
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Планировщик отказал: " + ex.GetBaseException().Message;
        }

        ShowAutostart();
    }

    private void ShowVersion(AppSettings settings)
    {
        VersionValue.Text = UpdateCheck.Current;

        UpdateValue.Text = settings.CheckForUpdates
            ? "Проверяется при запуске консоли."
            : "Проверка при запуске выключена.";
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

            UpdateValue.Text = release is null
                ? "Не удалось узнать: GitHub не ответил."
                : UpdateCheck.IsNewer(release.Version, UpdateCheck.Current)
                    ? $"Есть новее: {release.Version}. Ставится из консоли — обновление трогает "
                      + "движки, которые сейчас несут весь трафик машины."
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
    /// Спрашивает Defender, исключена ли наша папка.
    /// </summary>
    /// <remarks>
    /// В стороне от показа: <c>Get-MpPreference</c> поднимает PowerShell
    /// и отвечает секунду-полторы, а раздел должен открыться сразу.
    /// </remarks>
    private void StartDefenderCheck()
    {
        DefenderValue.Text = "спрашиваю…";
        DefenderButton.IsEnabled = false;

        var path = Path.GetFullPath(".");

        _ = Task.Run(() =>
        {
            bool excluded = IsExcluded(path);

            Dispatcher.Invoke(() =>
            {
                DefenderValue.Text = excluded ? "есть" : "нет";
                DefenderButton.IsEnabled = !excluded;
            });
        });
    }

    private void OnExclude(object sender, RoutedEventArgs e)
    {
        var path = Path.GetFullPath(".");

        DefenderButton.IsEnabled = false;
        Status.Text = "Прошу Defender об исключении…";

        _ = Task.Run(() =>
        {
            int code = Run(
                "powershell",
                $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{path.Replace("'", "''")}'\"");

            // Проверяем делом, а не кодом возврата: при включённой защите
            // от изменений команда проходит и не делает ничего.
            bool excluded = IsExcluded(path);

            Dispatcher.Invoke(() =>
            {
                DefenderValue.Text = excluded ? "есть" : "нет";
                DefenderButton.IsEnabled = !excluded;

                Status.Text = excluded
                    ? $"В исключениях: {path}"
                    : code == 0
                        ? "Команда прошла, а исключение не появилось — включена защита от "
                          + "изменений. Добавьте вручную: Безопасность Windows → Защита от "
                          + "вирусов → Исключения."
                        : $"Не вышло, код {code}.";
            });
        });
    }

    private static bool IsExcluded(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-MpPreference).ExclusionPath -join [char]10\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            return output.Split('\n').Any(line => string.Equals(
                line.Trim().TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // Defender может быть выключен, подменён другим антивирусом или
            // не отвечать вовсе. Ни один из случаев не повод не открыть раздел.
            return false;
        }
    }

    private static int Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return -1;

            process.WaitForExit(30_000);
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private void ShowFlags(AppSettings settings)
    {
        var rows = new List<FlagRow>
        {
            new("logs", "Вести журнал",
                "Без него разбор сбоя сводится к догадкам. Выключают те, кого смущает рост "
                + "файла: winws2 пишет строку на соединение.")
            { On = settings.LogsEnabled },

            new("verify", "Проверять проход трафика",
                "Не только открытость порта, но и то, что через него что-то проходит. "
                + "Дороже по времени, зато ловит молчащую трубу.")
            { On = settings.VerifyTraffic },

            new("foreign", "Автоподбор только по зарубежным",
                "Подбор идёт по задержке и потому всегда сползает на ближайший сервер — свой "
                + "же, — а через него не работает ровно то, ради чего туннель обычно и нужен.")
            { On = settings.ForeignExitsOnly },

            new("discord", "Предлагать перезапуск Discord",
                "Он запоминает голосовые серверы на сеанс и до перезапуска ходит по-старому. "
                + "Сам он не перезапускается никогда: посреди звонка это хуже задержки.")
            { On = settings.OfferDiscordRestart },

            new("updates", "Искать обновления при запуске",
                "Только искать. Установка остаётся отдельным действием с отдельным согласием.")
            { On = settings.CheckForUpdates },
        };

        Flags.ItemsSource = rows;
    }

    private void OnFlag(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            settings = key switch
            {
                "logs" => settings with { LogsEnabled = !settings.LogsEnabled },
                "verify" => settings with { VerifyTraffic = !settings.VerifyTraffic },
                "foreign" => settings with { ForeignExitsOnly = !settings.ForeignExitsOnly },
                "discord" => settings with { OfferDiscordRestart = !settings.OfferDiscordRestart },
                _ => settings with { CheckForUpdates = !settings.CheckForUpdates },
            };

            settings.Save(AppSettings.DefaultPath);

            ShowFlags(settings);
            ShowVersion(settings);

            Status.Text = "Записано. Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string folder })
            return;

        var target = Path.GetFullPath(folder);

        if (!Directory.Exists(target))
        {
            Status.Text = $"Папки ещё нет: {target}. Она заводится, когда в ней появляется первый файл.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть: " + ex.GetBaseException().Message;
        }
    }
}
