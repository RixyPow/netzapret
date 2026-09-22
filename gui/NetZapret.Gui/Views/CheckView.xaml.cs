using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Строка одной проверенной цели.</summary>
public sealed record CheckRow(
    string Host,
    string Mark,
    Visibility MarkShown,
    string Tcp,

    /// <summary>Версия, на которой сошлись, либо причина отказа.</summary>
    string Tls,

    string Http,
    string Data,
    string Verdict,
    Brush Color,
    string Why,
    Visibility WhyShown,

    /// <summary>Чем имя выведено из-под десинка; пусто — ничем.</summary>
    string Bypass,
    Visibility BypassShown,

    /// <summary>
    /// Есть ли по этой строке что делать.
    /// </summary>
    /// <remarks>
    /// Берётся у самого отчёта, а не выводится из текста вердикта. Прежде
    /// итог отбирал строки сравнением со словами «доступен» и «нет адреса
    /// у имени», и стоило появиться третьему виду, по которому лечить нечего,
    /// как он попал в раздел «что с этим делать». Так «сторона не даёт наш
    /// TLS» и оказалось среди дел: браузер умеет больше версий, чем наша
    /// проба, и человеку там делать нечего.
    /// </remarks>
    bool Actionable);

/// <summary>Раздел итога под таблицей.</summary>
public sealed record SectionRow(string Title, string Body, Brush Color);

/// <summary>
/// Проверка блокировок: что закрыто и чем лечится.
/// </summary>
/// <remarks>
/// <para>
/// Главное, что отличает нас от клиента VPN, и потому показано целиком —
/// таблицей с колонками, а не зелёной галочкой. Каждое утверждение здесь
/// выводится из замера, который виден рядом.
/// </para>
/// <para>
/// Считает <see cref="BlockCheck"/> из библиотеки, тот же, что у консоли.
/// Расходится только показ: там строки печатаются по мере готовности,
/// здесь заполняется таблица.
/// </para>
/// </remarks>
public partial class CheckView : UserControl
{
    /// <summary>Сколько проб держать в воздухе. То же число, что в консоли.</summary>
    /// <remarks>
    /// Четыре, а не восемь: при восьми проверка топила сама себя — последняя
    /// проба каждой цели тянет до четверти мегабайта, и восемь таких закачек
    /// разом съедают полосу, в которой соседи ждут ответа свои четыре секунды.
    /// </remarks>
    private const int Parallelism = 4;

    /// <summary>
    /// Строки и ход проверки живут дольше самого раздела.
    /// </summary>
    /// <remarks>
    /// Раздел пересоздаётся при каждом переходе по меню — иначе он показывал бы
    /// снимок прошлого захода. Но проверка идёт минуты, и обрывать её оттого,
    /// что человек заглянул в журнал, значит выбросить всю работу и заставить
    /// начать заново. Поэтому набранное и признак «идёт» общие, а раздел
    /// к ним подключается.
    /// </remarks>
    private static readonly ObservableCollection<CheckRow> Collected = [];

    /// <summary>
    /// Имена без адреса — их в таблице не показываем.
    /// </summary>
    /// <remarks>
    /// Считаются отдельно и называются под таблицей: пропустить строку молча
    /// хуже, чем показать бесполезную. Человек должен видеть, что имя
    /// проверялось и почему о нём нечего сказать.
    /// </remarks>
    private static readonly List<string> _markers = [];

    /// <summary>
    /// Сами отчёты, а не строки таблицы.
    /// </summary>
    /// <remarks>
    /// Строка таблицы несёт вердикт словами, и разбирать её обратно, чтобы
    /// узнать вид блокировки, значило бы сверять текст — ровно то, на чём
    /// итог уже однажды разошёлся с консолью. Разделы, которым нужен вид,
    /// берут его у отчёта.
    /// </remarks>
    private static readonly List<TargetReport> _reports = [];

    /// <summary>Длина журнала движка на начало прогона.</summary>
    private static long _logMark;

    /// <summary>Обстановка замера: через что ходим и куда выходим.</summary>
    private static SectionRow? _setupRow;

    /// <summary>Она же, но всегда чем-то заполненная — для показа.</summary>
    private SectionRow _setup => _setupRow ?? new SectionRow(
        "Обстановка не выяснена",
        "Через что шёл замер, узнать не удалось.",
        (Brush)FindResource("Muted"));

    /// <summary>Состояние туннеля на момент прогона.</summary>
    private static TunnelState _tunnel = TunnelState.Unknown;

    /// <summary>
    /// Секции действующего пресета, разобранные один раз на прогон.
    /// </summary>
    /// <remarks>
    /// Собираются заранее, а не на каждую строку: сборка открывает каждый
    /// список, на который ссылается пресет, а их дюжина на дюжину файлов.
    /// Делать это по сто двадцать раз значило бы прочитать одно и то же
    /// полторы тысячи раз за прогон.
    /// </remarks>
    private static PresetZones? _zones;

    private static CancellationTokenSource? _work;
    private static bool _running;
    private static IReadOnlyList<SectionRow> _sections = [];
    private static string _status = "Проверка идёт минуты: по каждому имени четыре пробы, и каждая ждёт ответа.";

    /// <summary>Цель точечной проверки; <c>null</c> — проверяется весь справочник.</summary>
    private static CheckTarget? _target;

    /// <summary>Полная глубина: по два имени с каждой части вместо одного.</summary>
    private static bool _deep;

    private const string QuickScope = "Быстрая";
    private const string FullScope = "Полная";
    private const string SpotScope = "Точечная";

    /// <summary>Пока список заполняется, выбор в нём не считается выбором человека.</summary>
    private bool _filling;

    public CheckView()
    {
        InitializeComponent();

        Rows.ItemsSource = Collected;

        Loaded += (_, _) =>
        {
            ShowSetup();
            ShowScopes();

            // Возвращаемся к тому, что успело набраться, и к своему состоянию
            // кнопок: уйти и вернуться не должно выглядеть как «ничего не было».
            Status.Text = _status;
            Sections.ItemsSource = _sections;
            Header.Visibility = Collected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            RunButton.IsEnabled = !_running;
            StopButton.IsEnabled = _running;
        };
    }

    /// <summary>Заполняет список сервисов и восстанавливает выбранный.</summary>
    private void ShowScopes()
    {
        _filling = true;

        try
        {
            // Только глубина: быстрая — по имени с каждой части, полная —
            // по два, точечная — одна цель целиком. Прежде под этими двумя
            // лежали ещё и два десятка сервисов одной лентой, и список
            // отвечал сразу на два разных вопроса — насколько подробно
            // и что именно, — заставляя пролистывать чужое ради своего.
            Scope.ItemsSource = new List<string> { QuickScope, FullScope, SpotScope };
            Scope.SelectedIndex = Selected();

            RunButton.Content = ChooseLabel();
        }
        finally
        {
            _filling = false;
        }
    }

    private void OnScope(object sender, SelectionChangedEventArgs e)
    {
        if (_filling)
            return;

        if (Scope.SelectedIndex == 2)
        {
            AskTarget();
            return;
        }

        _deep = Scope.SelectedIndex == 1;
        _target = null;

        RunButton.Content = ChooseLabel();
    }

    /// <summary>
    /// Спрашивает, что проверять точечно.
    /// </summary>
    /// <remarks>
    /// Отказ возвращает список к прежнему выбору: «точечная» без цели
    /// не значит ничего, и оставить её выбранной означало бы кнопку,
    /// которой нечего проверять.
    /// </remarks>
    private void AskTarget()
    {
        var picker = new TargetPicker { Owner = Window.GetWindow(this) };

        if (picker.ShowDialog() == true && picker.Chosen is { } chosen)
            _target = chosen;

        _filling = true;

        try
        {
            Scope.SelectedIndex = Selected();
        }
        finally
        {
            _filling = false;
        }

        RunButton.Content = ChooseLabel();
    }

    private static int Selected() => _target is not null ? 2 : _deep ? 1 : 0;

    private string ChooseLabel()
    {
        if (_target is null)
            return _deep ? "Проверить полностью" : "Проверить";

        var what = _target.Describe;

        // Длинные имена режутся: кнопка стоит в ряду с выбором глубины
        // и «Прервать», и растянуть её значит сдвинуть оба.
        return "Проверить " + (what.Length > 18 ? what[..17] + "…" : what);
    }

    /// <summary>
    /// Говорит, в какой обстановке снят замер.
    /// </summary>
    /// <remarks>
    /// При работающем обходе видно сеть уже вылеченной. Умолчать об этом
    /// значит выдать «доступно благодаря десинку» за «доступно и так».
    /// </remarks>
    private bool ShowSetup()
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool running = state is not null && state.IsSupervisorAlive();

        SetupCard.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        if (running)
        {
            SetupTitle.Text = "Движки работают — видно сеть уже с обходом";
            SetupBody.Text = "Чтобы увидеть, что закрыто на самом деле, остановите их в разделе "
                + "«Состояние» и повторите. Доступное сейчас может быть доступно как раз "
                + "благодаря обходу.";
        }

        return running;
    }

    private async void OnRun(object sender, RoutedEventArgs e)
    {
        bool running = ShowSetup();

        Collected.Clear();
        _markers.Clear();
        _reports.Clear();
        _sections = [];

        // Метка журнала снимается до первой пробы: всё, что движок напишет
        // после неё, относится к этому прогону, а всё, что было раньше, —
        // к прошлым запускам и к делу не относится.
        _logMark = EngineLog.Position();
        Sections.ItemsSource = null;
        Header.Visibility = Visibility.Visible;

        _work?.Cancel();
        _work = new CancellationTokenSource();
        _running = true;

        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;

        bool interrupted = false;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var zapretRoot = ZapretPaths.Discover()?.Root;

            RuleEngine? engine = null;

            try
            {
                engine = RuleSetLoader.LoadLayered(
                    settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

                RuleSetExpander.Expand(engine.RuleSet, zapretRoot);
            }
            catch (Exception)
            {
                // Без правил проверка всё равно осмысленна: она мерит сеть,
                // а не настройку. Пометок «через туннель» просто не будет.
            }

            _zones = null;

            try
            {
                if (settings.PresetName is { } name && ZapretPaths.FindPreset(name) is { } path)
                    _zones = PresetZones.Build(new PresetReader().Load(path), zapretRoot);
            }
            catch (Exception)
            {
                // Пресета может не быть — в режиме «только VPN» он не запускается,
                // да и файл могли удалить. Тогда строк про секции просто не будет.
            }

            var targets = Targets(zapretRoot, _target, _deep);

            if (targets.Count == 0)
            {
                Say(_target is null
                    ? "Проверять нечего: ни один список доменов не прочитался."
                    : $"У «{_target.Describe}» нет доменных частей — проверять нечего. "
                      + "Такой сервис задан подсетями, и стучаться в подсеть наугад не проверка.");

                Header.Visibility = Visibility.Collapsed;
                return;
            }

            Say(_target is not null
                ? $"Проверяю «{_target.Describe}»: {targets.Count} имён, по каждому четыре пробы."
                : _deep
                    ? $"Полная проверка: {targets.Count} имён, по два с каждой части, "
                      + "по каждому четыре пробы."
                    : $"Проверяю {targets.Count} — по каждому четыре пробы.");

            // Обстановка выясняется до проб, а не после. Мёртвый туннель
            // обесценивает вердикты по проксируемым именам, и знать об этом
            // надо раньше, чем они появятся на экране.
            await ReadSetupAsync(settings, running, _work.Token);

            await RunAsync(targets, engine, running && settings.NeedsProxy, _work.Token);
            await ReprobeAloneAsync(engine, running && settings.NeedsProxy, _work.Token);

            await Summarise(engine);
        }
        catch (OperationCanceledException)
        {
            interrupted = true;
            Say($"Прервано. Успело проверить {Collected.Count}.");
        }
        catch (Exception ex)
        {
            Say("Проверка сорвалась: " + ex.GetBaseException().Message);
        }
        finally
        {
            // Признак снимается здесь, а не только у кнопок. Прежде он
            // оставался поднятым навсегда, и раздел, пересозданный при
            // возврате на вкладку, восстанавливал по нему состояние кнопок:
            // проверка давно закончилась, а «Проверить» больше не нажималась.
            _running = false;

            RunButton.IsEnabled = true;
            StopButton.IsEnabled = false;

            // Прерванная проверка тоже пишется: набранное до остановки —
            // такой же замер, а переделывать его ради файла значит потратить
            // ещё столько же времени.
            if (SaveReport(running, interrupted) is { } note)
                Say(_status + "  " + note);
        }
    }

    /// <summary>Сколько отчётов держать. То же число, что у консоли.</summary>
    private const int KeepReports = 20;

    /// <summary>
    /// Записывает отчёт и возвращает, чем это кончилось.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Запись идёт всегда, а не по просьбе, и по той же причине, что в консоли:
    /// отчёт затем и нужен, чтобы его показать, — а до сих пор таблицу
    /// переносили выделением мышью, теряя половину при прокрутке.
    /// </para>
    /// <para>
    /// Имя со временем замера, а не одно на все прогоны: сравнить «было —
    /// стало» это половина разбора, потому что сайт ломается между двумя
    /// проверками, а не во время одной.
    /// </para>
    /// </remarks>
    private string? SaveReport(bool enginesRunning, bool interrupted)
    {
        if (Collected.Count == 0)
            return null;

        try
        {
            var full = Path.GetFullPath(Path.Combine(
                "reports", $"blockcheck-{DateTime.Now:yyyy-MM-dd-HHmm}.txt"));

            Directory.CreateDirectory(Path.GetDirectoryName(full)!);

            // Без метки порядка байтов: файл читают и Windows, и веб-формы,
            // куда его прикладывают.
            File.WriteAllText(full, BuildReport(enginesRunning, interrupted), new UTF8Encoding(false));

            KeepRecentReports();

            return $"Отчёт: {full}";
        }
        catch (Exception ex)
        {
            return "Отчёт записать не вышло: " + ex.GetBaseException().Message;
        }
    }

    private string BuildReport(bool enginesRunning, bool interrupted)
    {
        var text = new StringBuilder();

        text.AppendLine($"Проверка блокировок — {DateTime.Now:dd.MM.yyyy HH:mm}");
        text.AppendLine(new string('=', 78));
        text.AppendLine();
        text.AppendLine($"Охват:   {_target?.Describe ?? (_deep ? "все сервисы, полная" : "все сервисы, быстрая")}");

        // Состояние движков в отчёте обязательно: без него цифры нечитаемы.
        // Доступное при работающем обходе может быть доступно как раз
        // благодаря ему, и через неделю этого уже не вспомнить.
        text.AppendLine(enginesRunning
            ? "Движки:  работали — сеть видна уже с обходом"
            : "Движки:  остановлены — видно, что закрыто на самом деле");

        text.AppendLine($"Имён:    {Collected.Count}{(interrupted ? " (проверка прервана)" : string.Empty)}");
        text.AppendLine();

        text.AppendLine(
            Fit("ИМЯ", 38) + Fit("TCP", 8) + Fit("TLS", 8)
            + Fit("ПОРТ 80", 9) + Fit("ДАННЫЕ", 9) + "ВЕРДИКТ");

        text.AppendLine(new string('-', 78));

        foreach (var row in Collected)
        {
            var name = row.MarkShown == Visibility.Visible ? $"{row.Host} [{row.Mark}]" : row.Host;

            text.AppendLine(
                Fit(name, 38) + Fit(row.Tcp, 8) + Fit(row.Tls, 8)
                + Fit(row.Http, 9) + Fit(row.Data, 9) + row.Verdict);
        }

        if (_markers.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(
                $"Пропущено маркеров зон: {_markers.Count} — {string.Join(", ", _markers)}. "
                + "Записи A у них нет ни у кого: работают только поддомены, "
                + "а само имя стоит в списке, чтобы покрыть зону целиком.");
        }

        // Имена без объяснения, но выведенные из-под десинка, тоже сюда:
        // отчёт читают через неделю, и файл, умалчивающий про исключение,
        // вводит в заблуждение ровно так же, как вводило окно.
        var explained = Collected
            .Where(r => (r.WhyShown == Visibility.Visible && !string.IsNullOrWhiteSpace(r.Why))
                || !string.IsNullOrWhiteSpace(r.Bypass))
            .ToList();

        if (explained.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("ПОЧЕМУ");
            text.AppendLine(new string('-', 78));

            // Пометка идёт первой: она отменяет совет, а не дополняет его.
            foreach (var row in explained)
                text.AppendLine($"{row.Host}: {Explain(row)}");
        }

        if (_sections.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("ЧТО С ЭТИМ ДЕЛАТЬ");
            text.AppendLine(new string('-', 78));

            foreach (var section in _sections)
            {
                text.AppendLine();
                text.AppendLine(section.Title);
                text.AppendLine(section.Body);
            }
        }

        return text.ToString();
    }

    /// <summary>Подрезает и добивает значение до ширины колонки.</summary>
    private static string Fit(string? value, int width)
    {
        var text = value ?? string.Empty;

        if (text.Length >= width)
            text = text[..(width - 1)];

        return text.PadRight(width);
    }

    /// <summary>
    /// Убирает старые отчёты, оставляя последние.
    /// </summary>
    /// <remarks>
    /// Папка, растущая без предела, однажды станет поводом вычистить её
    /// целиком — вместе с тем отчётом, ради которого всё и затевалось.
    /// </remarks>
    private static void KeepRecentReports()
    {
        try
        {
            var directory = new DirectoryInfo("reports");

            if (!directory.Exists)
                return;

            foreach (var file in directory.GetFiles("blockcheck-*.txt")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepReports))
            {
                file.Delete();
            }
        }
        catch (Exception)
        {
            // Уборка не стоит того, чтобы из-за неё пропал только что
            // записанный отчёт.
        }
    }

    /// <summary>Говорит и запоминает: раздел пересоздаётся, а сказанное должно пережить уход.</summary>
    private void Say(string text)
    {
        _status = text;
        Status.Text = text;
    }

    /// <summary>
    /// Прерывает проверку.
    /// </summary>
    /// <remarks>
    /// Отзыв обязателен: пробы, уже ушедшие в сеть, досматриваются до своего
    /// таймаута, и это секунды. Прежде кнопка молча ничего не показывала, и
    /// отличить «прерываю» от «зависло» было нельзя — жали ещё раз.
    /// </remarks>
    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        Say("Прерываю — жду, пока вернутся уже начатые пробы…");

        _work?.Cancel();
    }

    /// <summary>
    /// Имена, которые в отчёт не идут.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Повторяет набор консоли — в интерфейсе решено дублировать, а не тянуть
    /// общий. Расхождение здесь стоит дорого: одна и та же машина в двух видах
    /// программы выдавала бы два разных отчёта, и верить нельзя было бы ни
    /// одному.
    /// </para>
    /// <para>
    /// Две причины попадания. Голые зоны сетей доставки — записи A у них нет
    /// и не было, работают только поддомены, и «нет адреса у имени» в каждом
    /// отчёте чистый шум; список измерен на 1.1.1.1 2026-09-04, а не составлен
    /// на глаз. И три имени, снятых по решению владельца проекта: у него они
    /// работают, а отчёт третий прогон подряд утверждал обратное.
    /// </para>
    /// </remarks>

    /// <summary>
    /// Что проверять.
    /// </summary>
    /// <remarks>
    /// <para>
    /// По всему справочнику — по имени с части: сорок имён, и это уже минуты.
    /// По одному сервису — по четыре: там, где чинят конкретную поломку,
    /// глубина важнее охвата, а времени на неё уходит меньше, чем на полный
    /// прогон.
    /// </para>
    /// <para>
    /// Пропущенное имя расходует свой слот. Иначе пропуск втягивает в проверку
    /// имя глубже по списку — часто такое же бесполезное: у GitHub вместо
    /// githubusercontent.com проверялся бы githubassets.com. Замалчивая одну
    /// пустую строку, набирали бы другую.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<(string Host, string Service)> Targets(
        string? zapretRoot,
        CheckTarget? target,
        bool deep)
    {
        // Своё имя проверяется как есть: справочник о нём ничего не знает,
        // раскладывать его по частям не на что, а спросили именно про него.
        if (target?.Host is { } own)
            return [(own.TrimStart('*', '.'), "своё имя")];

        var only = target?.Service;

        // Одно имя с части — быстрая; два — полная. Второе имя и отделяет
        // случайную неудачу одного хоста от закрытой части целиком: одно имя
        // ошибается само по себе, два подряд — уже нет. Для одного сервиса
        // берётся четыре: там время есть, а подробность и есть смысл выбора.
        int perPart = only is not null ? 4 : deep ? 2 : 1;

        var targets = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in ServiceCatalog.All)
        {
            if (only is not null && !string.Equals(service.Name, only, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var part in service.Parts)
            {
                // Адресные части проверять нечем: у них нет имени, а стучаться
                // в подсеть наугад — не проверка.
                if (part.ByAddress)
                    continue;

                int taken = 0;

                foreach (var domain in HostListReader.Read(part.List, zapretRoot, out _))
                {
                    if (taken == perPart)
                        break;

                    var host = domain.TrimStart('*', '.');

                    if (NotWorthChecking.Contains(host))
                    {
                        taken++;
                        continue;
                    }

                    if (!seen.Add(host))
                        continue;

                    targets.Add((host, $"{service.Name} · {part.Name}"));
                    taken++;
                }
            }
        }

        return targets;
    }

    private async Task RunAsync(
        IReadOnlyList<(string Host, string Service)> targets,
        RuleEngine? engine,
        bool tunnelInUse,
        CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(Parallelism);

        // Имена, выведенные из-под десинка. Считается раз на прогон, а не
        // на строку: вызов читает hosts с диска и проходит весь набор правил.
        //
        // Тем же вызовом, каким они пишутся движку в runtime\desync-exclude.txt
        // при запуске. Посчитай мы здесь по-своему, окно объясняло бы замер
        // не тем, что winws2 на самом деле получил.
        IReadOnlyList<(string Name, DesyncBypass Why)> bypassed = engine is null
            ? []
            : HostsFile.DescribeDesyncExclusions(engine.RuleSet, tunnelUp: tunnelInUse);

        var running = targets.Select(async target =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                bool tunnelled = tunnelInUse && engine is not null && Proxied(engine, target.Host);

                var report = await BlockCheck.CheckAsync(
                    target.Host, target.Service, cancellationToken, tunnelled);

                Dispatcher.Invoke(() =>
                {
                    // Маркеры зон в таблицу не идут. Записи A у akamai.net,
                    // scdn.co, playstation.net нет и не было ни у кого:
                    // работают только их поддомены, а само имя стоит в списке
                    // затем, чтобы покрыть зону целиком. Проверять там нечего,
                    // и строка «нет адреса у имени» среди закрытого только
                    // сбивает — её начинают чинить.
                    //
                    // Из списков такие имена убирать нельзя: они там несут
                    // работу, а не показ. Пропущенные считаются и называются
                    // под таблицей, чтобы их отсутствие не было тихим.
                    if (report.Kind == BlockKind.NoAddress)
                    {
                        _markers.Add(report.Host);
                        Say($"Проверено {Collected.Count} из {targets.Count}…");

                        return;
                    }

                    _reports.Add(report);

                    Collected.Add(Row(report, tunnelled, HostsFile.BypassFor(bypassed, target.Host)));
                    Say($"Проверено {Collected.Count} из {targets.Count}…");
                });
            }
            finally
            {
                slots.Release();
            }
        });

        try
        {
            await Task.WhenAll(running);
        }
        catch (OperationCanceledException)
        {
            // Прерывание — штатный исход. Что успели, то и покажем.
        }
    }

    /// <summary>
    /// Перепроверяет в тишине то, что могло провалиться от тесноты.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Консоль так делала с самого начала, окно — нет, и это стоило прямой
    /// ошибки в отчёте: itch.io числился оборванным, работая. Он отдаёт
    /// страницу chunked на сто одиннадцать килобайт и честно закрывает
    /// соединение — замер 2026-09-16 в одиночку доходит целиком. Но проверка
    /// гонит сотню имён разом, и трёхсекундная пауза посреди такой страницы
    /// набегает от соседей по прогону, а не от сети.
    /// </para>
    /// <para>
    /// Перепроверяются только вердикты, которые теснота способна подделать:
    /// они ставятся по неполученному ответу. Заглушка в DNS и отказ по стране
    /// получены по ответу, который пришёл, — такое теснота не подделает.
    /// Список решает сам отчёт через <see cref="TargetReport.MayBeCrowding"/>.
    /// </para>
    /// <para>
    /// По одному и с бюджетом: перепроверка идёт после полного прогона,
    /// и превращать её во второй такой же прогон незачем — она нужна, чтобы
    /// снять обвинение с немногих, а не чтобы удвоить ожидание.
    /// </para>
    /// </remarks>
    private async Task ReprobeAloneAsync(RuleEngine? engine, bool tunnelInUse, CancellationToken cancellationToken)
    {
        var suspect = _reports.Where(r => r.Actionable && r.MayBeCrowding).Take(12).ToList();

        if (suspect.Count == 0)
            return;

        Say($"Перепроверяю в тишине: {suspect.Count}…");

        var budget = System.Diagnostics.Stopwatch.StartNew();

        foreach (var crowded in suspect)
        {
            if (cancellationToken.IsCancellationRequested || budget.Elapsed > TimeSpan.FromSeconds(90))
                break;

            bool tunnelled = tunnelInUse && engine is not null && Proxied(engine, crowded.Host);

            var alone = await BlockCheck.ProbeOnceAsync(
                crowded.Host, crowded.Service, cancellationToken, tunnelled);

            var settled = BlockCheck.Reconcile(crowded, alone);

            if (settled.Kind == crowded.Kind)
                continue;

            // Строка в таблице заменяется на месте: вердикт, признанный
            // недостоверным, не должен остаться на экране рядом с исправленным.
            int at = _reports.IndexOf(crowded);

            if (at >= 0)
                _reports[at] = settled;

            int row = Collected.ToList().FindIndex(r => r.Host == crowded.Host);

            if (row >= 0)
                Collected[row] = Row(settled, tunnelled, DesyncBypass.None);
        }
    }

    private static bool Proxied(RuleEngine engine, string host) =>
        engine.Evaluate(new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemoteAddress = null,
            RemotePort = 443,
            Hostname = host,
        }).Mode == RoutingMode.Proxy;

    private CheckRow Row(TargetReport report, bool tunnelled, DesyncBypass bypass)
    {
        var key = report.Kind switch
        {
            BlockKind.None => "Accent",
            BlockKind.NoAddress => "Faint",

            // Жёлтый, а не красный. Гео-отказ — это работающая связь: ответ
            // пришёл, и пришёл от самого сайта. Красный ставил его в один ряд
            // с закрытым наглухо, а лечится он совсем иначе — не рецептом,
            // а выходом в другой стране.
            BlockKind.TlsDpi or BlockKind.Stall or BlockKind.GeoBlock => "Warn",

            // Отказ согласования — не поломка вовсе: сторона сама не даёт
            // тех версий, что мы пробуем, а браузер умеет больше.
            BlockKind.Handshake => "Faint",
            _ => "Danger",
        };

        // Причину выбирает сам отчёт — по той стадии, что решила вердикт.
        // Прежде здесь бралась первая непустая, и у имени с убитым
        // рукопожатием в строке оказывался рассказ про удавшийся TCP.
        var why = report.Why;

        // Имя, выведенное из-под десинка, проваливается и получает вердикт
        // «DPI по TLS» с советом «десинк» — которого к нему по нашему же
        // решению не применяют: список уходит в --hostlist-exclude, и winws2
        // отбрасывает имя раньше, чем начнёт разбирать секции. Со стороны
        // это неотличимо от неудачного рецепта, и вечер уходил на правку
        // секции, до которой дело не доходит. Теперь строка говорит об этом
        // сама.
        //
        // Только у провалившихся, как и в консоли: у «доступен» объяснять
        // нечего, а пометка на всех пятидесяти строках стала бы шумом.
        var note = report.Actionable ? HostsFile.DescribeBypass(bypass) : string.Empty;

        // Какая секция пресета взяла бы это имя. Без неё правка пресета —
        // угадывание: чинят секцию, до которой исполнение не доходит, потому
        // что раньше сработала другая, по большому списку. На этом обжигались
        // трижды за двое суток — голос Discord чинился секцией, до которой
        // не доходила очередь; пять новых секций встали ниже «Моих сайтов»
        // и не работали ни дня.
        //
        // Не показывается там, где имя выведено из-под десинка: список
        // исключений уходит в --hostlist-exclude, и winws2 отбрасывает имя
        // раньше, чем начнёт разбирать профили, — секция вычисляется
        // и ничего не значит.
        // Подмена DNS важнее всех прочих пометок и потому ставится раньше них.
        //
        // Она не уточняет вердикт, а отменяет его: до настоящего сайта мы
        // не дошли вовсе, и всё, что замерено, замерено на чужом узле.
        // Секция пресета к такому имени отношения не имеет — рецепт судит
        // по имени в приветствии, а подменён адрес, — и показать её значило бы
        // отправить чинить пресет там, где правится файл hosts.
        //
        // На этом я и обжёгся 19.09: мерил QUIC по адресу из подменённого
        // ответа и получал «QUIC не доходит» при исправном QUIC.
        if (report.Spoof.Spoofed)
        {
            // Лечения названо два, и оба верны. Подстановка адреса в hosts
            // короче и не требует туннеля; туннель длиннее, но работает,
            // когда настоящего адреса взять негде — на выходе имя разрешают
            // уже не из России. Чего здесь точно не надо, так это рецепта:
            // он судит по имени в приветствии, а подменён адрес.
            note = report.Spoof.Detail
                + " — рецептом десинка не лечится: прибейте настоящий адрес "
                + "во вкладке «Файл hosts» либо уведите имя в VPN";
        }
        else if (report.Actionable && note.Length == 0 && _zones is { } zones)
        {
            note = zones.MatchFor(report.Host) is { } match
                ? $"секция #{match.Ordinal} {match.Describe()}"
                : "секция: ни одна доменная не совпала";
        }

        // Правило ведёт в туннель, а замер прошёл мимо. Пометка важнее той,
        // что про десинк: она меняет смысл вердикта целиком — он описал
        // прямой путь, а не трубу, и чинить подписку по нему не туда.
        //
        // Тоже только у провалившихся, и это исправление. Условие стояло
        // без оглядки на Actionable, и в отчёте 20:45 семь открытых имён —
        // t.me, telegram.org, instagram.com, facebook.com, valheim.com,
        // linkedin.com, rutracker.org — получили в разделе «ПОЧЕМУ» строку
        // про туннель. У «доступен» объяснять нечего: имя открылось, и каким
        // путём оно шло, вердикта не меняет. Консоль так и делала с самого
        // начала — там пометка стоит после выхода по !Actionable, — и два
        // вида одного замера разошлись словами.
        if (report.Actionable && report.TunnelMissed)
        {
            note = "правило ведёт в VPN, а замер пошёл напрямую — вердикт не о туннеле"
                + (note.Length == 0 ? string.Empty : "; " + note);
        }

        return new CheckRow(
            report.Host,

            // Пометка ставится по замеру, а не по правилу: у имени, ушедшего
            // мимо туннеля, прежде всё равно стояло «чз».
            report.Tunnelled ? "чз" : string.Empty,
            report.Tunnelled ? Visibility.Visible : Visibility.Collapsed,
            report.Tcp.Describe(),
            report.DescribeTls(),
            report.Http.Describe(),
            report.DescribeData(),
            report.Describe(),
            (Brush)FindResource(key),
            why ?? string.Empty,
            report.Actionable && why is not null ? Visibility.Visible : Visibility.Collapsed,
            note,
            note.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
            report.Actionable);
    }

    /// <summary>Строка с пометкой про путь и про десинк — для сохраняемого отчёта.</summary>
    private static string Explain(CheckRow row) => (row.Bypass, row.Why) switch
    {
        ("", var why) => why,
        (var bypass, "") => bypass,
        var (bypass, why) => $"{bypass}; {why}",
    };

    /// <summary>
    /// Итог: по видам блокировок и по спорящим правилам.
    /// </summary>
    /// <remarks>
    /// Спорящие правила показываются даже когда всё открыто: правило, до
    /// которого не доходит очередь, — поломка, которая ждёт своего часа,
    /// и заметить её иначе нечем.
    /// </remarks>
    private async Task Summarise(RuleEngine? engine)
    {
        var sections = new List<SectionRow>();

        // По признаку, а не по тексту вердикта. Сверка со словами держалась,
        // пока видов «лечить нечего» было два — «доступен» и «нет адреса
        // у имени»; третий, «сторона не даёт наш TLS», в список слов никто
        // не дописал, и он попал в раздел «что с этим делать», где делать
        // нечего: браузер умеет больше версий, чем наша проба.
        var byKind = Collected
            .Where(r => r.Actionable)
            .GroupBy(r => r.Verdict)
            .OrderByDescending(g => g.Count())
            .ToList();

        sections.Add(byKind.Count == 0
            ? new SectionRow(
                "Всё открыто",
                _target is null
                    ? "Обходить нечего."
                    : $"У «{_target.Describe}» обходить нечего. Проверено вчетверо глубже полного прогона.",
                (Brush)FindResource("Accent"))
            : new SectionRow(
                "Итог",
                string.Join("\n", byKind.Select(g =>
                    $"{g.Key}: {g.Count()} — {string.Join(", ", g.Select(r => r.Host).Take(6))}")),
                (Brush)FindResource("Warn")));

        if (engine is not null)
        {
            var conflicts = RouteConflicts.Find(engine.RuleSet);

            if (conflicts.Count > 0)
            {
                sections.Add(new SectionRow(
                    "Правила спорят об одних и тех же именах",
                    string.Join("\n", conflicts.Take(8).Select(c =>
                        $"{c.Loser.Value} → {Describe(c.Loser.Mode)} перекрыто "
                        + $"{c.Winner.Value} → {Describe(c.Winner.Mode)}, например {c.Example}"))
                    + "\n\nПобеждает правило, до которого очередь доходит раньше. "
                    + "Перекрытое не применяется вовсе, хотя в файле есть.",
                    (Brush)FindResource("Warn")));
            }
        }

        // Обстановка замера — первой строкой и прежде вердиктов.
        //
        // Мёртвый туннель обесценивает всё, что сказано про проксируемые
        // имена: измерена труба, а не сайты. Случай стоил дня разбора —
        // у пользователя разом «сломались» WhatsApp и Telegram, проверка
        // показала на них блокировки и предложила менять маршруты, а в журнале
        // движка лежало четыре тысячи строк «timeout: no recent network
        // activity» и ни один из пятнадцати серверов не отвечал.
        //
        // Консоль говорила это с самого начала, окно — не говорило вовсе.
        sections.Insert(0, _setup);

        // Что сказал сам движок. Самый прямой источник, какой есть, и в окне
        // его до сих пор не было вовсе — только в консоли. Разница видна
        // на живом случае: 16 сентября четыре имени числились «туннель
        // не доставил», а движок за весь прогон не записал ни одной ошибки
        // соединения. Значит исходящий он поднял и байты провёз, а закрыла
        // рукопожатие удалённая сторона — то есть чинить надо не трубу.
        // Выяснять это пришлось чтением журнала руками, три захода подряд.
        var lost = _reports
            .Where(r => r.Kind == BlockKind.TunnelFailed)
            .Select(r => r.Host)
            .ToList();

        if (lost.Count > 0)
        {
            var complaints = EngineLog.Complaints(lost, path: null, since: _logMark);

            sections.Add(complaints.Count > 0
                ? new SectionRow(
                    "Что об этом сказал сам движок",
                    string.Join("\n", complaints.Select(c => $"{c.Host} через {c.Outbound}: {c.Error}"))
                    + "\n\nИмя движок разобрал верно — значит рукопожатие дошло до него целым, "
                    + "и десинк его не портил. Выход назван тот, через который шло на самом деле.",
                    (Brush)FindResource("Warn"))
                : new SectionRow(
                    "Движку жаловаться не на что",
                    "За этот прогон он не записал ни одной ошибки соединения по этим именам: "
                    + "исходящий поднят, байты провезены. Значит рукопожатие закрыла удалённая "
                    + "сторона, а не труба, и менять сервер подписки по этим строкам — не туда.\n\n"
                    + "Журнал: " + EngineLog.DefaultPath,
                    (Brush)FindResource("Muted")));
        }

        // Какие из проверенных имён прибиты — и потому измерены не там,
        // где кажется.
        //
        // Раздел «Файл hosts» это не заменяет и не дублирует: там показаны
        // пины, поставленные нами, а здесь читается весь файл целиком, вместе
        // с чужими. Чужие и опаснее: пины от редактора Zapret GUI на Canva,
        // RuTracker и LinkedIn однажды выглядели как неисправный VPN, и разбор
        // стоил дня.
        //
        // Смысл раздела не в списке, а в связи со строками таблицы. Пин бьёт
        // любой резолв, включая наш fakeip, поэтому вердикт по такому имени
        // описывает прибитый адрес, а не сайт. Вчера семь имён получили
        // пометку «правило ведёт в VPN, а замер пошёл напрямую» именно
        // поэтому — и чтобы это понять, пришлось читать hosts руками.
        var pinned = Pinned();

        if (pinned.Count > 0)
        {
            sections.Add(new SectionRow(
                $"Из проверенных прибиты в hosts: {pinned.Count}",
                string.Join("\n", pinned.Take(12).Select(p => $"{p.Key} → {p.Value}"))
                + (pinned.Count > 12 ? $"\n… и ещё {pinned.Count - 12}" : string.Empty)
                + "\n\nИх строки в таблице описывают прибитый адрес, а не сайт. Пин бьёт "
                + "любой резолв, включая наш: правило «через VPN» на такое имя молча "
                + "не работает, а десинк его не трогает вовсе.\n\nЗдесь читается весь файл, "
                + "вместе с чужими записями — свои показывает и снимает раздел «Файл hosts».",
                (Brush)FindResource("Muted")));
        }

        if (HostsEditor.WhoReplaced() is { } who)
        {
            sections.Add(new SectionRow(
                $"Файл hosts переписан: {who}",
                "Изменённый hosts он считает признаком заражения и вернул файл к своему "
                + "умолчанию — вместе со всеми пинами.",
                (Brush)FindResource("Danger")));
        }

        _sections = sections;
        Sections.ItemsSource = sections;

        // Показ идёт до замера, а ожидание — после: итог на экране появляется
        // сразу, а разделы про туннель догоняют его через несколько секунд.
        await AddTunnelReachAsync(sections);
        //
        // Но дождаться обязательно: отчёт пишется сразу за этим вызовом,
        // и брошенный замер не попал бы в файл — то есть в единственное,
        // что остаётся от проверки назавтра.
        await AddLiveTunnelAsync(sections);

        Say(_target is null
            ? $"Готово: проверено {Collected.Count}."
            : $"Готово: «{_target.Describe}», проверено {Collected.Count}.");
    }

    /// <summary>
    /// Проверяет через сам туннель то, что он якобы не доставил.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отвечает на вопрос, который раздел «в туннель нарочно» задать не может:
    /// довёз бы это имя выбранный сервер. Тот ходит через служебный вход
    /// боевого движка, то есть меряет действующую сборку целиком; этот
    /// поднимает свой sing-box с одним сервером, без TUN и без прав
    /// администратора, — и потому отделяет вину сервера от вины конфига.
    /// </para>
    /// <para>
    /// Сервер спрашивается у движка, а не берётся из настроек. Это не придирка:
    /// настройка — то, что просили при сборке конфига, а выбор живёт своей
    /// жизнью, его меняет и группа по задержке, и рука через Clash API.
    /// Проверить не тот сервер, которым идёт трафик, значит выдать успех
    /// чужого замера за оправдание.
    /// </para>
    /// <para>
    /// Тот же <see cref="TunnelReach"/>, что и в консоли, — не вторая копия.
    /// </para>
    /// </remarks>
    private async Task AddTunnelReachAsync(List<SectionRow> sections)
    {
        var lost = _reports
            .Where(r => r.Kind == BlockKind.TunnelFailed)
            .Select(r => r.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (lost.Count == 0)
            return;

        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            return;

        if (TunnelStatus.FindSingBox() is not { } singBox)
            return;

        var token = _work?.Token ?? CancellationToken.None;

        try
        {
            Say($"Проверяю через сам туннель: {lost.Count}…");

            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(settings.SubscriptionUrl), token);

            var live = await TunnelStatus.CurrentServerAsync(token);

            var server = info.Servers.FirstOrDefault(s => s.IsUsableOutbound && s.Tag == live)
                ?? info.Servers.FirstOrDefault(s => s.IsUsableOutbound && s.Tag == settings.PreferredServer)
                ?? info.Servers.FirstOrDefault(s => s.IsUsableOutbound);

            if (server is null)
                return;

            var readings = await TunnelReach.CheckAsync(
                singBox,
                server,
                lost,
                Path.Combine("runtime", "reach"),

                // Порт заведомо не тот, на котором работает боевой инбаунд:
                // столкнуться с собственным движком значило бы проверить
                // не то и объявить об этом уверенно.
                listenPort: 24081,
                token);

            if (readings.Count == 0)
                return;

            var reached = readings.Where(r => r.Reached).ToList();

            var body = string.Join("\n", readings.Select(r => r.Reached
                ? $"{r.Host}: дошло, ответ {r.Status}, {r.Elapsed.TotalSeconds:0.0} с"
                : $"{r.Host}: не дошло — {r.Detail}"));

            sections.Add(new SectionRow(
                $"Через сервер «{server.Tag}»",
                body + "\n\n" + (reached.Count == readings.Count
                    ? "Сервер довозит их все, а боевой туннель — нет. Значит виноват "
                      + "не он, а то, как собран конфиг."
                    : reached.Count > 0
                        ? "Часть сервер довозит, часть нет. Первые — беда конфига, "
                          + "вторые — самого сервера либо сайта."
                        : "Сервер не довозит ни одного. Вот теперь совет сменить сервер "
                          + "подписки обоснован замером, а не догадкой.")
                + "\n\nЗдесь поднимался отдельный движок с этим одним сервером, без TUN "
                + "и без прав администратора — боевой обход при этом не трогался.",
                (Brush)FindResource(reached.Count > 0 ? "Warn" : "Danger")));

            Sections.ItemsSource = null;
            Sections.ItemsSource = sections;
            _sections = sections;
        }
        catch (OperationCanceledException)
        {
            // Проверку прервали — догонять нечего.
        }
        catch (Exception)
        {
            // Подписка могла не ответить, сервер — не подняться. Это незнание,
            // и раздела просто не будет: пустая проверка лучше выдуманной.
        }
    }

    /// <summary>
    /// Проверенные имена, прибитые в файле hosts.
    /// </summary>
    /// <remarks>
    /// Сравнение по зоне: прибивают обычно поддомен, а проверяем мы апекс,
    /// и дословное сравнение прошло бы мимо ровно тех случаев, ради которых
    /// раздел и заведён.
    /// </remarks>
    private static IReadOnlyList<KeyValuePair<string, string>> Pinned()
    {
        try
        {
            var hosts = HostsFile.Read();

            if (hosts.Count == 0)
                return [];

            var found = new List<KeyValuePair<string, string>>();

            foreach (var report in _reports)
            {
                foreach (var (name, addresses) in hosts)
                {
                    if (addresses.Count == 0)
                        continue;

                    bool covers = string.Equals(name, report.Host, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("." + report.Host, StringComparison.OrdinalIgnoreCase)
                        || report.Host.EndsWith("." + name, StringComparison.OrdinalIgnoreCase);

                    if (covers)
                        found.Add(new(name, string.Join(", ", addresses.Take(2))));
                }
            }

            return found
                .GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception)
        {
            // Файл системный и может быть занят. Отсутствие раздела честнее
            // жалобы поверх готового отчёта.
            return [];
        }
    }

    /// <summary>
    /// Выясняет, через что шёл замер и куда он вышел.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Тем же <see cref="TunnelStatus"/>, что и консоль: сервер спрашивается
    /// у самого движка через Clash API, состояние — его же диагностикой,
    /// а страна выхода отдельно от того, шёл ли сам этот запрос через туннель.
    /// Последнее существенно: с поднятым туннелем ответ службы определения
    /// адреса может прийти и по домашнему каналу, и тогда «страна выхода»
    /// описывает провайдера, а не сервер подписки.
    /// </para>
    /// <para>
    /// Цвет несёт смысл. Мёртвый туннель — красный, потому что он отменяет
    /// доверие к целой половине отчёта; выход в России — жёлтый, потому что
    /// связь при этом исправна, а вот сервисы, закрывающиеся от страны,
    /// откажут так же, как без туннеля.
    /// </para>
    /// </remarks>
    private async Task ReadSetupAsync(AppSettings settings, bool enginesRunning, CancellationToken cancellationToken)
    {
        _tunnel = TunnelState.Unknown;

        if (!enginesRunning)
        {
            _setupRow = new SectionRow(
                "Движки остановлены",
                "Видно, что закрыто на самом деле: ни десинк, ни туннель сейчас не вмешиваются.",
                (Brush)FindResource("Muted"));

            return;
        }

        Say("Выясняю, через что пойдёт замер…");

        try
        {
            var server = await TunnelStatus.CurrentServerAsync(cancellationToken);

            _tunnel = settings.NeedsProxy
                ? await TunnelStatus.StateAsync(server, cancellationToken)
                : TunnelState.Off;

            var exit = settings.NeedsProxy
                ? await TunnelStatus.ReadExitAsync(cancellationToken)
                : new ExitReading { Tunnelled = false };

            var lines = new List<string>
            {
                $"Режим: {settings.DescribeMode()}",
                $"Пресет: {settings.DescribePreset()}",
                $"Сервер: {server ?? "не определён"}",
                $"Туннель: {TunnelHealth.Describe(_tunnel)}",
            };

            if (exit.Address is { Length: > 0 })
            {
                lines.Add($"Выход: {exit.Address}"
                    + (exit.Country is { Length: > 0 } c ? $", {c}" : string.Empty)
                    + (exit.Tunnelled ? " — через туннель" : " — мимо туннеля"));
            }

            var key = _tunnel == TunnelState.Dead ? "Danger"
                : exit.TunnelExitsDomestically ? "Warn"
                : "Muted";

            if (_tunnel == TunnelState.Dead)
            {
                lines.Add(string.Empty);
                lines.Add("Туннель поднят и не пропускает ничего. Пока это так, вердикты "
                    + "по именам, заведённым в VPN, описывают не сайты, а его — менять "
                    + "по ним маршруты не стоит.");
            }
            else if (exit.TunnelExitsDomestically)
            {
                lines.Add(string.Empty);
                lines.Add("Выход туннеля в России. Связь исправна, но сервисы, закрывающиеся "
                    + "от страны, откажут так же, как без туннеля.");
            }

            _setupRow = new SectionRow("Обстановка замера", string.Join("\n", lines), (Brush)FindResource(key));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _setupRow = new SectionRow(
                "Обстановка не выяснена",
                ex.GetBaseException().Message,
                (Brush)FindResource("Muted"));
        }
    }

    /// <summary>
    /// Догоняет итог замером недоставленных имён через действующий туннель.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отвечает на вопрос, который прямая проба задать не может: имя
    /// уезжает движку именем через служебный вход на петле, и доменные
    /// правила срабатывают так же, как для настоящей программы. Открылось
    /// там, но не открылось в таблице — значит виноват не сервер, а то,
    /// как собран конфиг.
    /// </para>
    /// <para>
    /// Тот же <see cref="BlockCheck.ProbeThroughTunnelAsync"/>, что и в консоли.
    /// Раздел молча не появляется, когда служебного входа нет: он поднимается
    /// выключателем «проверять проход трафика», а перезапускать ради замера
    /// работающие движки нельзя — они несут весь трафик машины.
    /// </para>
    /// </remarks>
    private async Task AddLiveTunnelAsync(List<SectionRow> sections)
    {
        var lost = _reports
            .Where(r => r.Kind == BlockKind.TunnelFailed)
            .Select(r => r.Host)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (lost.Count == 0)
            return;

        var token = _work?.Token ?? CancellationToken.None;

        try
        {
            if (!await TunnelProbe.IsUpAsync(SingBoxOptions.DefaultHealthPort, token))
            {
                sections.Add(new SectionRow(
                    "Через туннель нарочно проверить нечем",
                    "Служебный вход не поднят, а без него не узнать, довозит ли туннель эти "
                    + "имена, когда их отправляют в него намеренно. Включается выключателем "
                    + "«Проверять проход трафика» в разделе «Ещё» и начинает работать "
                    + "с ближайшего перезапуска движков.",
                    (Brush)FindResource("Muted")));

                Sections.ItemsSource = null;
                Sections.ItemsSource = sections;
                _sections = sections;

                return;
            }

            var lines = new List<string>();
            int opened = 0;

            foreach (var host in lost)
            {
                token.ThrowIfCancellationRequested();

                var report = await BlockCheck.ProbeThroughTunnelAsync(
                    host, null, SingBoxOptions.DefaultHealthPort, token);

                if (report.Kind == BlockKind.None)
                    opened++;

                lines.Add($"{host}: {report.Describe()}"
                    + (report.Why is { Length: > 0 } why ? $" — {why}" : string.Empty));
            }

            // Вывод зависит от исхода, а не печатается один на все случаи.
            // Прежде под таблицей стояло «открылось тут, но не открылось
            // выше», когда тут как раз не открылось ни одно имя, — то есть
            // раздел спорил сам с собой в двух строках подряд.
            var conclusion = opened == lost.Count
                ? "Здесь открылись все, а выше — ни одно. Значит сервер их довозит, "
                  + "и дело в том, как собран конфиг: адрес, fakeip либо порядок правил."
                : opened > 0
                    ? "Часть открылась здесь, но не выше — по этим именам виноват "
                      + "не сервер, а сборка конфига. Остальные не дошли и так."
                    : "Здесь не открылось ни одно — значит дело не в обходе этого имени "
                      + "мимо туннеля. Либо не довозит сервер, либо отвергает сама "
                      + "удалённая сторона.";

            sections.Add(new SectionRow(
                "Те же имена, пущенные в туннель нарочно",
                string.Join("\n", lines)
                + "\n\nЗдесь имя уехало движку именем, через служебный вход на петле. "
                + conclusion,
                (Brush)FindResource("Warn")));

            Sections.ItemsSource = null;
            Sections.ItemsSource = sections;
            _sections = sections;
        }
        catch (OperationCanceledException)
        {
            // Проверку прервали — догонять нечего.
        }
        catch (Exception ex)
        {
            sections.Add(new SectionRow(
                "Замер через туннель не состоялся",
                ex.GetBaseException().Message,
                (Brush)FindResource("Muted")));

            Sections.ItemsSource = null;
            Sections.ItemsSource = sections;
            _sections = sections;
        }
    }

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Direct => "напрямую",
        RoutingMode.Desync => "десинк",
        _ => "VPN",
    };
}
