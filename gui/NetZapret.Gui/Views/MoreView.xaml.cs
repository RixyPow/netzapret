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
/// Режим и автозапуск отсюда ушли на «Главную», а подписки — в «VPN».
/// Здесь они и правда лежали по остаточному принципу: режим задаёт рамку,
/// внутри которой имеют смысл маршруты, и место ему рядом с кнопкой запуска,
/// а подписке — там, где видно, что она приносит.
/// </remarks>
public partial class MoreView : UserControl
{
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

        ShowVersion(settings);
        ShowFlags(settings);
        ShowRoutes();

        RootValue.Text = Path.GetFullPath(".");

        Status.Text = "Изменения записываются сразу, а действовать начинают при следующем "
            + "запуске движков.";

        StartDefenderCheck();
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

    /// <summary>Найденное обновление; <c>null</c> — ставить нечего.</summary>
    private ReleaseInfo? _release;

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

        if (!Confirm(
            $"Обновить до {_release.Version}?\n\n"
            + "Движки будут остановлены, соединения оборвутся. Программа закроется, "
            + "файлы заменятся и она откроется снова.\n\n"
            + "Настройки, свои маршруты и подставленные адреса сохранятся."))
        {
            return;
        }

        InstallButton.IsEnabled = false;
        UpdateButton.IsEnabled = false;

        try
        {
            Status.Text = "Останавливаю движки…";
            await EngineControl.StopAsync(CancellationToken.None);

            var progress = new Progress<double>(fraction =>
                Status.Text = $"Скачиваю… {fraction * 100:0}%");

            var plan = await UpdateInstaller.StageAsync(_release, progress, CancellationToken.None);
            var script = UpdateInstaller.WriteApplyScript(plan, Path.GetFullPath("."));

            Status.Text = $"Скачано {plan.Files} файлов. Закрываюсь для подмены…";

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
            Status.Text = "Обновиться не вышло: " + ex.GetBaseException().Message;

            InstallButton.IsEnabled = true;
            UpdateButton.IsEnabled = true;
        }
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

    private void ShowRoutes()
    {
        try
        {
            var count = UserRulesFile.Load(UserRulesFile.DefaultPath).Entries.Count;

            RoutesValue.Text = count == 0
                ? "Своих маршрутов нет — судьбу соединений решают общие правила."
                : $"Своих маршрутов: {count}. Это выборы «напрямую», «десинк» и «через VPN», "
                  + "сделанные руками поверх общих правил.";

            ForgetRoutesButton.IsEnabled = count > 0;
        }
        catch (Exception ex)
        {
            RoutesValue.Text = "Файл своих маршрутов не читается: " + ex.GetBaseException().Message;
            ForgetRoutesButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// Работает ли супервизор прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Стереть рабочие файлы под живым супервизором значит потерять след
    /// запущенных процессов: остановить их станет нечем, а следующий запуск
    /// упрётся в занятый драйвер и осиротевший TUN.
    /// </remarks>
    private static bool EnginesRunning()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        return state is not null && state.IsSupervisorAlive();
    }

    private static bool Confirm(string question) =>
        MessageBox.Show(
            question,
            "NetZapret",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Отодвигает файл в сторону вместо удаления.
    /// </summary>
    /// <remarks>
    /// Настройки и маршруты набираются руками и месяцами, а кнопка стоит
    /// в разделе, куда заходят посмотреть версию. Копия не стоит ничего
    /// и однажды окупается целиком.
    /// </remarks>
    private static void SetAside(string path)
    {
        if (!File.Exists(path))
            return;

        var backup = path + ".bak";

        // Затираем предыдущую копию: две правки подряд означают, что
        // разбираются прямо сейчас, и интересна последняя.
        if (File.Exists(backup))
            File.Delete(backup);

        File.Move(path, backup);
    }

    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        if (EnginesRunning())
        {
            Status.Text = "Сначала остановите движки: под ними лежит состояние супервизора, "
                + "и без него остановить их станет нечем.";

            return;
        }

        if (!Confirm(
            "Сбросить настройки до заводских?\n\n"
            + "Режим, пресет, выбранный сервер и свои маршруты будут забыты, рабочие файлы "
            + "удалены. Ссылка подписки останется: её выдаёт поставщик, и восстановить её "
            + "программа не может.\n\n"
            + "Прежние настройки и маршруты лягут рядом с расширением .bak."))
        {
            return;
        }

        try
        {
            var subscription = AppSettings.Load(AppSettings.DefaultPath).SubscriptionUrl;

            SetAside(AppSettings.DefaultPath);
            SetAside(UserRulesFile.DefaultPath);

            if (Directory.Exists("runtime"))
                Directory.Delete("runtime", recursive: true);

            new AppSettings { SubscriptionUrl = subscription }.Save(AppSettings.DefaultPath);

            Reload();
            Status.Text = "Настройки сброшены, подписка сохранена. Прежние лежат рядом с .bak.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не вышло: " + ex.GetBaseException().Message;
        }
    }

    private void OnForgetRoutes(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
            "Забыть все свои маршруты?\n\n"
            + "Выборы «напрямую», «десинк» и «через VPN», сделанные руками, будут сняты — "
            + "решать станут общие правила.\n\n"
            + "Прежний файл ляжет рядом с расширением .bak."))
        {
            return;
        }

        try
        {
            SetAside(UserRulesFile.DefaultPath);

            ShowRoutes();
            Status.Text = "Свои маршруты забыты. Прежний файл лежит рядом с .bak.";

            // В отличие от сброса настроек, здесь движки могли остаться
            // работать — и работают они по прежним маршрутам, пока их
            // не перезапустить.
            this.Offer("свои маршруты");
        }
        catch (Exception ex)
        {
            Status.Text = "Не вышло: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Убирает журналы и конфиги прошлых запусков.
    /// </summary>
    /// <remarks>
    /// Повторяет отбор консольной команды clean: удаляются только json и log,
    /// действующий конфиг остаётся. Без него не запуститься, а собрать заново
    /// можно лишь с подпиской под рукой.
    /// </remarks>
    private void OnClean(object sender, RoutedEventArgs e)
    {
        if (EnginesRunning())
        {
            Status.Text = "Супервизор работает и держит эти файлы. Сначала остановите движки.";
            return;
        }

        try
        {
            const string runtime = "runtime";

            if (!Directory.Exists(runtime))
            {
                Status.Text = "Каталог runtime пуст или не существует — убирать нечего.";
                return;
            }

            var keep = Path.GetFullPath(Path.Combine(runtime, "singbox.json"));

            var removed = 0;
            long freed = 0;

            foreach (var file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase))
                    continue;

                var extension = Path.GetExtension(file);

                bool removable = extension is ".json" or ".log"
                    || Path.GetFileName(file).Contains(".log.", StringComparison.OrdinalIgnoreCase);

                if (!removable)
                    continue;

                try
                {
                    long size = new FileInfo(file).Length;
                    File.Delete(file);

                    removed++;
                    freed += size;
                }
                catch (Exception)
                {
                    // Один занятый файл не повод бросать уборку на половине.
                }
            }

            Status.Text = $"Удалено файлов: {removed}, освобождено {freed / 1024.0:0.#} КБ. "
                + "Действующий конфиг сохранён.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не вышло: " + ex.GetBaseException().Message;
        }
    }

    private void OnResetNetwork(object sender, RoutedEventArgs e)
    {
        if (!Confirm(
            "Сбросить сетевой стек Windows?\n\n"
            + "winsock и TCP/IP вернутся к исходным настройкам. Потребуется перезагрузка "
            + "компьютера: без неё сеть останется в переходном состоянии, то есть хуже "
            + "исходного."))
        {
            return;
        }

        ResetNetworkButton.IsEnabled = false;
        Status.Text = "Сбрасываю сетевой стек…";

        _ = Task.Run(() =>
        {
            string? failed = null;

            foreach (var arguments in new[] { "int ip reset", "winsock reset" })
            {
                int code = Run("netsh", arguments);

                if (code != 0)
                {
                    failed = $"netsh {arguments} завершился с кодом {code}. "
                        + "Обычно это значит, что не хватило прав администратора.";

                    break;
                }
            }

            Dispatcher.Invoke(() =>
            {
                ResetNetworkButton.IsEnabled = true;

                Status.Text = failed
                    ?? "Сетевой стек сброшен. Перезагрузите компьютер: без этого сеть "
                       + "останется в переходном состоянии.";
            });
        });
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
                "discord" => settings with { OfferDiscordRestart = !settings.OfferDiscordRestart },
                _ => settings with { CheckForUpdates = !settings.CheckForUpdates },
            };

            settings.Save(AppSettings.DefaultPath);

            ShowFlags(settings);
            ShowVersion(settings);

            Status.Text = "Записано. Применится при следующем запуске движков.";
            this.Offer("Настройка изменена");
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
