using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Часть сервиса и её маршрут.</summary>
public sealed class PartRow
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }

    /// <summary>
    /// Настоящее имя из списка — на нём проверяются рецепты.
    /// </summary>
    /// <remarks>
    /// Название части в заголовок годится, а в пробу нет: «Текст и вход»
    /// не разрешается, и проверка отвечала бы «не помогает» на всё подряд.
    /// </remarks>
    public string? Probe { get; init; }
    /// <summary>
    /// Куда идёт часть, словами.
    /// </summary>
    /// <remarks>
    /// Задаётся при сборке, но переписывается для прибитых: пометка про пин
    /// проставляется позже, когда прочитан hosts, и «напрямую» к тому моменту
    /// уже посчитано.
    /// </remarks>
    public required string Mode { get; set; }
    public required Brush Color { get; init; }
    /// <summary>
    /// Что выбрано в списке.
    /// </summary>
    /// <remarks>
    /// Пишется привязкой: SelectedIndex связывается в обе стороны по
    /// умолчанию, и WPF кладёт сюда новое значение ещё до того, как сработает
    /// обработчик выбора.
    /// </remarks>
    public required int Choice { get; set; }

    /// <summary>
    /// Маршрут на момент сборки строки.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Choice"/> именно потому, что тот меняется
    /// привязкой раньше обработчика: сверка с ним показывала бы, что выбор
    /// совпал с прежним, всегда и для любого выбора. Так и вышло —
    /// уведомление о перезапуске перестало появляться вовсе.
    /// </remarks>
    public required int Applied { get; init; }
    public required bool CanRoute { get; init; }
    public required string Letter { get; init; }

    /// <summary>Прибито ли в hosts хоть одно имя этой части.</summary>
    public bool HasPin { get; set; }

    /// <summary>Адресную часть прибить нечем: hosts понимает только имена.</summary>
    public required bool CanPin { get; init; }

    public string PinLabel => HasPin ? "снять пин" : "пин";

    public Brush PinColor =>
        (Brush)Application.Current.FindResource(HasPin ? "Accent" : "Text");

    public BitmapImage? Icon { get; set; }

    public Visibility IconShown => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Строка таблицы порядка вычисления.</summary>
public sealed record RuleRow(
    string Ordinal,
    string Kind,
    string Value,
    string Mode,
    string Server,
    Brush Color);

/// <summary>
/// Сервис со своими частями — папка.
/// </summary>
/// <remarks>
/// Свёрнутая по умолчанию: частей под сорок, и одной стеной их не окинуть
/// взглядом. Но заголовок говорит и в свёрнутом виде — сколько частей и куда
/// они идут, — иначе сворачивание прячет ровно то, ради чего раздел открывают.
/// </remarks>
public sealed record ServiceRow(string Name, IReadOnlyList<PartRow> Parts)
{
    public bool Open { get; set; }

    public Visibility PartsShown => Open ? Visibility.Visible : Visibility.Collapsed;

    public string Chevron => Open ? "▼" : "►";

    public string Count => Parts.Count + " " + Ending(Parts.Count);

    /// <summary>Куда идут части: одним словом, если все одинаково.</summary>
    public string Summary
    {
        get
        {
            var modes = Parts
                .GroupBy(p => p.Mode)
                .OrderByDescending(g => g.Count())
                .ToList();

            return modes.Count switch
            {
                0 => string.Empty,
                1 => "всё " + modes[0].Key,
                _ => string.Join(" · ", modes.Select(g => $"{g.Count()} {g.Key}")),
            };
        }
    }

    /// <summary>Цвет итога; при расхождении — приглушённый, чтобы не выдавать одну часть за весь сервис.</summary>
    public Brush Color =>
        Parts.Select(p => p.Mode).Distinct().Count() == 1 && Parts.Count > 0
            ? Parts[0].Color
            : (Brush)Application.Current.FindResource("Muted");

    private static string Ending(int count)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return "частей";

        return (count % 10) switch
        {
            1 => "часть",
            2 or 3 or 4 => "части",
            _ => "частей",
        };
    }
}

/// <summary>Своё доменное правило.</summary>
public sealed record OwnRow(string Value, string Mode, Brush Color);

/// <summary>
/// Сервисы и их маршруты.
/// </summary>
/// <remarks>
/// <para>
/// Ценность в том, что части одного сервиса расходятся: у Discord голос
/// ломается иначе, чем переписка, и лечится иначе. Поэтому строка на часть,
/// а не на сервис.
/// </para>
/// <para>
/// Читается и пишется через те же <see cref="ServiceRouting"/>
/// и <see cref="UserRulesFile"/>, что у консоли, — файл правил один, и меню
/// увидит тот же выбор.
/// </para>
/// </remarks>
public partial class RoutesView : UserControl
{
    private CancellationTokenSource? _icons;

    /// <summary>Пока идёт первичное заполнение, выбор в списках не считается выбором человека.</summary>
    private bool _filling;

    /// <summary>Пользовательские правила текущей сборки строк.</summary>
    private UserRulesFile _own = UserRulesFile.Load();

    /// <summary>Действующий пресет — по нему и видно, что будет с именем.</summary>
    private ZapretPreset? _preset;

    private string? _zapretRoot;

    public RoutesView()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) => _icons?.Cancel();
    }

    private void Reload()
    {
        _filling = true;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var zapretRoot = ZapretPaths.Discover()?.Root;
            var userRules = UserRulesFile.Load();

            // Те же правила держим под рукой при сборке строк: по ним
            // подписывается выбранный рецепт.
            _own = userRules;
            _zapretRoot = zapretRoot;

            // И пресет: без него «десинк» в списке не отличить от «десинк,
            // но пресет об этом имени не знает и не сделает ничего».
            _preset = null;

            try
            {
                if (settings.PresetName is { } name && ZapretPaths.FindPreset(name) is { } path)
                    _preset = new PresetReader().Load(path);
            }
            catch (Exception)
            {
                // Испорченный пресет не повод не показать маршруты: подпись
                // просто скажет, что чинить имя нечем.
            }

            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            var problems = RuleSetExpander.Expand(engine.RuleSet, zapretRoot);

            var services = new List<ServiceRow>();

            foreach (var service in ServiceCatalog.All)
            {
                var parts = ServiceRouting
                    .Describe(service, engine, zapretRoot, userRules)
                    .Select(Row)
                    .ToList();

                if (parts.Count > 0)
                    services.Add(new ServiceRow(service.Name, parts));
            }

            MarkPins(services, zapretRoot);

            // Раскрытые папки переживают перерисовку. Прежде любое действие
            // внутри папки — пин, свой маршрут, обновление списка — собирало
            // строки заново, и всё захлопывалось: человек выбирал маршрут
            // одной части, а искать следующую приходилось с начала.
            foreach (var service in services)
                service.Open = _open.Contains(service.Name);

            _all = services;
            Services.ItemsSource = services;
            ShowOwn();

            Status.Text = problems.Count == 0
                ? $"Сервисов: {services.Count}. Выбор пишется в config\\rules.user.yaml и применяется перезапуском."
                : $"Часть списков не нашлась, и эти правила не действуют: {string.Join("; ", problems.Take(3))}";

            ShowOrder(engine);
            StartIcons(services);
        }
        catch (Exception ex)
        {
            Status.Text = "Правила не читаются: " + ex.GetBaseException().Message;
        }
        finally
        {
            _filling = false;
        }
    }

    private PartRow Row(ServiceRouting.PartStatus part)
    {
        // Ключ несёт и тип, и путь: правило по адресам пишется ipset'ом,
        // по именам — hostlist'ом, и перепутать их значит записать правило,
        // которое не совпадёт ни с чем.
        var kind = part.Part.ByAddress ? "ipset" : "hostlist";

        var detail = part.Part.ByAddress
            ? $"{part.DomainCount} подсетей"
            : $"{part.DomainCount} доменов";

        if (part.Example is { } example)
            detail += $" · например {example}";

        if (!part.Explicit)
            detail += " · по умолчанию";

        var (color, choice) = part.Mode switch
        {
            RoutingMode.Direct => ("Muted", 0),
            RoutingMode.Desync => ("Warn", 1),
            _ => ("Accent", 2),
        };

        var host = (part.Example ?? part.Part.Name).TrimStart('*', '.');

        return new PartRow
        {
            // Уже загруженный значок ставится сразу. Раздел пересоздаётся при
            // каждом заходе, и без этого он начинал бы с букв, а значки
            // проступали бы заново — при живом кэше в памяти.
            Icon = host.Contains('.') ? SiteIcons.Cached(host) : null,
            Key = kind + "|" + part.Part.List,
            Title = part.Part.Name,

            // Обычно ровно то имя, что показано в подписи «например …»: оно
            // взято из самого списка и потому заведомо им покрыто. Каталог
            // может назвать другое, и тогда верить надо ему: запись списка —
            // это зона, а фильтр стоит на имени, и у голоса Discord апекс
            // зоны оказался единственным именем, которое не закрыто.
            Probe = part.Part.ByAddress ? null : (part.Part.Probe ?? host),

            Detail = detail,

            // Чем именно пойдёт часть — прямо в подписи маршрута. «Десинк»
            // сам по себе не говорит ничего: решает пресет, и решить он может
            // в том числе «не знаю такого имени».
            Mode = Describe(part),

            Color = (Brush)Application.Current.FindResource(color),
            Choice = choice,
            Applied = choice,

            // Адресные части значка не получают: у них нет имени, у которого
            // его можно было бы спросить.
            Letter = host.Length > 0 ? host[..1].ToUpperInvariant() : "·",
            CanRoute = true,

            // Адресную часть прибить нечем: hosts понимает только имена,
            // а подсеть в него не записать.
            CanPin = !part.Part.ByAddress,
        };
    }

    /// <summary>
    /// Догружает значки сайтов, не задерживая показ.
    /// </summary>
    /// <remarks>
    /// Список появляется сразу с буквами, значки приезжают по одному.
    /// Ждать сети, чтобы показать то, что уже посчитано, значило бы менять
    /// полминуты человеческого времени на картинки.
    /// </remarks>
    private void StartIcons(IReadOnlyList<ServiceRow> services)
    {
        _icons?.Cancel();
        _icons = new CancellationTokenSource();

        var token = _icons.Token;

        // Значок сервиса ставится частям сразу, до сети: при возврате в раздел
        // он уже в памяти, и мигания «буквы, потом картинки» не будет.
        foreach (var service in services)
            LendServiceIcon(service);

        _ = Task.Run(async () =>
        {
            foreach (var service in services)
            {
                foreach (var part in service.Parts)
                {
                    if (token.IsCancellationRequested)
                        return;

                    var host = Host(part);

                    // Про что уже спрашивали, того не спрашиваем: значок либо
                    // проставлен при сборке строки, либо его нет вовсе.
                    if (host is null || SiteIcons.Known(host))
                        continue;

                    var icon = await SiteIcons.ForAsync(host, token);

                    if (icon is null || token.IsCancellationRequested)
                        continue;

                    Dispatcher.Invoke(() =>
                    {
                        part.Icon = icon;
                        Redraw();
                    });
                }

                if (token.IsCancellationRequested)
                    return;

                Dispatcher.Invoke(() =>
                {
                    if (LendServiceIcon(service))
                        Redraw();
                });
            }
        }, token);
    }

    /// <summary>
    /// Показывает правила в том порядке, в каком их вычисляет движок.
    /// </summary>
    /// <remarks>
    /// Список сервисов выше сгруппирован — так о маршрутах и думают, — но
    /// группировка прячет главное: побеждает правило, до которого очередь
    /// доходит раньше. Перекрытое в файле есть, а не применяется никогда,
    /// и увидеть это можно только здесь.
    /// </remarks>
    private void ShowOrder(RuleEngine engine)
    {
        var ruleSet = engine.RuleSet;

        var rows = ruleSet.Rules.Select(rule => new RuleRow(
            rule.Ordinal.ToString(),
            rule.Match.ToString().ToLowerInvariant(),
            rule.Value,
            rule.Mode.ToString().ToLowerInvariant(),
            rule.Mode == RoutingMode.Proxy ? rule.Server ?? ruleSet.DefaultServer ?? "auto" : "—",
            (Brush)FindResource(ColorOf(rule.Mode)))).ToList();

        // Умолчание — последней строкой: это тоже решение, и без него таблица
        // обрывается там, где ответ ещё не дан.
        rows.Add(new RuleRow(
            "—",
            "default",
            "*",
            ruleSet.DefaultMode.ToString().ToLowerInvariant(),
            ruleSet.DefaultServer ?? "—",
            (Brush)FindResource(ColorOf(ruleSet.DefaultMode))));

        Order.ItemsSource = rows;

        OrderSummary.Text = $"{ruleSet.Rules.Count} правил, порядок: process → domain → ip → default. "
            + "Побеждает то, до которого очередь доходит раньше.";

        OrderNote.Text = ruleSet.Operating != OperatingMode.Selective
            ? "В этом режиме правила не вычисляются — они показаны для справки."
            : engine.RequiresHostnames
                ? "Доменные правила требуют источника имён — его даёт fakeip, "
                  + "поднимаемый вместе с туннелем."
                : string.Empty;
    }

    private static string ColorOf(RoutingMode mode) => mode switch
    {
        RoutingMode.Direct => "Muted",
        RoutingMode.Desync => "Warn",
        _ => "Accent",
    };

    private void OnOrderToggle(object sender, RoutedEventArgs e)
    {
        var open = OrderPanel.Visibility != Visibility.Visible;

        OrderPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        OrderChevron.Text = open ? "▼" : "►";
    }

    /// <summary>
    /// Отдаёт частям без своего значка значок сервиса.
    /// </summary>
    /// <remarks>
    /// У раздачи и превью своего значка нет и не будет: <c>googlevideo.com</c>
    /// и <c>i.ytimg.com</c> его не отдают, у Discord и Telegram то же самое
    /// с их вспомогательными именами. Буква рядом с красным значком YouTube
    /// говорит, будто это разные вещи, — а это один сервис, разложенный
    /// по частям.
    /// </remarks>
    private static bool LendServiceIcon(ServiceRow service)
    {
        var own = service.Parts.FirstOrDefault(p => p.Icon is not null)?.Icon;

        if (own is null)
            return false;

        var lent = false;

        foreach (var part in service.Parts.Where(p => p.Icon is null))
        {
            part.Icon = own;
            lent = true;
        }

        return lent;
    }

    private static string? Host(PartRow part)
    {
        int at = part.Detail.IndexOf("например ", StringComparison.Ordinal);

        if (at < 0)
            return null;

        var rest = part.Detail[(at + "например ".Length)..];
        var host = rest.Split(' ', '·')[0].TrimStart('*', '.');

        return host.Contains('.') ? host : null;
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    /// <summary>
    /// Отмечает части, чьи имена прибиты в hosts.
    /// </summary>
    /// <remarks>
    /// Файл читается один раз на весь раздел, а не на каждую часть: их под
    /// семьдесят, а hosts у людей вырастает до тысяч строк.
    /// </remarks>
    private static void MarkPins(IReadOnlyList<ServiceRow> services, string? zapretRoot)
    {
        List<string> pins;

        try
        {
            pins = HostsEditor.Pins().Keys.ToList();
        }
        catch (Exception)
        {
            // Нечитаемый hosts не повод не показать раздел.
            return;
        }

        if (pins.Count == 0)
            return;

        foreach (var part in services.SelectMany(s => s.Parts).Where(p => p.CanPin))
        {
            var zones = HostListReader.Read(part.Key.Split('|', 2)[1], zapretRoot, out _)
                .Select(d => d.TrimStart('*', '.'))
                .ToList();

            part.HasPin = pins.Any(name => Covers(zones, name));

            // Прибитое имя, идущее напрямую, так и называется. «Напрямую»
            // здесь недоговаривает: адрес выбран руками, а не резолвером,
            // и когда он однажды протухнет, искать причину будут где угодно,
            // только не в hosts.
            if (part.HasPin && part.Choice == 0)
                part.Mode = "прибит в hosts";
        }
    }

    /// <summary>
    /// Покрывает ли зона части это прибитое имя.
    /// </summary>
    /// <remarks>
    /// По зоне, а не по точному совпадению: списки хранят <c>openai.com</c>,
    /// а прибивается <c>api.openai.com</c>. При точном сравнении кнопка врала
    /// бы «пина нет» над живым пином.
    /// </remarks>
    private static bool Covers(IReadOnlyList<string> zones, string name) =>
        zones.Any(zone => string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Пин части: первым нажатием открывает окно, повторным снимает.
    /// </summary>
    /// <remarks>
    /// Снятие без вопроса, а закрепление через окно — потому что цена разная.
    /// Закрепить значит выбрать чей адрес и переписать системный файл; снять
    /// значит вернуть как было, и переспрашивать об этом незачем.
    /// </remarks>
    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key })
            return;

        var list = key.Split('|', 2) is [_, var path] ? path : null;

        if (list is null)
            return;

        var service = ServiceCatalog.All.FirstOrDefault(s =>
            s.Parts.Any(p => string.Equals(p.List, list, StringComparison.OrdinalIgnoreCase)));

        var part = service?.Parts.FirstOrDefault(p =>
            string.Equals(p.List, list, StringComparison.OrdinalIgnoreCase));

        if (service is null || part is null)
            return;

        var row = _all.SelectMany(s => s.Parts).FirstOrDefault(p => p.Key == key);

        if (row is { HasPin: true })
        {
            Unpin(part);
            return;
        }

        var window = new PinWindow(service, part) { Owner = Window.GetWindow(this) };
        window.ShowDialog();

        if (!window.Changed)
            return;

        RouteDirectIfPinned(part);

        Reload();
        this.Offer($"«{part.Name}»: маршрут или пин изменены");
    }

    /// <summary>
    /// Прибитой части проставляет маршрут «напрямую».
    /// </summary>
    /// <remarks>
    /// Пин и туннель друг друга исключают: пин говорит «иди на этот адрес»,
    /// правило «через VPN» — «иди через зарубежный выход», и вместе выходит
    /// «зайди на отечественный узел из-за границы». Замер 2026-09-11: рукопожатие
    /// к прибитому адресу через выход в Нидерландах не проходит вовсе, браузер
    /// показывает ERR_SSL_PROTOCOL_ERROR, и виноватым выглядит пин.
    ///
    /// Десинк поверх пина ломает то же самое, только на своём слое: он судит
    /// по имени и о подмене адреса не знает.
    ///
    /// Поэтому пин сам ставит «напрямую» — единственный маршрут, при котором
    /// он работает.
    /// </remarks>
    private void RouteDirectIfPinned(ServicePart part)
    {
        try
        {
            var zones = HostListReader.Read(part.List, ZapretPaths.Discover()?.Root, out _)
                .Select(d => d.TrimStart('*', '.'))
                .ToList();

            if (!HostsEditor.Pins().Keys.Any(name => Covers(zones, name)))
                return;

            var file = UserRulesFile.Load();
            file.Set(MatchKind.HostList, part.List, RoutingMode.Direct);
            file.Save();
        }
        catch (Exception)
        {
            // Не записалось — пин всё равно поставлен, а маршрут виден
            // в списке и правится вручную.
        }
    }

    private void Unpin(ServicePart part)
    {
        try
        {
            var zones = HostListReader.Read(part.List, ZapretPaths.Discover()?.Root, out _)
                .Select(d => d.TrimStart('*', '.'))
                .ToList();

            var ours = HostsEditor.Pins().Keys.Where(pin => Covers(zones, pin)).ToList();

            if (ours.Count == 0)
                return;

            var result = HostsEditor.Unpin(ours);
            HostsEditor.FlushDns();

            Reload();

            Status.Text = $"Снято имён: {ours.Count}. Осталось прибитых: {result.Pinned}. "
                + "Маршрут не трогали — он остался таким, каким был.";

            this.Offer($"Пин снят: {part.Name}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось снять пин: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Всё, что собрано; поиск отбирает из этого, не перечитывая правила.</summary>
    private IReadOnlyList<ServiceRow> _all = [];

    /// <summary>Какие папки раскрыты. Переживает перерисовку списка.</summary>
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Перерисовывает показанное.
    /// </summary>
    /// <remarks>
    /// Строки — простые объекты без уведомлений: городить их ради значка
    /// и треугольника не стоит. Перерисовывается именно показанное, а не всё
    /// собранное: при поиске в списке лежит отобранное, и подмена его полным
    /// набором отбирала бы у человека то, что он только что набрал.
    /// </remarks>
    /// <summary>
    /// Перерисовывает список.
    /// </summary>
    /// <remarks>
    /// Под признаком заполнения, и это не перестраховка. Сброс источника
    /// пересоздаёт строки, каждый список маршрута заново получает свой
    /// SelectedIndex, и WPF считает это выбором человека: раскрытие папки
    /// записывало правило и вызывало уведомление о перезапуске — по разу
    /// на каждую часть внутри.
    /// </remarks>
    private void Redraw()
    {
        var was = _filling;
        _filling = true;

        try
        {
            var shown = Services.ItemsSource;

            Services.ItemsSource = null;
            Services.ItemsSource = shown;
        }
        finally
        {
            _filling = was;
        }
    }

    /// <summary>Открывает или закрывает папку.</summary>
    /// <remarks>
    /// Состояние проставляется и отобранной строке, и той, что лежит
    /// в собранном: при поиске это разные объекты, и без второй половины
    /// раскрытая папка захлопывалась бы, стоило очистить строку поиска.
    /// </remarks>
    private void OnFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ServiceRow row })
            return;

        row.Open = !row.Open;

        foreach (var same in _all.Where(s => s.Name == row.Name))
            same.Open = row.Open;

        Remember(row.Name, row.Open);
        Redraw();
    }

    /// <summary>Запоминает раскрытую папку, чтобы она пережила перерисовку.</summary>
    private void Remember(string name, bool open)
    {
        if (open)
            _open.Add(name);
        else
            _open.Remove(name);
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        bool open = ExpandButton.Content as string == "Раскрыть всё";

        foreach (var service in _all)
        {
            service.Open = open;
            Remember(service.Name, open);
        }

        if (Services.ItemsSource is IEnumerable<ServiceRow> shown)
        {
            foreach (var service in shown)
                service.Open = open;
        }

        ExpandButton.Content = open ? "Свернуть всё" : "Раскрыть всё";
        Redraw();
    }

    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        var needle = Search.Text.Trim();

        SearchHint.Visibility = needle.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (needle.Length == 0)
        {
            Services.ItemsSource = _all;
            return;
        }

        // Ищем и по названию сервиса, и по имени части, и по домену из примера:
        // человек помнит «где у меня ютуб» одним из трёх, и заставлять его
        // угадывать нужное — значит сделать поиск бесполезным.
        //
        // Найденное раскрывается само: искали часть, а не папку, и заставлять
        // открывать её вручную значит отдать половину найденного обратно.
        var found = _all
            .Select(s => s with
            {
                Parts = s.Parts.Where(p => Matches(s.Name, p, needle)).ToList(),
                Open = true,
            })
            .Where(s => s.Parts.Count > 0)
            .ToList();

        Services.ItemsSource = found;

        Status.Text = found.Count == 0
            ? $"По «{needle}» ничего нет. Свой домен можно добавить строкой выше."
            : $"Найдено частей: {found.Sum(s => s.Parts.Count)}.";
    }

    private static bool Matches(string service, PartRow part, string needle) =>
        service.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || part.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || part.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Показывает свои доменные правила.</summary>
    private void ShowOwn()
    {
        var file = UserRulesFile.Load();

        Own.ItemsSource = file.Entries
            .Where(entry => entry.Match == MatchKind.Domain)
            .Select(entry => new OwnRow(
                entry.Value,

                // Рецепт называется прямо в строке: иначе выбранный однажды,
                // он пропадал бы из виду, и понять, чем чинится имя, можно
                // было бы только заглянув в yaml.
                entry.Mode == RoutingMode.Desync && !string.IsNullOrWhiteSpace(entry.Recipe)
                    ? $"десинк: {entry.Recipe}"
                    : Describe(entry.Mode),
                (Brush)FindResource(entry.Mode switch
                {
                    RoutingMode.Direct => "Muted",
                    RoutingMode.Desync => "Warn",
                    _ => "Accent",
                })))
            .ToList();
    }

    private void OnOwnKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            AddOwn();
    }

    private void OnAddOwn(object sender, RoutedEventArgs e) => AddOwn();

    /// <summary>
    /// Записывает правило на свой домен.
    /// </summary>
    /// <remarks>
    /// Звёздочка ставится сама: имя означает зону, и <c>example.com</c> должен
    /// покрывать поддомены — иначе человек напишет корень, а картинки с
    /// <c>cdn.example.com</c> пойдут мимо правила, и понять это по виду
    /// не выйдет.
    /// </remarks>
    private void AddOwn()
    {
        var raw = OwnDomain.Text.Trim().Trim('/').ToLowerInvariant();

        // Из адреса берём только имя: люди вставляют ссылку целиком, и правило
        // на «https://example.com/page» не совпало бы ни с чем.
        if (raw.Contains("://"))
            raw = raw.Split("://")[1];

        raw = raw.Split('/')[0].TrimStart('*', '.');

        if (raw.Length == 0 || !raw.Contains('.') || raw.Contains(' '))
        {
            Status.Text = "Это не похоже на имя сайта. Нужно что-то вроде example.com.";
            return;
        }

        var mode = OwnMode.SelectedIndex switch
        {
            0 => RoutingMode.Direct,
            1 => RoutingMode.Desync,
            _ => RoutingMode.Proxy,
        };

        // Для десинка спрашиваем, чем именно чинить. Без этого вопроса режим
        // означал лишь «мимо туннеля»: что сделать с именем, решал пресет,
        // а не попавшему ни в один его список не делалось ничего.
        string? recipe = null;

        // У своего домена заголовок и проверяемое имя — одно и то же.
        // Защита от второго окна нужна и здесь: кнопку успевают нажать
        // дважды, а второе модальное окно поверх первого делает первое
        // недоступным — «выбрать» в нём просто не отзывается.
        if (mode == RoutingMode.Desync)
        {
            if (_asking)
                return;

            _asking = true;

            try
            {
                if (!AskRecipe(raw, raw, out recipe))
                    return;
            }
            finally
            {
                _asking = false;
            }
        }

        try
        {
            var file = UserRulesFile.Load();
            file.Set(MatchKind.Domain, "*." + raw, mode, recipe);
            file.Save();

            OwnDomain.Text = string.Empty;

            ShowOwn();

            Status.Text = string.IsNullOrEmpty(recipe)
                ? $"Записано: {raw} → {Describe(mode)}. Применится при следующем запуске движков."
                : $"Записано: {raw} → десинк рецептом «{recipe}». "
                  + "Применится при следующем запуске движков.";

            this.Offer($"Добавлен маршрут: {raw}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Спрашивает рецепт; <c>false</c> — человек передумал добавлять правило.
    /// </summary>
    /// <remarks>
    /// Отказ от окна — это отмена всего действия, а не молчаливое «решает
    /// пресет». Иначе закрытый крестиком выбор превращался бы в правило,
    /// которого не просили.
    /// </remarks>
    /// <param name="title">Что писать в заголовке окна.</param>
    /// <param name="domain">На чём проверять рецепты — настоящее имя.</param>
    private bool AskRecipe(string title, string domain, out string? recipe)
    {
        recipe = null;

        try
        {
            // Без выбранного пресета выбирать не из чего: рецепты берутся
            // из него. Правило при этом записывается как прежде.
            if (AppSettings.Load(AppSettings.DefaultPath).PresetName is not { } name
                || ZapretPaths.FindPreset(name) is not { } presetPath)
            {
                return true;
            }

            var window = new RecipeWindow(title, domain, new PresetReader().Load(presetPath))
            {
                Owner = Window.GetWindow(this),
            };

            if (window.ShowDialog() != true)
                return false;

            recipe = string.IsNullOrEmpty(window.Chosen) ? null : window.Chosen;
            return true;
        }
        catch (Exception)
        {
            // Пресет не прочитался — выбирать не из чего, но и мешать записи
            // правила незачем: без рецепта оно ведёт себя как прежде.
            return true;
        }
    }

    private void OnRemoveOwn(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value })
            return;

        try
        {
            var file = UserRulesFile.Load();
            file.Remove(MatchKind.Domain, value);
            file.Save();

            ShowOwn();
            Status.Text = $"Убрано: {value}. Применится при следующем запуске движков.";
            this.Offer($"Убран маршрут: {value}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось убрать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Записывает выбранный маршрут.
    /// </summary>
    /// <remarks>
    /// Применяется перезапуском, и об этом сказано прямо. Сами не
    /// перезапускаем: движки несут весь трафик машины, и ронять их в ответ
    /// на выбор в списке — не та цена, на которую человек соглашался.
    /// </remarks>
    private void OnRoute(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || sender is not ComboBox { Tag: string key } box)
            return;

        // Выбор того же самого — не выбор. Список получает SelectedIndex
        // при каждой пересборке строк, и без этой проверки открытие папки
        // писало правило и звало уведомление о перезапуске по разу
        // на каждую часть внутри.
        //
        // Сверяется Applied, а не Choice: в Choice привязка уже положила
        // новое значение, и сравнение с ним всегда говорило «то же самое».
        if (box.DataContext is PartRow current && box.SelectedIndex == current.Applied)
            return;

        var parts = key.Split('|', 2);

        if (parts.Length != 2)
            return;

        var mode = box.SelectedIndex switch
        {
            0 => RoutingMode.Direct,
            1 => RoutingMode.Desync,
            _ => RoutingMode.Proxy,
        };

        var match = parts[0] == "ipset" ? MatchKind.IpSet : MatchKind.HostList;

        // Рецепт спрашиваем и у сервисов, а не только у своих доменов.
        // Именно здесь он и нужен чаще: сервис — это список из десятков имён,
        // и когда пресет их не открывает, руками разбираться не в чем.
        //
        // У списков адресов не спрашиваем: рецепт применяется по имени
        // в приветствии TLS, а в правиле по адресу имени нет вовсе.
        // Проверять надо на настоящем имени из списка, а не на названии
        // сервиса: «discord» не разрешается, и на нём любой рецепт отвечает
        // «не помогает».
        var example = box.DataContext is PartRow row ? row.Probe : null;

        if (mode == RoutingMode.Desync && match == MatchKind.HostList
            && !string.IsNullOrWhiteSpace(example))
        {
            // Окно выбора поднимается не отсюда, а следующим ходом очереди
            // сообщений. Внутри обработчика выбора нельзя: список ещё
            // не закончил свою работу и успевает поднять событие второй раз,
            // а мы на нём открываем второе модальное окно. Тогда первое
            // остаётся на экране, но нажать в нём ничего нельзя — поверх
            // стоит модальное, и «выбрать» не отзывается вовсе.
            AskLater(key, parts[1], example, match, mode);
            return;
        }

        Write(key, parts[1], match, mode, recipe: null);
    }

    /// <summary>Не спрашиваем ли рецепт прямо сейчас.</summary>
    /// <remarks>
    /// Страховка на случай, если событие выбора придёт ещё раз, пока окно
    /// открыто: второе окно поверх первого делает первое недоступным.
    /// </remarks>
    private bool _asking;

    private void AskLater(
        string key,
        string list,
        string example,
        MatchKind match,
        RoutingMode mode)
    {
        if (_asking)
            return;

        _asking = true;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (!AskRecipe(ServiceName(list), example, out var recipe))
                {
                    // Отказ от выбора — отказ от всего действия. Список при
                    // этом остался на новом значении, и его надо вернуть.
                    Reload();
                    return;
                }

                Write(key, list, match, mode, recipe);
            }
            finally
            {
                _asking = false;
            }
        }), DispatcherPriority.Background);
    }

    private void Write(string key, string list, MatchKind match, RoutingMode mode, string? recipe)
    {
        var parts = key.Split('|', 2);

        try
        {
            var file = UserRulesFile.Load();
            file.Set(match, parts[1], mode, recipe);
            file.Save();

            // Перечитываем сразу: подпись слева и сводка папки считаются при
            // сборке строк, и без этого они показывали прежний маршрут до тех
            // пор, пока человек не уйдёт с вкладки и не вернётся. Раскрытые
            // папки перерисовку переживают, так что список остаётся на месте.
            Reload();

            Status.Text = string.IsNullOrEmpty(recipe)
                ? $"Записано: {parts[1]} → {Describe(mode)}. Применится при следующем запуске движков."
                : $"Записано: {parts[1]} → десинк рецептом «{recipe}». "
                  + "Применится при следующем запуске движков.";

            this.Offer("Маршрут изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Рецепт, выбранный для этого списка; <c>null</c> — решает пресет.
    /// </summary>
    /// <remarks>
    /// Читается из пользовательских правил при каждой сборке строк, а не
    /// хранится рядом: правило могли поправить в файле руками, и вторая копия
    /// разошлась бы с ним молча.
    /// </remarks>
    private string? RecipeFor(string listPath) =>
        _own.Entries.FirstOrDefault(e =>
            e.Match == MatchKind.HostList
            && e.Mode == RoutingMode.Desync
            && e.Matches(listPath))?.Recipe;

    /// <summary>
    /// Чем именно пойдёт эта часть.
    /// </summary>
    /// <remarks>
    /// <para>
    /// «Десинк» сам по себе не говорит ничего: он означает «мимо туннеля,
    /// решает пресет», а решить пресет может и «не трогать», и «ничего
    /// не знаю об этом имени». И то и другое выглядело в списке одинаково
    /// с работающим правилом — а это ровно тот случай, из-за которого
    /// разбираться приходилось замерами.
    /// </para>
    /// <para>
    /// Показывается название приёма, а не имя секции, у которой он взят.
    /// Секции пресета названы по тому, что чинят, и подпись выходила вроде
    /// «десинк: rutracker.org» у Speedtest — человек читал это как ошибку.
    /// </para>
    /// <para>
    /// Первая совпавшая секция и решает: winws2 отдаёт пакет первому профилю,
    /// чей фильтр совпал, и дальше не смотрит.
    /// </para>
    /// </remarks>
    private string Describe(ServiceRouting.PartStatus part)
    {
        if (part.Mode != RoutingMode.Desync)
            return part.DescribeMode();

        // Свой выбор важнее пресетовского: он и стоит первым в командной
        // строке. Хранится имя секции, показывается название приёма.
        if (RecipeFor(part.Part.List) is { } stored && !string.IsNullOrWhiteSpace(stored))
        {
            return _preset is not null && DesyncRecipes.Find(_preset, stored) is { } own
                ? $"десинк: {own.Technique}"
                : $"десинк: {stored} — нет в пресете";
        }

        if (_preset is null)
            return "десинк: пресет не выбран";

        // Правила по адресам рецепта не получают: десинк отбирает трафик
        // по имени в приветствии TLS, а в таких пакетах имени нет.
        if (part.Part.ByAddress)
            return "десинк";

        var section = PresetPorts
            .SectionsFor(_preset, _zapretRoot, part.Domains)
            .FirstOrDefault();

        if (section is null)
            return "десинк: пресет не чинит";

        return section.IsPassThrough
            ? "десинк: пресет пропускает"
            : $"десинк: {Technique(section)}";
    }

    /// <summary>Приёмы секции по порядку, без настроек.</summary>
    private static string Technique(ZapretSection section) =>
        string.Join(" + ", section.DesyncRecipes
            .Select(r => r.IndexOf(':') is var c && c < 0 ? r : r[..c])
            .Distinct());

    /// <summary>
    /// Имя списка в читаемое название — для заголовка окна рецептов.
    /// </summary>
    /// <remarks>
    /// Путь вида <c>config/lists/discord-media.txt</c> в заголовке «Чем
    /// чинить» выглядел бы вопросом не о том. Человек выбирает маршрут
    /// сервису, а не файлу.
    /// </remarks>
    private static string ServiceName(string listPath)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(listPath);

        return name.Length == 0 ? listPath : name.Replace('-', ' ');
    }

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Direct => "напрямую",
        RoutingMode.Desync => "десинк",
        _ => "через VPN",
    };
}
