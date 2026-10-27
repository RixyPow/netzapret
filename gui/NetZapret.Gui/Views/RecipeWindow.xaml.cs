using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Строка одного рецепта в окне выбора.
/// </summary>
/// <remarks>
/// Класс с уведомлениями, а не запись. Перебор рецептов меняет вердикт
/// у каждой строки по очереди, и прежде для показа этого пересоздавался весь
/// <c>ItemsSource</c> — присваиванием <c>null</c> и обратно. WPF на этом падал:
/// подмена приходила из продолжения задачи и попадала в середину раскладки,
/// а <c>StackPanel</c> в этот момент уже держал в руках прежний список.
/// Отсюда «ArgumentOutOfRangeException: Parameter 'index'» посреди проверки.
/// </remarks>
public sealed class RecipeRow : INotifyPropertyChanged
{
    /// <summary>Что запишется в правило.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Шаги рецепта — те самые, что уйдут в <c>--lua-desync</c>.
    /// </summary>
    /// <remarks>
    /// Несёт строка, а не ищутся по имени в пресете. Поиск по имени
    /// работал, пока рецепты брались только оттуда, и уже тогда подводил:
    /// имя группы — это имя её первой секции, и перестановка секций
    /// обесценивала сохранённый выбор молча. Теперь источников два,
    /// и второго в пресете нет вовсе.
    /// </remarks>
    public required IReadOnlyList<string> Steps { get; init; }

    /// <summary>Что показывается в заголовке: приёмы, а не сервис.</summary>
    public required string Title { get; init; }

    /// <summary>Набор целиком, с настройками.</summary>
    public required string Summary { get; init; }

    /// <summary>Где ещё этот набор применяется — чтобы судить по знакомому.</summary>
    public required string UsedBy { get; init; }

    /// <summary>
    /// За сколько имя открылось; <c>null</c> — не открылось или не мерили.
    /// </summary>
    /// <remarks>
    /// Нужно затем, что рабочих рецептов обычно несколько, а совет был
    /// «берите любой из отмеченных». Любой — значит гадать. Время отвечает
    /// на это замером: рецепт, при котором рукопожатие проходит вдвое
    /// быстрее, и в работе будет вести себя лучше.
    /// </remarks>
    public TimeSpan? Took { get; set; }

    /// <summary>
    /// Этот набор пресет и так применяет к этому имени.
    /// </summary>
    /// <remarks>
    /// Без пометки человек выбирает вслепую то, что уже действует, и потом
    /// не понимает, почему ничего не изменилось. Считается тем же кодом,
    /// что решает судьбу имени в работе, — <see cref="PresetZones"/>.
    /// </remarks>
    public bool Current { get; init; }

    private string _verdict = string.Empty;
    private Brush _verdictColour = Brushes.Transparent;
    private Brush _edge = Brushes.Transparent;

    public string Verdict
    {
        get => _verdict;
        set => Set(ref _verdict, value);
    }

    public Brush VerdictColour
    {
        get => _verdictColour;
        set => Set(ref _verdictColour, value);
    }

    public Brush Edge
    {
        get => _edge;
        set => Set(ref _edge, value);
    }

    /// <summary>
    /// Какой рецепт советовать: быстрейший из тех, что открыли имя.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Здесь, а не в обработчике кнопки, потому что это единственная чистая
    /// часть окна и единственная, которую можно проверить тестом. Оставь её
    /// в обработчике — тест повторил бы выборку своими словами и проверял бы
    /// собственную копию, а не то, что увидит человек.
    /// </para>
    /// <para>
    /// Неработавшие не рассматриваются вовсе: у них время означает срок
    /// ожидания, а не скорость. Пустить их в сравнение значило бы советовать
    /// самый быстрый отказ.
    /// </para>
    /// </remarks>
    public static RecipeRow? Best(IEnumerable<RecipeRow> rows) =>
        rows.Where(r => r.Took is not null).OrderBy(r => r.Took!.Value).FirstOrDefault();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// Выбор рецепта десинка для одного имени.
/// </summary>
/// <remarks>
/// <para>
/// Заведено потому, что <c>mode: desync</c> в маршрутах означал ровно одно —
/// «мимо туннеля». Чем чинить имя, решал пресет, и если оно не попадало
/// ни в один его список, не делалось ничего: снаружи это неотличимо
/// от неработающего десинка. Хуже того, попасть можно в секцию с рецептом
/// <c>pass</c> — она забирает имя себе и пропускает нетронутым.
/// </para>
/// <para>
/// Рецепты берутся из самого пресета: там они подобраны под живые блокировки
/// и обновляются вместе с ним. Свой перечень мы бы выдумали один раз
/// и оставили стареть.
/// </para>
/// </remarks>
public partial class RecipeWindow : Window
{
    private readonly string _domain;
    private readonly ZapretPreset _preset;
    private readonly List<RecipeRow> _rows = [];

    private CancellationTokenSource? _work;

    /// <summary>Куда стучаться приветствием; узнаётся раз на проверку.</summary>
    private IPAddress? _address;

    /// <summary>Что выбрали; <c>null</c> — окно закрыли отменой.</summary>
    public string? Chosen { get; private set; }

    /// <param name="title">Что показать в заголовке: имя или название сервиса.</param>
    /// <param name="domain">
    /// Имя, на котором проверяются рецепты. Для сервиса — любое из его списка.
    /// </param>
    /// <remarks>
    /// Разделены намеренно, и это исправление. Сервису в заголовок идёт
    /// «discord», а проверять на нём нечего: такого имени не существует,
    /// оно не разрешается, и каждый рецепт отвечал «не помогает» — все
    /// одиннадцать, включая заведомо рабочие.
    /// </remarks>
    /// <param name="presetOnlyIfKnown">
    /// Предлагать «решает пресет», только когда в пресете есть секция под это
    /// имя. Для своих доменов (владелец, 26.09): свой домен почти никогда
    /// не стоит ни в одном списке пресета, и «десинк» без рецепта оставлял
    /// его без обхода вовсе — а в строке стояло «десинк», как будто работает.
    /// </param>
    public RecipeWindow(string title, string domain, ZapretPreset preset, bool presetOnlyIfKnown = false)
    {
        InitializeComponent();

        _domain = domain;
        _preset = preset;

        Head.Text = $"Чем чинить {title}";

        if (!string.Equals(title, domain, StringComparison.OrdinalIgnoreCase))
            Head.Text += $" — проверяю на {domain}";

        // Какую секцию пресет отдаёт этому имени сам. Спрашивается тем же
        // кодом, что решает это в работе: winws2 отдаёт пакет первой
        // совпавшей секции, и ответ здесь обязан совпадать с тем, что
        // покажет отчёт проверки.
        string? already = null;

        try
        {
            already = PresetZones.Build(preset, ZapretPaths.Discover()?.Root)
                .MatchFor(domain)?.Name;
        }
        catch (Exception)
        {
            // Списки пресета могли не прочитаться. Пометка — удобство,
            // и её отсутствие не повод не дать выбрать рецепт.
        }

        // Приёмы, которые умеет движок. Нужны затем, чтобы из каталога
        // предлагать только то, что этот пресет потянет: рецепт с
        // неподключённым модулем winws2 не запустит вовсе, и проверка
        // ответила бы «не помогает» о приёме, которого не пробовали.
        IReadOnlyDictionary<string, string> providers =
            new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (ZapretPaths.Discover() is { } paths)
                providers = LuaModules.Providers(LuaModules.Scan(paths.LuaDirectory));
        }
        catch (Exception)
        {
            // Без приёмов останутся только наборы пресета — это не отказ,
            // а сужение выбора до заведомо рабочего.
        }

        foreach (var choice in RecipeChoices.Build(preset, providers, already))
        {
            _rows.Add(new RecipeRow
            {
                Name = choice.Name,
                Title = choice.Title,

                // Шаги несёт сама строка, а не ищутся потом по имени.
                // Поиск по имени уже стоил голоса Discord: правка пресета
                // переставила секции, имя группы сменилось, и сохранённый
                // выбор перестал находиться — молча.
                Steps = choice.Steps,

                // С местами переноса: шаг не содержит пробелов, и WPF,
                // не найдя где разорвать, рвал его посреди слова — в узком
                // окне строка вставала столбцом в пять букв.
                Summary = StepText.Of(choice.Steps),
                UsedBy = choice.Source,
                Current = choice.Current,
                Edge = (Brush)FindResource("Border"),
            });
        }

        if (_rows.FirstOrDefault(r => r.Current) is { } current)
        {
            current.Verdict = "сейчас применяется";
            current.VerdictColour = (Brush)FindResource("Muted");
        }

        Recipes.ItemsSource = _rows;

        Say(_rows.Count == 0
            ? "Ни одного рецепта не нашлось — ни в пресете, ни в каталоге."
            : $"Рецептов: {_rows.Count} — из пресета «{preset.Name}» и из каталога. "
              + "Проверка подбирает рабочий сама — движки останавливать не нужно.");

        TestButton.IsEnabled = _rows.Count > 0;

        if (presetOnlyIfKnown)
        {
            if (already is null)
            {
                PresetButton.Visibility = Visibility.Collapsed;
                PresetNote.Text = $"Пресет «{preset.Name}» не знает {domain}: ни одна его секция это имя "
                    + "не берёт, и без рецепта десинк не сделал бы с ним ничего. Выберите рецепт — "
                    + "или «Отмена», и маршрут останется прежним.";
            }
            else
            {
                PresetNote.Text = $"«Решает пресет» — имя возьмёт его секция «{already}».";
            }
        }

        Closed += (_, _) => _work?.Cancel();
    }

    private void OnPick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string name })
            return;

        Chosen = name;
        Close(true);
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        Chosen = DesyncRecipes.FromPreset;
        Close(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

    /// <summary>
    /// Прерывает перебор, оставляя уже полученные ответы.
    /// </summary>
    /// <remarks>
    /// Отвечает немедленно, не дожидаясь, пока прерывание дойдёт до проб.
    /// Дойти оно может не сразу: текущий рецепт держит запущенный winws2,
    /// и его завершения ждут до четырёх секунд. Молчащая кнопка эти секунды
    /// выглядит неработающей.
    /// </remarks>
    private void OnStop(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        Say("Останавливаю: жду, пока закончится текущий рецепт…", "Muted", "⏹");

        _work?.Cancel();
    }

    /// <summary>
    /// Отвечает человеку так, чтобы ответ было видно.
    /// </summary>
    /// <remarks>
    /// Полосой, а не строкой под заголовком. Прежде окно отвечало подписью,
    /// и её не замечали: нажал «Проверить все», ничего видимого не случилось,
    /// и человек приходил с «проверка не начинается». Чаще всего она и не
    /// должна была начаться — имя открывается без десинка (до 27.10 — и при
    /// работающих движках), — но сказано это было так, что не читалось.
    /// </remarks>
    private void Say(string text, string colour = "Muted", string mark = "•")
    {
        Status.Text = text;
        StatusMark.Text = mark;
        StatusMark.Foreground = (Brush)FindResource(colour);
        StatusCard.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Закрывает окно с ответом.
    /// </summary>
    /// <remarks>
    /// Через try, потому что <c>DialogResult</c> у окна, показанного не через
    /// <c>ShowDialog</c>, бросает исключение. Пока такое окно у нас одно
    /// и модальное, но нажатие «выбрать» не должно ронять программу, если
    /// однажды его откроют иначе: человек в этот момент делает ровно то,
    /// зачем окно и открыл.
    /// </remarks>
    private void Close(bool answer)
    {
        _work?.Cancel();

        try
        {
            DialogResult = answer;
        }
        catch (InvalidOperationException)
        {
            Close();
        }
    }

    /// <summary>
    /// Перебирает рецепты и смотрит, с каким имя открывается.
    /// </summary>
    /// <remarks>
    /// <para>
    /// С 27.10 — не останавливая движки (владелец: «сделай подбор рецепта
    /// не выключая движки, раз мы уже научились»). Прежде они были обязаны
    /// стоять: второй winws2 правил бы те же пакеты поверх работающего, и вышел
    /// бы замер не рецепта, а их суммы. Теперь рецепт поднимается пробным winws2
    /// (<see cref="RecipeTrial"/>), который перехватывает только порты пробы,
    /// основной их не трогает (<see cref="TunnelCapture.KeepProbesOut"/>), и окно
    /// ходит строго с них (<see cref="ProbePorts"/>). Остановленные движки тоже
    /// годятся — тогда пробный winws2 просто один.
    /// </para>
    /// <para>
    /// Нельзя только рядом с десинком сборки до 27.10: он порты пробы
    /// перехватывает, и замер снова вышел бы суммой. Туннелю имя не достаётся:
    /// подменный адрес заменяется честным (<see cref="AddressForAsync"/>).
    /// </para>
    /// </remarks>
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive()
            && state.Services.FirstOrDefault(s => s.Name == "winws2") is { KeepsProbesOut: false })
        {
            Say("Десинк запущен сборкой, которая перехватывает порты пробы: рецепт проверялся бы "
                + "поверх него. Перезапустите движки на «Главной» — и проверка пойдёт, не останавливая их.",
                "Warn", "⏸");

            Journal.Write("рецепты", $"{_domain}: {Status.Text}");
            return;
        }

        var paths = ZapretPaths.Discover();

        if (paths is null || !File.Exists(paths.ExecutablePath))
        {
            Say("winws2 не найден рядом с программой — проверять нечем.", "Danger", "✕");
            Journal.Write("рецепты", $"{_domain}: {Status.Text}");
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        TestButton.IsEnabled = false;
        TestButton.Content = "Проверяю…";
        StopButton.IsEnabled = true;

        int worked = 0;
        int tried = 0;

        // Каждый рецепт — в журнал одной строкой на проверку, как у «Сайт
        // не открывается?». Без неё проверку 27.10 было не восстановить:
        // окно закрыто — и что оно показало, не знает никто.
        var heard = new List<string>();

        // Все соединения проверки — с портов пробы: их видит только пробный
        // winws2, основной десинк их не трогает.
        using var ports = ProbePorts.Use();

        try
        {
            // Адрес спрашивается один раз на всю проверку. Имя пробы может
            // не разрешаться вовсе, и тогда каждый повтор — это ещё один отказ
            // резолвера в цепочке из десятка рецептов.
            _address = await AddressForAsync(_domain, _work.Token);

            if (_address is null)
            {
                Say($"Не удалось узнать адрес ни у {_domain}, ни у его зоны — "
                    + "проверять некуда стучаться.", "Danger", "✕");

                return;
            }

            // Сперва без всякого рецепта: если имя открывается само, дальше
            // мерить нечего и выбирать не из чего.
            //
            // Тем же способом, что и весь перебор, а не полной пробой. Полная
            // ходит через DNS и пять стадий, и на имени пробы, которого в DNS
            // нет, отвечала про резолвер вместо фильтра. Разные мерки в одном
            // окне вдобавок расходятся в ответах — а сравнивать предстоит
            // именно их.
            //
            // Ответ сервера любым кодом, 404 тоже, — уже «открывается»: фильтр
            // так не отвечает, а дальше этот замер — точка отсчёта (Flow.Opens).
            if (await OpensAsync(_domain, _work.Token) is { Answered: true } bare)
            {
                Say($"{_domain} открывается и без десинка — {Verdict(bare, TimeSpan.Zero)}. "
                    + "Рецепт ему не нужен, берите «решает пресет».", "Accent", "✓");

                return;
            }

            worked = 0;

            foreach (var row in _rows)
            {
                Say($"Проверяю {++tried} из {_rows.Count}: {row.Title}…", "Warn", "◐");

                row.Verdict = "проверяю…";
                row.VerdictColour = (Brush)FindResource("Muted");
                row.Took = null;

                var clock = Stopwatch.StartNew();
                var flow = await TryAsync(paths, row, _work.Token);
                clock.Stop();

                // Открывшим считается тот, кто довёл до конечного ответа сервера,
                // а не тот, кто прошёл фильтр. Разница вскрылась 21.09: все
                // рецепты получали 301 на apex Instagram и числились рабочими,
                // притом что сайт не грузился ни с одним.
                bool ok = flow.Opens;

                // Время — только у дошедших до страницы. У остальных оно
                // означает срок ожидания либо длину цепочки перенаправлений,
                // а не скорость, и сравнивать его не с чем.
                row.Took = ok ? clock.Elapsed : null;

                // Вердикт называет, что пришло, а не только «да/нет».
                // Прежде здесь стояло «открывается за 0,8 с» и на коротком
                // перенаправлении на страницу входа — владелец прочёл это
                // как «сайт работает» и был вправе.
                row.Verdict = Verdict(flow, clock.Elapsed);

                // Три цвета, а не два. Перенаправление — это пройденный
                // фильтр, но не пришедшая страница, и валить его в одну
                // кучу с отказом так же неверно, как с успехом.
                row.VerdictColour = (Brush)FindResource(
                    ok ? "Accent" : flow.Passed ? "Warn" : "Danger");

                row.Edge = (Brush)FindResource(ok ? "Accent" : "Border");
                heard.Add($"«{row.Title}» — {row.Verdict}");

                if (ok)
                    worked++;
            }

            if (worked == 0)
            {
                Say($"Ни один рецепт не открыл {_domain}. Дело может быть не в десинке — "
                    + "посмотрите «Проверку блокировок» целиком.", "Danger", "✕");
            }
            else
            {
                // Советуем быстрейший, а не «любой из отмеченных». Прежний
                // совет отправлял выбирать наугад из пяти одинаково зелёных
                // строк; время же — замер, и оно говорит, какой рецепт
                // проводит рукопожатие с меньшим сопротивлением.
                var best = RecipeRow.Best(_rows)!;

                var sovet = $"Открывают {worked} из {_rows.Count}. "
                    + $"Быстрее всех «{best.Title}» — {best.Took!.Value.TotalSeconds:0.0} с.";

                if (best.Current)
                    sovet += " Его пресет и так применяет: менять нечего.";

                Say(sovet, "Accent", "✓");
            }
        }
        catch (OperationCanceledException)
        {
            Say($"Остановлено. Проверено {tried} из {_rows.Count}, ответы сохранены.",
                "Muted", "⏹");
        }
        catch (Exception ex)
        {
            Say("Проверка не удалась: " + ex.GetBaseException().Message, "Danger", "✕");
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestButton.Content = "Проверить все";
            StopButton.IsEnabled = false;

            // Итог — тем же текстом, что в полосе ответа: он и называет, чем кончилось.
            Journal.Write("рецепты", $"{_domain}{(_address is { } at ? $" ({at})" : string.Empty)}: "
                + (heard.Count > 0 ? string.Join("; ", heard) + " → " : string.Empty)
                + Status.Text);
        }
    }

    /// <summary>
    /// Поднимает пробный winws2 с одним рецептом и пробует открыть имя.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Профиль ровно один, без остального пресета: иначе имя могла бы забрать
    /// чужая секция, и замер сказал бы о ней, а не о выбранном рецепте.
    /// </para>
    /// <para>
    /// Строку собирает библиотека (<see cref="WinwsCommandLine.ProbeLine"/>) тем же
    /// кодом, что свой профиль в работе: глобальные ключи пресета (без них —
    /// ни приёмов Lua, ни образцов пакетов, и «не помогает» каждый рецепт),
    /// порты секции, которая забирает это имя, тот же <c>--out-range</c>.
    /// Прежде окно собирало её само, и разойтись с работой было легко.
    /// </para>
    /// </remarks>
    private async Task<Flow> TryAsync(ZapretPaths paths, RecipeRow row, CancellationToken cancellationToken)
    {
        if (row.Steps.Count == 0)
            return Flow.Nothing("рецепт без шагов");

        Directory.CreateDirectory(Path.GetDirectoryName(RecipeTrial.ListPath)!);
        await File.WriteAllTextAsync(RecipeTrial.ListPath, _domain + Environment.NewLine, cancellationToken);

        var profile = new OwnDesyncProfile
        {
            Name = "проба",
            Steps = row.Steps,
            HostListPath = RecipeTrial.ListPath,
            Ports = PresetPorts.ForDomains(_preset, paths.Root, [_domain]),
        };

        var (trial, failure) = await RecipeTrial.StartAsync(paths, _preset, profile, cancellationToken);

        // Не поднялся — например, отказался от кривого рецепта. Это и есть
        // его отрицательный результат.
        if (trial is null)
            return Flow.Nothing(failure ?? "winws2 не поднялся");

        await using (trial)
            return await OpensAsync(_domain, cancellationToken);
    }

    /// <summary>
    /// Открывается ли имя: одно рукопожатие TLS, и всё.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Полная проба спрашивает TCP, TLS 1.2, TLS 1.3, HTTP и передачу данных —
    /// пять ответов там, где нужен один. Рецепт чинит ровно приветствие TLS,
    /// и вопрос к нему ровно один: дошло ли оно. На десятке рецептов разница
    /// между «одно рукопожатие» и «пять проб» — это минуты.
    /// </para>
    /// <para>
    /// Чужой сертификат и любой ответ об ошибке считаются успехом: отвечает
    /// сервер — значит приветствие дошло, а это и есть предмет замера.
    /// Проверять имя на сертификате тут незачем и вредно: у выдуманного
    /// имени пробы его не будет никогда.
    /// </para>
    /// </remarks>
    private async Task<Flow> OpensAsync(string host, CancellationToken cancellationToken)
    {
        var where = host;
        var path = "/";
        Flow last = Flow.Nothing("не начинали");

        // Перенаправления проходим до конца, а не останавливаемся на первом.
        //
        // Владелец 21.09: «результат не изменился, только обёртка другая» —
        // и был прав. Назвать 301 перенаправлением мало: это не вердикт,
        // а недоведённый замер. Instagram отвечает 301 на apex, браузер идёт
        // дальше и там-то и ломается, а проверка останавливалась и говорила
        // «фильтр пройден».
        //
        // Три шага: больше нужно разве что кольцу, а кольцо — само по себе
        // отказ. Каждый шаг идёт своим соединением: предыдущее закрыто
        // по нашей же просьбе Connection: close.
        for (int hop = 0; hop < 3; hop++)
        {
            last = await FetchAsync(where, path, cancellationToken);

            if (!last.Passed || last.Page || last.Location is not { Length: > 0 } next)
                return last;

            if (!Uri.TryCreate(next, UriKind.Absolute, out var target))
            {
                // Относительный адрес — тот же хост, другой путь.
                path = next.StartsWith('/') ? next : "/" + next;
                continue;
            }

            // Уводит на чужую зону — дальше не наше дело: там другое имя,
            // другой фильтр и другой разговор. Но сказать об этом надо.
            if (!SameZone(target.Host, host))
                return last with { Ending = $"уводит на {target.Host}" };

            where = target.Host;
            path = target.PathAndQuery;

            // Имя сменилось — сменился и адрес. Список проверки покрывает
            // зону целиком, так что десинк к нему применится тот же.
            if (!string.Equals(where, host, StringComparison.OrdinalIgnoreCase))
                _address = await AddressForAsync(where, cancellationToken) ?? _address;
        }

        return last with { Ending = "перенаправления не кончаются" };
    }

    /// <summary>Одно ли это дерево имён.</summary>
    /// <remarks>
    /// Апекс на www и обратно — самое частое перенаправление в сети,
    /// и обрывать на нём замер значило бы не доводить его никогда.
    /// А вот уход на чужую зону — это уже другое имя и другой разговор.
    /// </remarks>
    private static bool SameZone(string one, string other)
    {
        var a = one.TrimStart('*', '.');
        var b = other.TrimStart('*', '.');

        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Flow> FetchAsync(string host, string path, CancellationToken cancellationToken)
    {
        try
        {
            var address = _address;

            if (address is null)
                return Flow.Nothing("адрес не известен");

            using var client = ProbePorts.Client(address.AddressFamily);

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Reach);

            await client.ConnectAsync(address, 443, deadline.Token);

            await using var tls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, _, _, _) => true);

            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
            }, deadline.Token);

            return await FlowsAsync(tls, host, path, cancellationToken);
        }
        catch (Exception ex)
        {
            // Молчание, сброс, отказ — до сервера не дошли. Но чем именно
            // кончилось, сказать стоит: «сброс» и «тишина» лечатся разным,
            // и прежде оба выглядели одинаковым «не помогает».
            return Flow.Nothing(Short(ex));
        }
    }

    /// <summary>
    /// Сколько данных считать доказательством, что рецепт работает.
    /// </summary>
    /// <remarks>
    /// Шестнадцать килобайт — выше всех порогов, на которых у нас рвались
    /// потоки: steamcommunity.com умирал на 14 381 Б, skinsrestorer.net
    /// на 13 505, Steam на 16 384. Меньший порог такие обрывы пропустил бы,
    /// а больший стоил бы времени на каждом из восьми рецептов.
    /// </remarks>
    private const int Enough = 16 * 1024;

    /// <summary>Сколько ждать продолжения, прежде чем счесть поток убитым.</summary>
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Сколько ждать соединения и рукопожатия.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Главный расход всего перебора, и вот почему. Провальных рецептов
    /// в списке большинство, а проваливаются они одинаково: TCP встаёт
    /// мгновенно — фильтр его пропускает, — и висит рукопожатие, до самого
    /// предела. То есть предел этот платится за каждый неработающий рецепт
    /// целиком.
    /// </para>
    /// <para>
    /// Было четыре секунды, стало две с половиной. Рабочее рукопожатие
    /// на замерах владельца проходит за 0,8 с, так что запас остаётся
    /// трёхкратный, а на одиннадцати рецептах это экономит около девяти
    /// секунд — больше, чем все прочие сокращения вместе.
    /// </para>
    /// </remarks>
    private static readonly TimeSpan Reach = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Что написать в строке рецепта по итогам замера.
    /// </summary>
    /// <remarks>
    /// Отдельно и статически — единственная чистая часть этого перебора,
    /// и единственная, которую можно проверить тестом. Слова здесь важнее
    /// обычного: прежний вердикт «открывается за 0,8 с» читался как
    /// утверждение о сайте, тогда как сказано им было о рукопожатии.
    /// </remarks>
    internal static string Verdict(Flow flow, TimeSpan took)
    {
        var seconds = $"{took.TotalSeconds:0.0} с";

        // Ошибку сайта называем кодом: «сервер закрыл соединение» на месте
        // «ответ 404» скрыло 27.10, что рецепт до сервера довёл.
        if (flow.Status >= 400)
            return $"ответ сервера {flow.Status}, {flow.Bytes} Б, {seconds}";

        if (!flow.Passed)
            return "не помогает — " + flow.Ending;

        if (flow.Page)
            return $"страница {flow.Status}, {flow.Bytes} Б, {seconds}";

        // Сюда попадает перенаправление, которое мы не смогли довести
        // до конца: увело на чужую зону либо закольцевалось. Фильтр им
        // пройден, но страницы нет, и в счёт открывших такое не идёт —
        // иначе выходит «открывают 5 из 11» при неработающем сайте.
        return $"фильтр пройден, страницы нет — {flow.Ending}";
    }

    /// <summary>Укорачивает длинный адрес: в строку он не влезает.</summary>
    private static string Trim(string place) =>
        place.Length > 44 ? place[..44] + "…" : place;

    /// <summary>
    /// Чем кончился обмен после рукопожатия.
    /// </summary>
    /// <remarks>
    /// Не «да/нет», и это исправление 21.09. Прежде успехом считался любой
    /// пришедший байт, и окно отвечало «открывается за 0,8 с» про имя,
    /// которое в браузере не грузилось вовсе. Байты и вправду приходили —
    /// короткое перенаправление на страницу входа, — но по такому ответу
    /// нельзя сказать ни «работает», ни «не работает», не назвав его.
    /// </remarks>
    internal sealed record Flow
    {
        public required int Bytes { get; init; }

        /// <summary>Код ответа; <c>null</c> — ответ не похож на HTTP.</summary>
        public int? Status { get; init; }

        /// <summary>Куда перенаправляют, если перенаправляют.</summary>
        public string? Location { get; init; }

        /// <summary>Чем кончилось — словами, для вердикта.</summary>
        public required string Ending { get; init; }

        /// <summary>
        /// Рецепт провёл соединение мимо фильтра.
        /// </summary>
        /// <remarks>
        /// Признак — разобранный код ответа, а не объём. Ответивший сервер
        /// означает, что до него дошли: ни фильтр, ни заглушка внятного
        /// HTTP по этому имени не отдадут. Перенаправление сюда входит —
        /// апекс на www перенаправляет половина интернета, и звать это
        /// отказом значило бы врать в другую сторону.
        /// </remarks>
        public bool Passed => Status is >= 200 and < 400;

        /// <summary>Пришла настоящая страница, а не перенаправление.</summary>
        public bool Page => Status is >= 200 and < 300;

        /// <summary>Сервер ответил по HTTP — каким бы кодом ни было.</summary>
        public bool Answered => Status is not null;

        /// <summary>
        /// Рецепт довёл до конечного ответа сервера: страница или его ошибка, но не перенаправление.
        /// </summary>
        /// <remarks>
        /// <para>
        /// 4xx и 5xx с 21.09 считались неудачей: по одному коду не отличить
        /// отказ сайта от отказа фильтра. Но отличать есть с чем: до рецептов
        /// окно меряет имя без рецепта, и до перебора доходит только то, где
        /// сервер не ответил вовсе. Без рецепта тишина, с рецептом — ответ
        /// сервера: соединение провёл рецепт, какой бы код сайт ни отдал.
        /// </para>
        /// <para>
        /// Прежнее правило делало окно бесполезным для узлов без главной
        /// страницы. Владелец 27.10, i.ytimg.com: превью YouTube грузятся,
        /// а «не помогает» у всех рецептов — режущие приводили к ответу 404
        /// (1503 Б, им узел отвечает на <c>/</c> и через основной десинк),
        /// и вердикт ещё и прятал код за «сервер закрыл соединение».
        /// </para>
        /// </remarks>
        public bool Opens => Page || Status >= 400;

        public static Flow Nothing(string ending) => new() { Bytes = 0, Ending = ending };
    }

    /// <summary>
    /// Идут ли данные после того, как рукопожатие состоялось.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Этот урок мы уже оплатили однажды — в проверке блокировок. Там
    /// состоявшееся рукопожатие перестало считаться ответом после разбора
    /// steamcommunity.com: он не открывался, а проверка называла его
    /// доступным, потому что рукопожатие и вправду проходило — поток умирал
    /// после, на четырнадцатой тысяче байт.
    /// </para>
    /// <para>
    /// Окно выбора рецепта того урока не получило и продолжало отвечать
    /// по одному рукопожатию. Отсюда и «три рецепта из восьми открывают»
    /// у раздачи Spotify при неработающих обложках: приветствие доходило
    /// со всеми тремя, а картинка не приходила ни с одним.
    /// </para>
    /// <para>
    /// Закрытое сервером соединение — успех, а не отказ: короткая страница
    /// кончается раньше порога, и ждать от неё шестнадцати килобайт незачем.
    /// Отказ — это тишина при живом соединении.
    /// </para>
    /// </remarks>
    private static async Task<Flow> FlowsAsync(
        SslStream tls,
        string host,
        string path,
        CancellationToken cancellationToken)
    {
        var request = System.Text.Encoding.ASCII.GetBytes(
            $"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
            + "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64)\r\n"
            + "Accept: */*\r\nAccept-Encoding: identity\r\nConnection: close\r\n\r\n");

        await tls.WriteAsync(request, cancellationToken);

        var buffer = new byte[16 * 1024];
        var head = new System.Text.StringBuilder();
        int total = 0;
        string ending = "дочитали до порога";

        while (total < Enough)
        {
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quiet.CancelAfter(Silence);

            int read;

            try
            {
                read = await tls.ReadAsync(buffer, quiet.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Тишина при живом соединении — то самое, что ищем.
                return Flow.Nothing(total == 0 ? "тишина" : $"оборвалось на {total} Б");
            }
            catch (Exception ex)
            {
                return Flow.Nothing(total == 0
                    ? Short(ex)
                    : $"оборвалось на {total} Б: {Short(ex)}");
            }

            if (read == 0)
            {
                ending = "сервер закрыл соединение";
                break;
            }

            // Заголовок копим до первой пустой строки: код и Location лежат
            // в нём, а тело нам неинтересно — мы не браузер.
            if (head.Length < 4096)
                head.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));

            total += read;
        }

        return Parse(head.ToString(), total, ending);
    }

    /// <summary>
    /// Разбирает начало ответа: код и, если есть, куда перенаправляют.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Код ответа — единственный внятный признак того, что до сервера дошли.
    /// Ни фильтр, ни заглушка оператора по этому имени внятного HTTP
    /// не отдадут: для этого нужен сертификат на него, а его у них нет.
    /// </para>
    /// <para>
    /// Не разобралось — считаем, что не дошли. Прежде здесь хватало любого
    /// байта, и в успех попадало всё подряд, включая обрывки, по которым
    /// сказать ничего нельзя.
    /// </para>
    /// </remarks>
    private static Flow Parse(string head, int total, string ending)
    {
        var first = head.Split('\n', 2)[0].Trim();

        if (!first.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            || first.Split(' ') is not [_, var code, ..]
            || !int.TryParse(code, out int status))
        {
            return new Flow
            {
                Bytes = total,
                Ending = total == 0 ? ending : $"{total} Б, и это не похоже на HTTP",
            };
        }

        string? where = null;

        foreach (var line in head.Split('\n'))
        {
            if (line.StartsWith("Location:", StringComparison.OrdinalIgnoreCase))
            {
                where = line["Location:".Length..].Trim();
                break;
            }
        }

        return new Flow
        {
            Bytes = total,
            Status = status,
            Location = where,
            Ending = ending,
        };
    }

    /// <summary>
    /// Короткое объяснение обрыва.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Своими словами, а не обрезанное системное. Windows отвечает
    /// «Удаленный хост принудительно разорвал существующее подключение»,
    /// и обрезка по длине давала «…принудительно разорвал су» — оборванное
    /// посреди слова и ничего не добавляющее к «не помогает».
    /// </para>
    /// <para>
    /// Три исхода, и различать их стоит: сброс — это DPI ответил, отказ
    /// в рукопожатии — сторона не приняла наше приветствие, тишина —
    /// до неё не дошло вовсе. Лечатся они разным.
    /// </para>
    /// </remarks>
    internal static string Short(Exception ex)
    {
        var why = ex.GetBaseException();

        if (why is System.Security.Authentication.AuthenticationException)
            return "отказ в рукопожатии";

        if (why is OperationCanceledException or TimeoutException)
            return "не дождались";

        if (why is System.Net.Sockets.SocketException socket)
        {
            return socket.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionReset => "соединение сброшено",
                System.Net.Sockets.SocketError.ConnectionRefused => "в соединении отказано",
                System.Net.Sockets.SocketError.TimedOut => "не дождались",
                System.Net.Sockets.SocketError.HostUnreachable => "хост недостижим",
                System.Net.Sockets.SocketError.NetworkUnreachable => "сеть недостижима",
                _ => "обрыв связи",
            };
        }

        // Сброс приходит и завёрнутым в IOException — от SslStream
        // и от потока сокета. Разворачивать до конца не всегда выходит,
        // поэтому смотрим ещё и по вложенному.
        for (var inner = ex; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Net.Sockets.SocketException nested)
                return Short(nested);
        }

        var said = why.Message;

        return said.Length > 40 ? said[..40] + "…" : said;
    }

    /// <summary>
    /// Адрес, куда стучаться приветствием с этим именем.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Имя пробы может не разрешаться вовсе, и это не помеха: фильтр читает
    /// его из приветствия TLS, а не из DNS. Голосовые серверы Discord
    /// выдаются под звонок, постоянного имени у них нет, и проверять зону
    /// больше нечем — <c>frankfurt1234.discord.media</c> сегодня отвечает
    /// пустотой у любого резолвера.
    /// </para>
    /// <para>
    /// Поэтому адрес берётся у ближайшего предка, который разрешается:
    /// он в той же зоне и стоит за той же сетью доставки. Цепочку предков
    /// строит <see cref="HostNames.ZoneChain"/>, там же и решено, где
    /// остановиться.
    /// </para>
    /// </remarks>
    private static async Task<IPAddress?> AddressForAsync(
        string host,
        CancellationToken cancellationToken)
    {
        foreach (var name in HostNames.ZoneChain(host))
        {
            try
            {
                var found = await Dns.GetHostAddressesAsync(name, cancellationToken);

                var address = found.FirstOrDefault(a =>
                    a.AddressFamily == AddressFamily.InterNetwork) ?? found.FirstOrDefault();

                // Движки теперь работают во время пробы, и имени «через VPN»
                // система отдаёт подставной адрес: соединение по нему ушло бы
                // в туннель, и рецепт мерил бы VPN. Настоящий — у DoH.
                if (address is not null && TunnelHealth.IsFakeIp(address))
                {
                    using var lookup = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };

                    address = await DohResolver.ResolveAsync(name, lookup, cancellationToken) is { } honest
                        && IPAddress.TryParse(honest, out var parsed) ? parsed : null;
                }

                if (address is not null)
                    return address;
            }
            catch (Exception)
            {
                // Не разрешилось — спросим про предка.
            }
        }

        return null;
    }
}
