using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Строка одной проверенной цели.</summary>
public sealed record CheckRow(
    string Host,
    string Mark,
    Visibility MarkShown,
    string Tcp,
    string Tls12,
    string Tls13,
    string Http,
    string Data,
    string Verdict,
    Brush Color,
    string Why,
    Visibility WhyShown);

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

    private static CancellationTokenSource? _work;
    private static bool _running;
    private static IReadOnlyList<SectionRow> _sections = [];
    private static string _status = "Проверка идёт минуты: по каждому имени четыре пробы, и каждая ждёт ответа.";

    /// <summary>Что проверять: <c>null</c> — весь справочник, иначе имя сервиса.</summary>
    private static string? _scope;

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
            var names = new List<string> { "Все сервисы" };
            names.AddRange(ServiceCatalog.All.Select(s => s.Name));

            Scope.ItemsSource = names;

            int at = _scope is null ? 0 : names.FindIndex(n => n == _scope);

            Scope.SelectedIndex = at < 0 ? 0 : at;
            RunButton.Content = _scope is null ? "Проверить" : "Проверить сервис";
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

        _scope = Scope.SelectedIndex <= 0 ? null : Scope.SelectedItem as string;
        RunButton.Content = _scope is null ? "Проверить" : "Проверить сервис";
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
        _sections = [];
        Sections.ItemsSource = null;
        Header.Visibility = Visibility.Visible;

        _work?.Cancel();
        _work = new CancellationTokenSource();
        _running = true;

        RunButton.IsEnabled = false;
        StopButton.IsEnabled = true;

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

            var targets = Targets(zapretRoot, _scope);

            if (targets.Count == 0)
            {
                Say(_scope is null
                    ? "Проверять нечего: ни один список доменов не прочитался."
                    : $"У «{_scope}» нет доменных частей — проверять нечего. "
                      + "Такой сервис задан подсетями, и стучаться в подсеть наугад не проверка.");

                Header.Visibility = Visibility.Collapsed;
                return;
            }

            Say(_scope is null
                ? $"Проверяю {targets.Count} — по каждому четыре пробы."
                : $"Проверяю «{_scope}»: {targets.Count} имён, по каждому четыре пробы.");

            await RunAsync(targets, engine, running && settings.NeedsProxy, _work.Token);

            Summarise(engine);
        }
        catch (OperationCanceledException)
        {
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
        }
    }

    /// <summary>Говорит и запоминает: раздел пересоздаётся, а сказанное должно пережить уход.</summary>
    private void Say(string text)
    {
        _status = text;
        Status.Text = text;
    }

    private void OnStop(object sender, RoutedEventArgs e) => _work?.Cancel();

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
    private static readonly HashSet<string> NotWorthChecking = new(StringComparer.OrdinalIgnoreCase)
    {
        "riotgames.es",
        "itch.zone",
        "rutor.info",
        "valorant.com",

        "cdninstagram.com",
        "tiktokcdn.com",
        "licdn.com",
        "nocookie.net",
        "cloudfront.net",
        "ooklaserver.net",
        "cdnst.net",
        "rgpub.io",
        "ytimg.com",
        "ggpht.com",
        "twimg.com",
        "discordapp.net",
        "rbxcdn.com",
        "steamstatic.com",
        "githubusercontent.com",
    };

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
    private static IReadOnlyList<(string Host, string Service)> Targets(string? zapretRoot, string? only)
    {
        int perPart = only is null ? 1 : 4;

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
                    Collected.Add(Row(report, tunnelled));
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

    private static bool Proxied(RuleEngine engine, string host) =>
        engine.Evaluate(new ConnectionEvent
        {
            Timestamp = DateTimeOffset.Now,
            Protocol = ProtocolKind.Tcp,
            RemoteAddress = null,
            RemotePort = 443,
            Hostname = host,
        }).Mode == RoutingMode.Proxy;

    private CheckRow Row(TargetReport report, bool tunnelled)
    {
        var key = report.Kind switch
        {
            BlockKind.None => "Accent",
            BlockKind.NoAddress => "Faint",
            BlockKind.TlsDpi or BlockKind.Stall => "Warn",
            _ => "Danger",
        };

        var why = report.Data.Detail ?? report.Tcp.Detail
            ?? report.Tls13.Detail ?? report.Tls12.Detail ?? report.Http.Detail;

        return new CheckRow(
            report.Host,
            tunnelled ? "чз" : string.Empty,
            tunnelled ? Visibility.Visible : Visibility.Collapsed,
            report.Tcp.Describe(),
            report.Tls12.Describe(),
            report.Tls13.Describe(),
            report.Http.Describe(),
            report.Data.Describe(),
            report.Describe(),
            (Brush)FindResource(key),
            why ?? string.Empty,
            report.Actionable && why is not null ? Visibility.Visible : Visibility.Collapsed);
    }

    /// <summary>
    /// Итог: по видам блокировок и по спорящим правилам.
    /// </summary>
    /// <remarks>
    /// Спорящие правила показываются даже когда всё открыто: правило, до
    /// которого не доходит очередь, — поломка, которая ждёт своего часа,
    /// и заметить её иначе нечем.
    /// </remarks>
    private void Summarise(RuleEngine? engine)
    {
        var sections = new List<SectionRow>();

        var byKind = Collected
            .Where(r => r.Verdict != "доступен" && r.Verdict != "нет адреса у имени")
            .GroupBy(r => r.Verdict)
            .OrderByDescending(g => g.Count())
            .ToList();

        sections.Add(byKind.Count == 0
            ? new SectionRow(
                "Всё открыто",
                _scope is null
                    ? "Обходить нечего."
                    : $"У «{_scope}» обходить нечего. Проверено вчетверо глубже полного прогона.",
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

        Say(_scope is null
            ? $"Готово: проверено {Collected.Count}."
            : $"Готово: «{_scope}», проверено {Collected.Count}.");
    }

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Direct => "напрямую",
        RoutingMode.Desync => "десинк",
        _ => "VPN",
    };
}
