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
    public RecipeWindow(string title, string domain, ZapretPreset preset)
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
              + "Проверка подбирает рабочий сама, но требует остановленных движков.");

        TestButton.IsEnabled = _rows.Count > 0;
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
    /// должна была начаться — имя открывается без десинка либо движки
    /// работают, — но сказано это было так, что не читалось.
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
    /// Движки обязаны быть остановлены, и это не перестраховка. Работающий
    /// winws2 уже держит свой набор фильтров, а второй экземпляр поверх него
    /// правил бы те же пакеты дважды — вышел бы замер не рецепта, а их суммы.
    /// Туннель тем более: через него имя откроется любым рецептом, включая
    /// пустой, и проверка ответит «работает всё».
    /// </para>
    /// <para>
    /// Останавливать их сами не беремся: это весь трафик машины, и человек
    /// должен решить это сам, а не обнаружить постфактум.
    /// </para>
    /// </remarks>
    private async void OnTest(object sender, RoutedEventArgs e)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
        {
            Say("Сначала остановите движки на «Главной». Работающий десинк правил бы "
                + "те же пакеты вторым слоем, а туннель открыл бы имя любым рецептом — "
                + "проверять было бы нечего.", "Warn", "⏸");

            return;
        }

        var paths = ZapretPaths.Discover();
        var winws = paths is null ? null : Path.Combine(paths.Root, "exe", "winws2.exe");

        if (winws is null || !File.Exists(winws))
        {
            Say("winws2 не найден рядом с программой — проверять нечем.", "Danger", "✕");
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        TestButton.IsEnabled = false;
        TestButton.Content = "Проверяю…";
        StopButton.IsEnabled = true;

        int worked = 0;
        int tried = 0;

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
            if (await OpensAsync(_domain, _work.Token) is { Passed: true } bare)
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
                var flow = await TryAsync(winws, paths!.Root, row, _work.Token);
                clock.Stop();

                // Открывшим считается тот, кто довёл до страницы, а не тот,
                // кто прошёл фильтр. Разница вскрылась 21.09: все рецепты
                // получали 301 на apex Instagram и числились рабочими,
                // притом что сайт не грузился ни с одним.
                bool ok = flow.Page;

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
                    flow.Page ? "Accent" : ok ? "Warn" : "Danger");

                row.Edge = (Brush)FindResource(ok ? "Accent" : "Border");

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
        }
    }

    /// <summary>
    /// Поднимает winws2 с одним профилем и пробует открыть имя.
    /// </summary>
    /// <remarks>
    /// Профиль ровно один, без остального пресета: иначе имя могла бы забрать
    /// чужая секция, и замер сказал бы о ней, а не о выбранном рецепте.
    /// </remarks>
    private async Task<Flow> TryAsync(
        string winws,
        string zapretRoot,
        RecipeRow row,
        CancellationToken cancellationToken)
    {
        if (row.Steps.Count == 0)
            return Flow.Nothing("рецепт без шагов");

        var list = Path.GetFullPath(Path.Combine(
            WinwsCommandLine.OwnListsDirectory, "probe.txt"));

        Directory.CreateDirectory(Path.GetDirectoryName(list)!);
        await File.WriteAllTextAsync(list, _domain + Environment.NewLine, cancellationToken);

        var start = new ProcessStartInfo(winws)
        {
            WorkingDirectory = zapretRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // Глобальные ключи пресета обязательны, и это выяснилось дорого:
        // без них проверка отвечала «не помогает» на каждый рецепт подряд,
        // включая заведомо рабочие. В них три вещи, без которых рецепт —
        // пустой звук:
        //
        //   --lua-init   загружает библиотеку, где эти самые fake,
        //                multidisorder и hostfakesplit_multi определены;
        //                без неё имя в --lua-desync не значит ничего;
        //   --wf-*       фильтр WinDivert, то есть какой трафик вообще
        //                перехватывать; без него не перехватывается ничего;
        //   --blob       заготовки поддельных пакетов, на которые рецепты
        //                ссылаются по имени (blob=tls_google).
        foreach (var argument in _preset.GlobalArguments)
            start.ArgumentList.Add(argument);

        // Профиль собирается теми же ключами, что уйдут в работу. Разойдись
        // они — проверка мерила бы не то, что потом применится, и это
        // не теория: без --out-range рецепт открывал домен для curl,
        // а sing-box получал от того же сервера отказ в рукопожатии.
        start.ArgumentList.Add("--new");
        start.ArgumentList.Add("--name=NetZapret: проба");
        // Порты те же, что у секции, которая это имя забирает. Зашитое
        // 80,443 мерило бы не то, что применится: у обложек Spotify секция
        // объявлена на одном 443, а у Discord - на восьми портах.
        start.ArgumentList.Add("--filter-tcp="
            + PresetPorts.ForDomains(_preset, zapretRoot, [_domain]));
        start.ArgumentList.Add($"--hostlist={WinwsCommandLine.Forward(list)}");
        start.ArgumentList.Add(WinwsCommandLine.ProbeOutRange);

        foreach (var step in row.Steps)
            start.ArgumentList.Add($"--lua-desync={step}");

        Process? process = null;

        try
        {
            process = Process.Start(start);

            if (process is null)
                return Flow.Nothing("winws2 не запустился");

            // Ждём, пока драйвер встанет в разрыв, — но по его же словам,
            // а не по часам. Раньше здесь стояли глухие две секунды на каждый
            // рецепт: на десятке это двадцать секунд чистого ожидания, и почти
            // всё оно лишнее — winws2 обычно готов за треть секунды.
            await ReadyAsync(process, cancellationToken);

            return await OpensAsync(_domain, cancellationToken);
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(Leaves);
                }
            }
            catch (Exception)
            {
                // Процесс мог уйти сам — например, отказавшись от кривого
                // рецепта. Это и есть его отрицательный результат.
            }

            process?.Dispose();
        }
    }

    /// <summary>
    /// Ждёт, пока winws2 доложит о готовности.
    /// </summary>
    /// <remarks>
    /// Ждём ровно ту строку, которой он сообщает о поставленном фильтре:
    /// раньше неё пакеты не перехватываются вовсе, и проба мерила бы
    /// незащищённое соединение. Прежде здесь ждали строку про число профилей —
    /// она печатается при разборе настроек, то есть до того, как драйвер
    /// встал в разрыв.
    /// </remarks>
    private static async Task ReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);

        var line = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();

        while (DateTime.UtcNow < deadline)
        {
            var finished = await Task.WhenAny(line, Task.Delay(300, cancellationToken));

            if (finished != line)
                continue;

            if (line.Result is null)
                break;

            // «windivert initialized. capture is started.» — драйвер в разрыве.
            if (line.Result.Contains("capture is started", StringComparison.OrdinalIgnoreCase))
                break;

            line = process.StandardOutput.ReadLineAsync(cancellationToken).AsTask();
        }

        // Небольшая доводка всё же нужна: между разбором настроек и первым
        // перехваченным пакетом драйвер успевает не всегда.
        await Task.Delay(Settle, cancellationToken);
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

            using var client = new TcpClient();

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
    /// Выдержка после того, как драйвер доложил о готовности.
    /// </summary>
    /// <remarks>
    /// Между разбором настроек и первым перехваченным пакетом winws2
    /// успевает не всегда, и совсем без выдержки проба мерила бы
    /// незащищённое соединение. Но четыреста миллисекунд брались с запасом
    /// на глаз; двухсот пятидесяти хватает, а на одиннадцати рецептах
    /// это ещё полторы секунды.
    /// </remarks>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Сколько ждать, пока снятый процесс уйдёт.
    /// </summary>
    /// <remarks>
    /// Ждём не ухода как такового, а освобождения WinDivert: не дождавшись,
    /// следующий рецепт не поднимется. Четыре секунды здесь были запасом
    /// на случай, которого не случалось ни разу, — winws2 уходит за
    /// сотни миллисекунд.
    /// </remarks>
    private const int Leaves = 1500;

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
        if (!flow.Passed)
            return "не помогает — " + flow.Ending;

        var seconds = $"{took.TotalSeconds:0.0} с";

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
