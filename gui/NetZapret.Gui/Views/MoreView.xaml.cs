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

/// <summary>Выключатель без своего раздела.</summary>
public sealed record FlagRow(string Key, string Name, string Note)
{
    public bool On { get; set; }

    /// <summary>
    /// Состояние словом рядом с тумблером.
    /// </summary>
    /// <remarks>
    /// Короче прежнего — «вкл.» вместо «включено», — потому что рядом
    /// теперь стоит тумблер и слово его лишь подтверждает. Подтверждение
    /// нужно: положение кружка и оттенок теряются при беглом взгляде
    /// и исчезают вовсе у тех, кто плохо различает цвета.
    /// </remarks>
    public string State => On ? "вкл." : "выкл.";
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
    public MoreView()
    {
        InitializeComponent();

        ShowAbout();

        Loaded += (_, _) => Reload();
    }

    /// <summary>
    /// Плитки тем: встроенные первыми, затем из папки themes\.
    /// </summary>
    private void ShowTheme(AppSettings settings)
    {
        var loads = ThemeLoader.LoadAll();
        var current = Themes.Current;

        ThemeList.ItemsSource = loads
            .Select(load => ThemeTile.From(load, load.Id == current, key => (Brush)FindResource(key)))
            .ToList();

        var bad = loads.Where(l => !l.Ok).ToList();

        ThemeHint.Text = @"Своя тема — папка в themes\ рядом с программой: цвета, шрифты и фон. "
            + @"Формат описан в themes\README.md."
            + (bad.Count == 0
                ? string.Empty
                : $" Не применяются: {string.Join(", ", bad.Select(b => b.Theme?.Name ?? b.Id))} — "
                  + "наведите на плитку, чтобы увидеть почему.");

        // Настройка просила одну тему, а применилась другая — сказать сразу,
        // иначе человек решит, что выбор не сохранился.
        if (settings.Theme is { Length: > 0 } wanted && wanted != current)
            Status.Text = $"Тема «{wanted}» не применилась — стоит встроенная. Причина — в подсказке её плитки.";

        ShowLook(settings, loads.FirstOrDefault(l => l.Id == current)?.Theme);

        // У встроенной «Изменить» создаёт новую — подпись говорит это заранее.
        EditThemeButton.Content = ThemeLoader.Shipped.Contains(current) ? "Изменить копию" : "Изменить";
        ShowFonts(settings);
    }

    private bool _showingLook;

    /// <summary>Настройки оформления — только если у темы есть картинка фона.</summary>
    private void ShowLook(AppSettings settings, Theme? theme)
    {
        bool hasBackground = theme?.Background is not null;

        LookPanel.Visibility = hasBackground ? Visibility.Visible : Visibility.Collapsed;
        LookNone.Visibility = hasBackground ? Visibility.Collapsed : Visibility.Visible;

        BackgroundSwitch.IsChecked = settings.ThemeBackgroundShown;
        BlurSwitch.IsChecked = settings.ThemeBlur;

        // Размытию и затемнению без картинки делать нечего — видны,
        // но приглушены, чтобы было понятно, от чего они зависят.
        BlurRow.IsEnabled = settings.ThemeBackgroundShown;
        DimRow.IsEnabled = settings.ThemeBackgroundShown;
        DimRow.Opacity = settings.ThemeBackgroundShown ? 1 : 0.5;

        _showingLook = true;
        DimChoice.SelectedIndex = Math.Clamp(settings.ThemeDimSteps, 0, 2);
        _showingLook = false;
    }

    private const string ThemeFont = "как в теме";

    /// <summary>Шрифт в списке: имя и само начертание — выбирают глазами.</summary>
    public sealed record FontItem(string Name, FontFamily Family)
    {
        public override string ToString() => Name;
    }

    /// <summary>Установленные шрифты — один раз на запуск: перечень не меняется, а читается долго.</summary>
    private static readonly Lazy<IReadOnlyList<FontItem>> SystemFonts = new(() =>
        Fonts.SystemFontFamilies
            .Where(f => !string.IsNullOrWhiteSpace(f.Source))
            .GroupBy(f => f.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FontItem(g.Key, g.First()))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());

    /// <summary>Списки шрифтов и размеров — с выбранным сейчас.</summary>
    private void ShowFonts(AppSettings settings)
    {
        _showingLook = true;

        try
        {
            var fonts = new List<FontItem> { new(ThemeFont, (FontFamily)FindResource("UiFont")) };
            fonts.AddRange(SystemFonts.Value);

            UiFontChoice.ItemsSource = fonts;
            UiFontChoice.SelectedItem = fonts.FirstOrDefault(f =>
                string.Equals(f.Name, settings.UiFont, StringComparison.OrdinalIgnoreCase)) ?? fonts[0];

            var scales = Appearance.Scales.Select(s => $"{s * 100:0} %").ToList();
            ScaleChoice.ItemsSource = scales;

            int nearest = Appearance.Scales
                .Select((s, i) => (Distance: Math.Abs(s - settings.UiScale), Index: i))
                .MinBy(x => x.Distance).Index;

            ScaleChoice.SelectedIndex = nearest;
        }
        finally
        {
            _showingLook = false;
        }
    }

    private void OnFontChoice(object sender, SelectionChangedEventArgs e)
    {
        if (_showingLook)
            return;

        static string? Pick(ComboBox box) =>
            box.SelectedItem is FontItem { Name: var name } && name != ThemeFont ? name : null;

        var scale = ScaleChoice.SelectedIndex is int i && i >= 0 && i < Appearance.Scales.Count
            ? Appearance.Scales[i]
            : 1.0;

        ApplyLook(AppSettings.Load(AppSettings.DefaultPath) with
        {
            UiFont = Pick(UiFontChoice),
            UiScale = scale,
        });
    }

    private void OnLook(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ApplyLook(key == "blur"
            ? settings with { ThemeBlur = !settings.ThemeBlur }
            : settings with { ThemeBackgroundShown = !settings.ThemeBackgroundShown });
    }

    private void OnDim(object sender, SelectionChangedEventArgs e)
    {
        if (_showingLook || DimChoice.SelectedIndex < 0)
            return;

        ApplyLook(AppSettings.Load(AppSettings.DefaultPath) with { ThemeDimSteps = DimChoice.SelectedIndex });
    }

    /// <summary>Записывает и сразу применяет: тема пересобирается с новыми настройками.</summary>
    private void ApplyLook(AppSettings settings)
    {
        try
        {
            settings.Save(AppSettings.DefaultPath);

            var result = Themes.Apply(Themes.Current, settings);

            Status.Text = result.Ok
                ? string.Empty
                : "Тема не применилась с этими настройками: " + string.Join("; ", result.Problems.Take(2));

            ShowTheme(settings);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось применить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Применяет тему сразу, без перезапуска.
    /// </summary>
    /// <remarks>
    /// Подменяется один словарь ресурсов, а стили опираются только на его
    /// ключи. Записывается в настройки только применившаяся тема: иначе
    /// следующий запуск молча открылся бы встроенной, а в настройках
    /// значилась бы та, что не прошла проверку.
    /// </remarks>
    private void OnTheme(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        try
        {
            var result = Themes.Apply(id);

            if (result.Ok)
            {
                (AppSettings.Load(AppSettings.DefaultPath) with { Theme = id })
                    .Save(AppSettings.DefaultPath);

                Status.Text = string.Empty;
            }
            else
            {
                Status.Text = $"Тема «{id}» не применена: {string.Join("; ", result.Problems.Take(3))}"
                    + (result.Problems.Count > 3 ? $" и ещё {result.Problems.Count - 3}." : ".");
            }

            ShowTheme(AppSettings.Load(AppSettings.DefaultPath));
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сменить тему: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Тема, выбранная сейчас, — с чего начинает редактор.</summary>
    private static Theme? CurrentTheme()
    {
        var load = ThemeLoader.Load(Themes.Current);

        return load.Theme ?? ThemeLoader.Load(Themes.DefaultId).Theme;
    }

    private void OnCreateTheme(object sender, RoutedEventArgs e) => OpenEditor(editing: false);

    private void OnEditTheme(object sender, RoutedEventArgs e) => OpenEditor(editing: true);

    /// <summary>
    /// Редактор темы. «Изменить» у встроенной ведёт себя как «Создать»:
    /// встроенные не перезаписываются, и сказать это лучше заранее.
    /// </summary>
    private void OpenEditor(bool editing)
    {
        if (CurrentTheme() is not { } theme)
        {
            Status.Text = "Не нашлось темы, с которой начать: папки themes нет рядом с программой.";
            return;
        }

        bool shipped = ThemeLoader.Shipped.Contains(theme.Id);

        var window = new ThemeEditorWindow(theme, editing && !shipped ? theme.Id : null)
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() == true && window.SavedId is { } id)
        {
            Status.Text = window.SavedAsCopy
                ? $"Правка не читается — сохранена черновиком в themes\\{id}, рабочая тема не тронута."
                : editing && shipped
                    ? $"Встроенная тема не меняется — правка сохранена новой: themes\\{id}."
                    : $"Сохранено в themes\\{id} и применено.";
        }

        ShowTheme(AppSettings.Load(AppSettings.DefaultPath));
    }

    private void OnExportTheme(object sender, RoutedEventArgs e)
    {
        var id = Themes.Current;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Выгрузить тему",
            FileName = $"netzapret-{id}.zip",
            Filter = "Тема NetZapret (*.zip)|*.zip",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            ThemeWriter.Export(id, dialog.FileName);
            Status.Text = $"Тема выгружена: {dialog.FileName}. Её можно передать — загружается кнопкой «Загрузить из файла…».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось выгрузить: " + ex.GetBaseException().Message;
        }
    }

    private void OnImportTheme(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Загрузить тему",
            Filter = "Тема NetZapret (*.zip)|*.zip",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        try
        {
            var id = ThemeWriter.Import(dialog.FileName);
            var load = ThemeLoader.Load(id);

            Status.Text = load.Ok
                ? $"Загружена «{load.Theme!.Name}» — выберите её плиткой."
                : $"Загружена в themes\\{id}, но не применится: {string.Join("; ", load.Problems.Take(2))}.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось загрузить: " + ex.GetBaseException().Message;
        }

        ShowTheme(AppSettings.Load(AppSettings.DefaultPath));
    }

    private void OnThemesFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ThemeLoader.DefaultRoot);
            Process.Start(new ProcessStartInfo { FileName = ThemeLoader.DefaultRoot, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть папку тем: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Открывает мастер первого запуска заново.
    /// </summary>
    /// <remarks>
    /// Только снимает отметку «пройден» и просит окно показать мастер —
    /// ничего из уже настроенного (подписку, режим, пресет) не трогает.
    /// Тот же путь, каким мастер и заканчивается сам, только в обратную
    /// сторону: <see cref="AppSettings.OnboardingDone"/> обратно в false.
    /// </remarks>
    private void OnRestartOnboarding(object sender, RoutedEventArgs e)
    {
        try
        {
            (AppSettings.Load(AppSettings.DefaultPath) with { OnboardingDone = false })
                .Save(AppSettings.DefaultPath);

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

        ShowFlags(settings);
        ShowRoutes();
        ShowTheme(settings);

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

            Status.Text = "Записано. Применится при следующем запуске движков.";
            this.Offer("Настройка изменена");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
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
}
