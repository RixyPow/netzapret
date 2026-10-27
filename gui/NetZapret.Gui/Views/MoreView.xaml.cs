using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Themes;
using NetZapret.Core.Updates;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

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
    public MoreView()
    {
        InitializeComponent();

        ShowAbout();

        Loaded += (_, _) => Reload();
    }

    /// <summary>
    /// Открывает мастер первого запуска заново.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Только просит окно показать мастер — ничего из уже настроенного
    /// (подписку, режим, пресет) не трогает.
    /// </para>
    /// <para>
    /// Отметку «пройден» (<see cref="AppSettings.OnboardingDone"/>) не снимает.
    /// До 07.10 снимала, а ставилась она обратно только «Готово» или
    /// «Пропустить»: открыл мастер посмотреть, ушёл в другой раздел — и он
    /// встречал при каждом следующем запуске. Владелец 07.10: «почему опять
    /// запускается мастер первого запуска при билде» — три сборки подряд.
    /// </para>
    /// </remarks>
    private void OnRestartOnboarding(object sender, RoutedEventArgs e)
    {
        try
        {
            (Window.GetWindow(this) as MainWindow)?.RestartOnboarding();

            Status.Text = "Мастер открыт на «Главной».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть мастер: " + ex.GetBaseException().Message;
        }
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowRoutes();

        ShowUpdates(settings);

        RootValue.Text = Path.GetFullPath(".");

        Status.Text = "Изменения записываются сразу, а действовать начинают при следующем "
            + "запуске движков.";

        StartDefenderCheck();
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

    /// <summary>«Искать обновления при запуске» — тумблер и слово рядом.</summary>
    private void ShowUpdates(AppSettings settings)
    {
        UpdatesSwitch.IsChecked = settings.CheckForUpdates;
        UpdatesWord.Text = settings.CheckForUpdates ? "вкл." : "выкл.";
    }

    private void OnUpdatesSwitch(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = AppSettings.Load(AppSettings.DefaultPath) with { CheckForUpdates = UpdatesSwitch.IsChecked == true };

            next.Save(AppSettings.DefaultPath);
            ShowUpdates(next);

            // Движкам безразлично: читается окном при его запуске.
            Status.Text = next.CheckForUpdates
                ? "Записано: обновления будут искаться при запуске программы."
                : "Записано: при запуске обновления не ищутся — проверить можно кнопкой на «Главной».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
            ShowUpdates(AppSettings.Load(AppSettings.DefaultPath));
        }
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

            (AppSettings.Fresh with { SubscriptionUrl = subscription }).Save(AppSettings.DefaultPath);

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

    /// <summary>Карточка «О программе»: неизменна, заполняется один раз.</summary>
    private void ShowAbout()
    {
        AuthorValue.Text = $"NetZapret {UpdateCheck.Current}. Автор — {About.Author}; идеи и отчёты об ошибках "
            + "приносят участники обсуждений и issues на GitHub. Лицензия MIT, программа бесплатна.";

        RepositoryLink.Tag = About.Repository;
        IssuesLink.Tag = About.Issues;
        DiscussionsLink.Tag = About.Discussions;
        TelegramLink.Tag = About.Telegram;
        SupportLink.Tag = About.Support;

        Components.ItemsSource = About.Components;

        _ = ShowDownloadsAsync();
    }

    /// <summary>Скачиваний на GitHub — спрашиваем один раз за запуск окна.</summary>
    private static long? _downloads;

    private async Task ShowDownloadsAsync()
    {
        _downloads ??= await DownloadStats.TotalAsync(CancellationToken.None);

        if (_downloads is not { } total)
            return;

        DownloadsValue.Text = $"Скачано с GitHub: {total:N0} раз. Это скачивания, а не установки: "
            + "обновление из программы тоже засчитывается. Своего счётчика у программы нет — "
            + "она не сообщает о вас никуда.";
        DownloadsValue.Visibility = Visibility.Visible;
    }

    private void OnLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string url })
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть ссылку: " + ex.GetBaseException().Message;
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

    private void OnFixKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            OnFix(sender, e);
    }

    /// <summary>Шаги подбора на экране — по мере проверки.</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<FixStepRow> _fixRows = [];

    /// <summary>Строка-заголовок рецептов: появляется с первым рецептом, итог пишется в неё.</summary>
    private FixStepRow? _recipesRow;

    /// <summary>Имя последнего подбора — для «Показать в «Маршрутах»».</summary>
    private string? _fixHost;

    /// <summary>
    /// «Сайт не открывается»: подбор маршрута по симптому, сработавший — в правила (SiteFixer).
    /// </summary>
    private async void OnFix(object sender, RoutedEventArgs e)
    {
        var input = FixSite.Text;

        if (string.IsNullOrWhiteSpace(input) || !FixButton.IsEnabled)
            return;

        FixButton.IsEnabled = false;
        FixButton.Content = "Проверяю…";
        StartFix();

        var progress = new Progress<string>(text => FixNow.Text = text);
        var stepDone = new Progress<SiteFixStep>(AddFixStep);

        try
        {
            var result = await Task.Run(() => SiteFixer.FixAsync(input, progress, CancellationToken.None, stepDone));

            ShowFix(result);

            Journal.Write("починка сайта", $"{result.Host}: "
                + string.Join("; ", result.Steps.Select(s => $"{s.Path} — {(s.Works ? "да" : "нет")}, {s.Detail}"))
                + (result.Applied is { } mode ? $" → записан маршрут {mode}" : " → маршрут не менялся"));

            if (result.Applied is { } applied && result.NeedsRestart)
            {
                this.Offer($"Маршрут для {result.Host}: " + (applied == RoutingMode.Proxy
                    ? "через VPN"
                    : result.Recipe is { } recipe ? $"десинк, {recipe}" : "десинк"));
            }
        }
        catch (Exception ex)
        {
            FixNowLine.Visibility = Visibility.Collapsed;
            Outcome(Glyphs.Warning, "Warn", "Проверка не удалась", ex.GetBaseException().Message, reveal: false);
        }
        finally
        {
            FixButton.IsEnabled = true;
            FixButton.Content = "Починить";
        }
    }

    /// <summary>Чистит прошлый подбор и показывает ход нового.</summary>
    internal void StartFix()
    {
        FixSteps.ItemsSource ??= _fixRows;
        _fixRows.Clear();
        _recipesRow = null;
        _fixHost = null;

        FixPanel.Visibility = Visibility.Visible;
        FixOutcome.Visibility = Visibility.Collapsed;
        FixNowLine.Visibility = Visibility.Visible;
        FixNow.Text = "Проверяю…";
    }

    /// <summary>Строка на опробованный шаг; рецепты — под общим заголовком с отступом.</summary>
    internal void AddFixStep(SiteFixStep step)
    {
        bool skipped = step.Detail.StartsWith("не пробовал", StringComparison.Ordinal);

        if (step.Try == RouteTry.Recipes && step.Recipe is not null)
        {
            if (_recipesRow is null)
            {
                _recipesRow = new FixStepRow
                {
                    Glyph = Glyphs.Recipes,
                    Tone = (Brush)FindResource("Muted"),
                    Title = "Рецепты десинка",
                    Weight = FontWeights.SemiBold,
                };

                _fixRows.Add(_recipesRow);
            }

            _fixRows.Add(new FixStepRow
            {
                Glyph = step.Works ? Glyphs.Ok : Glyphs.No,
                Tone = (Brush)FindResource(step.Works ? "Accent" : "Danger"),
                Title = step.Recipe,
                Detail = step.Detail,
                Indent = new Thickness(24, 0, 0, 0),
            });

            int tried = _fixRows.Count(r => r.Indent.Left > 0);
            int worked = _fixRows.Count(r => r.Indent.Left > 0 && r.Glyph == Glyphs.Ok);

            _recipesRow.Detail = worked > 0 ? $"открыл {tried}-й по счёту" : $"опробовано {tried}, ни один не открыл";
            _recipesRow.Glyph = worked > 0 ? Glyphs.Ok : Glyphs.Recipes;
            _recipesRow.Tone = (Brush)FindResource(worked > 0 ? "Accent" : "Muted");
            return;
        }

        _fixRows.Add(new FixStepRow
        {
            Glyph = skipped ? Glyphs.Warning : step.Works ? Glyphs.Ok : Glyphs.No,
            Tone = (Brush)FindResource(skipped ? "Warn" : step.Works ? "Accent" : "Danger"),
            Title = StepTitle(step),
            Detail = step.Detail,
            Weight = FontWeights.SemiBold,
        });
    }

    /// <summary>Что пробовали — словами для строки.</summary>
    private static string StepTitle(SiteFixStep step) => step.Try switch
    {
        null => "Как есть" + (step.Path.IndexOf('(') is var at and >= 0 ? " · " + step.Path[(at + 1)..].TrimEnd(')') : string.Empty),
        RouteTry.Direct => "Напрямую, без десинка",
        RouteTry.Preset => "Десинк пресета",
        RouteTry.Recipes => "Рецепты десинка",
        RouteTry.Vpn => "Через VPN",
        _ => step.Path,
    };

    /// <summary>Итог подбора — плашкой над ходом.</summary>
    internal void ShowFix(SiteFixResult result)
    {
        _fixHost = result.Host;
        FixNowLine.Visibility = Visibility.Collapsed;

        // Шаги, пришедшие не по ходу (например, ход выключен), — дорисовываем.
        if (_fixRows.Count == 0)
        {
            foreach (var step in result.Steps)
                AddFixStep(step);
        }

        if (result.Host is null || result.Steps.Count == 0)
        {
            Outcome(Glyphs.Warning, "Warn", result.Summary, null, reveal: false);
            return;
        }

        var rule = SiteFixer.RuleValue(result.Host);

        switch (result.Applied)
        {
            case RoutingMode.Direct:
                Outcome(Glyphs.Ok, "Accent", $"Записан маршрут «напрямую» для {rule}",
                    "Десинк этому сайту мешал. Действует уже сейчас.", reveal: true);
                break;

            case RoutingMode.Desync:
                Outcome(Glyphs.Ok, "Accent",
                    $"Записан маршрут «десинк»{(result.Recipe is { } recipe ? $" с рецептом «{recipe}»" : string.Empty)} для {rule}",
                    "Заработает после перезапуска движков.", reveal: true);
                break;

            case RoutingMode.Proxy:
                int recipes = result.Steps.Count(s => s.Try == RouteTry.Recipes && s.Recipe is not null);

                Outcome(Glyphs.Ok, "Accent", $"Записан маршрут «через VPN» для {rule}",
                    "Заработает после перезапуска движков."
                    + (recipes > 0 ? $" Рецептов десинка опробовано {recipes} — ни один не открыл." : string.Empty),
                    reveal: true);
                break;

            default:
                bool open = result.Steps[0].Works;

                Outcome(open ? Glyphs.Ok : Glyphs.No, open ? "Accent" : "Danger",
                    open ? $"{result.Host} открывается как есть — менять нечего" : $"{result.Host} не открылся ни одним путём — маршрут не менял",
                    result.Advice, reveal: false);
                break;
        }
    }

    private void Outcome(string glyph, string tone, string head, string? note, bool reveal)
    {
        var brush = (Brush)FindResource(tone);

        FixOutcomeGlyph.Text = glyph;
        FixOutcomeGlyph.Foreground = brush;
        FixOutcome.BorderBrush = brush;
        FixOutcomeHead.Text = head;
        FixOutcomeNote.Text = note ?? string.Empty;
        FixOutcomeNote.Visibility = string.IsNullOrWhiteSpace(note) ? Visibility.Collapsed : Visibility.Visible;
        FixReveal.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        FixOutcome.Visibility = Visibility.Visible;
        FixPanel.Visibility = Visibility.Visible;
    }

    /// <summary>«Маршруты» на части, чьё правило теперь решает это имя (RoutesView.Reveal) — отбор сбрасывается на «Все».</summary>
    private void OnFixReveal(object sender, RoutedEventArgs e)
    {
        if (_fixHost is null)
            return;

        RoutesView.PendingReveal = _fixHost;
        (Window.GetWindow(this) as MainWindow)?.Open("routes");
    }

    /// <summary>Знаки строк подбора — Segoe Fluent Icons (MDL2 те же коды).</summary>
    internal static class Glyphs
    {
        public const string Ok = "";
        public const string No = "";
        public const string Warning = "";
        public const string Recipes = "";
    }
}

/// <summary>Строка хода подбора: значок, что пробовали, чем кончилось.</summary>
public sealed class FixStepRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _glyph = string.Empty;
    private Brush _tone = Brushes.Transparent;
    private string _detail = string.Empty;

    public string Glyph
    {
        get => _glyph;
        set => Set(ref _glyph, value);
    }

    public Brush Tone
    {
        get => _tone;
        set => Set(ref _tone, value);
    }

    public required string Title { get; init; }

    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    public Thickness Indent { get; init; }

    public FontWeight Weight { get; init; } = FontWeights.Normal;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }
}
