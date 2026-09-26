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
public sealed record PartRow
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }

    /// <summary>Имя из списка: по нему берётся значок и ищет поиск.</summary>
    public string? Example { get; init; }

    /// <summary>Полный путь к файлу списка; <c>null</c> — ссылки «список» нет.</summary>
    public string? ListPath { get; init; }

    public Visibility ListShown => ListPath is null ? Visibility.Collapsed : Visibility.Visible;

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

    /// <summary>
    /// Это свой домен — его можно убрать.
    /// </summary>
    /// <remarks>
    /// У каталожной части убирать нечего: она не добавлена человеком
    /// и никуда не денется. Ей меняют маршрут, а не существование.
    /// </remarks>
    public bool Own { get; init; }

    public Visibility RemoveShown => Own ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Что случится по нажатию — словами, в подсказке.
    /// </summary>
    /// <remarks>
    /// Кнопка стала значком, и слово «снять пин» с неё ушло. Значок один
    /// на оба состояния — их различает только цвет, — и без подсказки
    /// человек не знает, поставит он пин или снимет уже стоящий.
    /// Разница велика: одно переписывает системный файл, другое возвращает
    /// его как было.
    /// </remarks>
    public string PinHint => HasPin
        ? "Снять пин: адрес перестанет быть прибитым в hosts. Маршрут не тронем."
        : "Прибить адрес в hosts. Маршрут при этом уйдёт напрямую — иначе правило "
            + "сработает поверх адреса и уведёт соединение мимо него.";

    /// <summary>
    /// Цвет значка: прибитое выделено, остальное обычным.
    /// </summary>
    /// <remarks>
    /// Обычным, а не приглушённым. Пин нарисован тонкой обводкой, и на
    /// четырнадцати точках приглушённый цвет делал его едва различимым —
    /// у слова «пин», которое здесь стояло раньше, такой беды не было:
    /// буквы толще линий.
    /// </remarks>
    public Brush PinColor =>
        (Brush)Application.Current.FindResource(HasPin ? "Accent" : "Text");

    public BitmapImage? Icon { get; set; }

    public Visibility IconShown => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Поле слева от значка — под ширину колонки со стрелкой.
    /// </summary>
    /// <remarks>
    /// Внутри карточки, а не полем самой карточки. Сдвинув карточку, мы
    /// выровняли значки, но укоротили строку: у папки она шла во всю ширину,
    /// а у сервиса без стрелки кончалась на те же двадцать четыре раньше.
    /// Вложенным частям сдвиг карточки как раз нужен — он и показывает
    /// вложенность, — а строке верхнего уровня нужно только поле внутри.
    /// </remarks>
    public double Lead { get; init; }

    public Thickness IconMargin => new(Lead, 0, 12, 0);
}

/// <summary>Строка таблицы порядка вычисления.</summary>
public sealed record RuleRow(
    string Ordinal,
    string Kind,
    string Value,
    string Mode,
    string Server,
    Brush Color)
{
    /// <summary>Номер в rules.user.yaml; -1 — правило не наше, не таскается.</summary>
    public int UserIndex { get; init; } = -1;

    /// <summary>Группа движка: переставлять имеет смысл только внутри неё.</summary>
    public int Tier { get; init; }

    public bool Movable => UserIndex >= 0;

    public Visibility GripShown => Movable ? Visibility.Visible : Visibility.Hidden;
}

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

    /// <summary>
    /// Разворачивать нечего: часть одна, и она же вся строка.
    /// </summary>
    /// <remarks>
    /// Папка вокруг единственной части обещает содержимое, которого нет:
    /// раскрыл — а внутри то же самое имя, только названное «Всё». Такие
    /// показываются одной строкой без шапки и без стрелки, а имя сервиса
    /// переходит в саму строку.
    /// </remarks>
    public bool Single => Parts.Count == 1;

    /// <summary>Шапка — только у того, что вправду сворачивается.</summary>
    public Visibility HeaderShown => Single ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PartsShown =>
        Single || Open ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Отступ строк слева — ровно под ширину колонки со стрелкой.
    /// </summary>
    /// <remarks>
    /// Одинаковый и у вложенных, и у одиночных, и это выравнивает значки
    /// по всему списку в одну линию. У папки перед значком стоит стрелка
    /// и сдвигает его вправо; у сервиса без стрелки значок прижимался
    /// к краю, и список шёл зигзагом.
    ///
    /// Вложенность при этом не теряется: карточка части начинается на те же
    /// двадцать четыре правее карточки папки, и видно её по самой карточке,
    /// а не по сдвигу значка внутри неё.
    /// </remarks>
    public Thickness PartsMargin => Single ? new Thickness(0) : new Thickness(24, 0, 0, 0);

    /// <summary>
    /// Значок сервиса — тот же, что у первой его части.
    /// </summary>
    /// <remarks>
    /// Шапка без значка выходила ниже строки с ним, и список шёл ступеньками:
    /// у папки одна высота, у одиночного сервиса другая. Взять первый значок
    /// честно — части одного сервиса живут на одном домене, и картинка у них
    /// общая.
    /// </remarks>
    public BitmapImage? Icon => Parts.Count > 0 ? Parts[0].Icon : null;

    public string Letter => Parts.Count > 0 ? Parts[0].Letter : "·";

    public Visibility IconShown => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Поворот значка раскрытия.
    /// </summary>
    /// <remarks>
    /// Угол, а не знак: стрелка рисуется фигурой — см. <see cref="Chevrons"/>.
    /// </remarks>
    public double ChevronAngle => Chevrons.Angle(Open);

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

    /// <summary>Его списки, прочитанные один раз.</summary>
    private PresetZones? _zones;

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

            // Свои домены прежнего вида — domain-правила без файла —
            // переводятся в списки здесь, при первом открытии раздела после
            // обновления (OwnLists, 26.09). Порядок, режим и рецепт остаются.
            try
            {
                if (OwnLists.Migrate(userRules) > 0)
                    userRules.Save();
            }
            catch (Exception)
            {
                // Не вышло — строки просто покажутся по-старому, без файла;
                // движок понимает оба вида.
            }

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

                _zones = _preset is null ? null : PresetZones.Build(_preset, zapretRoot);

                _providers = ZapretPaths.Discover() is { } install
                    ? LuaModules.Providers(LuaModules.Scan(install.LuaDirectory))
                    : null;
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

                if (parts.Count == 0)
                    continue;

                // Полка с разными сервисами раскладывается сразу: человек ищет
                // Signal, а не «мессенджеры», и лишний щелчок стоит между ним
                // и его сервисом. Каждая часть получает свою строку.
                if (service.Grouping)
                {
                    services.AddRange(parts.Select(p =>
                        new ServiceRow(p.Title, [p with { Lead = 24 }])));
                    continue;
                }

                // Сервис из одной части папкой не становится: разворачивать
                // нечего, а стрелка обещает содержимое, которого нет. Имя
                // сервиса переходит в саму строку — часть у таких зовётся
                // «Всё», и это ничего не говорит в общем списке.
                if (parts.Count == 1)
                {
                    // Поле под стрелку — внутри карточки: строка верхнего уровня
                    // должна идти во всю ширину, а значок стоять там же, где
                    // у папки.
                    services.Add(new ServiceRow(
                        service.Name,
                        [parts[0] with { Title = service.Name, Lead = 24 }]));

                    continue;
                }

                services.Add(new ServiceRow(service.Name, parts));
            }

            // Свои домены — в общий список, наравне с каталожными.
            // Решение владельца 20.09: им так же нужны значок, пин и рецепт,
            // а отдельная карточка ничего этого не давала и вдобавок делила
            // список надвое по признаку, который человеку безразличен —
            // по тому, знали мы это имя заранее или нет.
            services.AddRange(OwnRows(userRules));

            MarkPins(services, zapretRoot);

            // Раскрытые папки переживают перерисовку. Прежде любое действие
            // внутри папки — пин, свой маршрут, обновление списка — собирало
            // строки заново, и всё захлопывалось: человек выбирал маршрут
            // одной части, а искать следующую приходилось с начала.
            foreach (var service in services)
                service.Open = _open.Contains(service.Name);

            _all = services;

            // Отбор переживает пересборку. Любое действие внутри строки —
            // смена маршрута, пин, выбор рецепта — собирает список заново,
            // и без этого он возвращался целиком: найденное пропадало,
            // и приходилось дописывать пробел в поле, чтобы отбор случился
            // ещё раз.
            Services.ItemsSource = InChosenOrder(services);
            Filter();

            ShowOwn();

            Status.Text = problems.Count == 0
                // Про то, куда пишется выбор, теперь говорит подсказка
                // у заголовка: строка эта стояла над списком постоянно,
                // а нужна была один раз. Счёт сервисов остался — он
                // меняется и отвечает на вопрос «всё ли прочиталось».
                ? $"Сервисов: {services.Count}."
                : $"Часть списков не нашлась, и эти правила не действуют: {string.Join("; ", problems.Take(3))}";

            ShowOrder(engine);

            ShowBook();
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
        var kind = part.Part.ByAddress ? RouteKeys.IpSet : RouteKeys.HostList;

        // Без «например …»: вместо примера — ссылка на сам список (владелец,
        // 26.09). Одно имя из списка ничего не говорит о том, что в нём,
        // а у Notion пример «notion.so» и вовсе увёл в сторону: сайт давно
        // живёт на notion.com, которого в списке не было.
        var detail = part.Part.ByAddress
            ? Count(part.DomainCount, "подсеть", "подсети", "подсетей")
            : Count(part.DomainCount, "домен", "домена", "доменов");

        if (!part.Explicit)
            detail += " · по умолчанию";

        var (color, choice) = part.Mode switch
        {
            RoutingMode.Direct => ("Muted", 0),
            RoutingMode.Desync => ("Warn", 1),
            _ => ("Accent", 2),
        };

        var host = (part.Example ?? part.Part.Name).TrimStart('*', '.');

        var mode = Describe(part);

        return new PartRow
        {
            // Уже загруженный значок ставится сразу. Раздел пересоздаётся при
            // каждом заходе, и без этого он начинал бы с букв, а значки
            // проступали бы заново — при живом кэше в памяти.
            // С диска, если в памяти пусто: после перезапуска памяти нет вовсе,
            // и список открывался буквами при полном кэше на диске.
            Icon = host.Contains('.') && !IsAddress(host) ? SiteIcons.Cached(host) : null,
            Key = RouteKeys.Make(kind, part.Part.List),
            Title = part.Part.Name,

            // Обычно ровно то имя, что лежит в Example: оно
            // взято из самого списка и потому заведомо им покрыто. Каталог
            // может назвать другое, и тогда верить надо ему: запись списка —
            // это зона, а фильтр стоит на имени, и у голоса Discord апекс
            // зоны оказался единственным именем, которое не закрыто.
            Probe = part.Part.ByAddress ? null : (part.Part.Probe ?? host),

            Detail = detail,
            Example = part.Example?.TrimStart('*', '.'),
            ListPath = ListFile(part.Part.List),

            // Чем именно пойдёт часть — прямо в подписи маршрута. «Десинк»
            // сам по себе не говорит ничего: решает пресет, и решить он может
            // в том числе «не знаю такого имени».
            Mode = mode,

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

        // Свои правила узнаются в файле по паре «тип + значение» — тем же
        // ключом, каким их находит Set. Номер из движка не годится: он
        // считается без выключенных записей, и после первой выключенной
        // перестановка сдвигала бы не ту строку.
        var own = UserRulesFile.Load().Entries.ToList();

        var rows = ruleSet.Rules.Select(rule => new RuleRow(
            rule.Ordinal.ToString(),
            rule.Match.ToString().ToLowerInvariant(),
            rule.Value,
            rule.Mode.ToString().ToLowerInvariant(),
            rule.Mode == RoutingMode.Proxy ? rule.Server ?? ruleSet.DefaultServer ?? "auto" : "—",
            (Brush)FindResource(ColorOf(rule.Mode)))
        {
            UserIndex = rule.Source == RuleSource.User
                ? own.FindIndex(e => e.Match == rule.Match && e.Matches(rule.Value))
                : -1,
            Tier = RuleEngine.Tier(rule.Match, rule.Source, rule.Value),
        }).ToList();

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
            + "Побеждает то, до которого очередь доходит раньше. Свои правила с ручкой ⠿ "
            + "перетаскиваются внутри своей группы.";

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

    /// <summary>Где нажали на строку порядка — чтобы отличить перетаскивание от щелчка.</summary>
    private Point? _dragFrom;

    private void OnRulePress(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragFrom = sender is FrameworkElement { DataContext: RuleRow { Movable: true } }
            ? e.GetPosition(this)
            : null;
    }

    /// <summary>
    /// Начинает перетаскивание своего правила.
    /// </summary>
    /// <remarks>
    /// Только когда мышь ушла дальше системного порога: иначе всякое
    /// нажатие с дрожью руки становилось бы перестановкой.
    /// </remarks>
    private void OnRuleDrag(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
            return;

        var moved = e.GetPosition(this) - from;

        if (Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance
            && Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance)
        {
            return;
        }

        _dragFrom = null;

        if (sender is FrameworkElement { DataContext: RuleRow row } element)
            DragDrop.DoDragDrop(element, row, DragDropEffects.Move);
    }

    private void OnRuleDragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanDrop(e, out _, out _) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Переставляет своё правило и сохраняет порядок в rules.user.yaml.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Просьба владельца 26.09. Внутри группы движок проверяет правила
    /// в порядке файла, а файл ведёт программа — менять порядок было нечем.
    /// </para>
    /// <para>
    /// Только свои и только внутри группы (<see cref="RuleEngine.Tier"/>).
    /// Правило по процессу всё равно проверится раньше любого доменного,
    /// а своё — раньше заводского, куда бы его ни уронили; разрешить такую
    /// перестановку значило бы показать порядок, которого у движка нет.
    /// </para>
    /// </remarks>
    private void OnRuleDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (!CanDrop(e, out var source, out var target))
        {
            if (source is not null && target is not null && source != target)
                Status.Text = "Переставить можно только внутри своей группы: правило по процессу "
                    + "проверяется раньше доменного, доменное — раньше адресного, а ваше — раньше "
                    + "заводского, куда бы его ни поставить.";

            return;
        }

        try
        {
            var file = UserRulesFile.Load();

            if (!file.Move(source!.UserIndex, target!.UserIndex))
                return;

            file.Save();
            Reload();

            // Раскрытым: перестановку делали, глядя в него, и свёрнутый после
            // сохранения список заставил бы искать, куда встало правило.
            OrderPanel.Visibility = Visibility.Visible;
            Chevrons.Turn(OrderChevron, true);

            Status.Text = $"Порядок сохранён: {source.Value} встал на место {target.Value}. "
                + "Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сохранить порядок: " + ex.GetBaseException().Message;
        }
    }

    private static bool CanDrop(DragEventArgs e, out RuleRow? source, out RuleRow? target)
    {
        source = e.Data.GetData(typeof(RuleRow)) as RuleRow;
        target = (e.OriginalSource as FrameworkElement)?.DataContext as RuleRow
            ?? (e.Source as FrameworkElement)?.DataContext as RuleRow;

        return source is { Movable: true }
            && target is { Movable: true }
            && source != target
            && source.Tier == target.Tier;
    }

    private void OnOrderToggle(object sender, RoutedEventArgs e)
    {
        var open = OrderPanel.Visibility != Visibility.Visible;

        OrderPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        Chevrons.Turn(OrderChevron, open);
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
        // Из поля, а не разбором подписи: «например …» из подписи ушло,
        // и разбор молча оставил бы все части без значков.
        if (part.Example is not { } host || !host.Contains('.'))
            return null;

        // У подсети значка нет и быть не может: значок берут у сайта, а сайт
        // узнают по имени. Прежде такие имена спрашивались наравне со всеми,
        // и в кэше оседали пустышки вроде «104.244.42.0_24.ico» — по одному
        // сетевому запросу на каждую впустую.
        return IsAddress(host) ? null : host;
    }

    /// <summary>Похоже ли на адрес или подсеть, а не на имя.</summary>
    private static bool IsAddress(string host)
    {
        var address = host.Split('/')[0];

        return System.Net.IPAddress.TryParse(address, out _);
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
            var zones = RouteKeys.Zones(part.Key, zapretRoot);

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
    /// Свои домены — строками общего списка.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Каждый своей строкой без папки: частей у него нет, разворачивать
    /// нечего. Устроен он так же, как одиночный сервис из каталога, и это
    /// не сходство ради сходства — от этого ему и достаются даром значок
    /// сайта, кнопка пина, выбор маршрута и окно рецептов.
    /// </para>
    /// <para>
    /// Ключ начинается с <c>own</c>, значение — путь своего списка
    /// (<see cref="OwnLists"/>, с 26.09): по ключу обработчики узнают,
    /// что строка своя — её можно убрать, и убирается она вместе с файлом.
    /// </para>
    /// </remarks>
    private IReadOnlyList<ServiceRow> OwnRows(UserRulesFile userRules)
    {
        var rows = new List<ServiceRow>();

        foreach (var entry in userRules.Entries.Where(e => e.Match == MatchKind.HostList && OwnLists.IsOwn(e.Value)))
        {
            var domain = OwnLists.DomainOf(entry.Value);
            var count = RouteKeys.Zones("own|" + entry.Value, _zapretRoot).Count;

            var (color, choice) = entry.Mode switch
            {
                RoutingMode.Direct => ("Muted", 0),
                RoutingMode.Desync => ("Warn", 1),
                _ => ("Accent", 2),
            };

            var part = new PartRow
            {
                Icon = SiteIcons.Cached(domain),
                Key = "own|" + entry.Value,
                Title = domain,
                Probe = domain,

                // Рецепт называется прямо в подписи: выбранный однажды,
                // он иначе пропадал бы из виду, и понять, чем чинится имя,
                // можно было бы только заглянув в yaml.
                Detail = "свой домен"
                    + (count > 1 ? " · " + Count(count, "имя", "имени", "имён") : string.Empty)
                    + (entry.Mode == RoutingMode.Desync
                        && !string.IsNullOrWhiteSpace(entry.Recipe)
                            ? $" · рецепт: {entry.Recipe}"
                            : string.Empty),

                Example = domain,
                ListPath = ListFile(entry.Value),

                Mode = Describe(entry.Mode),
                Color = (Brush)Application.Current.FindResource(color),
                Choice = choice,
                Applied = choice,
                CanRoute = true,
                CanPin = true,
                Own = true,
                Letter = domain.Length > 0 ? domain[..1].ToUpperInvariant() : "·",
                Lead = 24,
            };

            rows.Add(new ServiceRow(domain, [part]));
        }

        return rows;
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
        if (sender is not Button { Tag: string key }
            || key.Split('|', 2) is not [var kind, var value])
        {
            return;
        }

        var row = _all.SelectMany(s => s.Parts).FirstOrDefault(p => p.Key == key);

        // Свой домен в каталоге не ищется — его там нет. Всё, что нужно
        // окну, лежит в самом имени.
        if (kind == RouteKeys.Own)
        {
            if (row is { HasPin: true })
            {
                Unpin(key, value);
                return;
            }

            Open(new PinWindow(value, RouteKeys.Zones(key, _zapretRoot)), key, OwnLists.DomainOf(value));
            return;
        }

        var service = ServiceCatalog.All.FirstOrDefault(s =>
            s.Parts.Any(p => string.Equals(p.List, value, StringComparison.OrdinalIgnoreCase)));

        var part = service?.Parts.FirstOrDefault(p =>
            string.Equals(p.List, value, StringComparison.OrdinalIgnoreCase));

        if (service is null || part is null)
            return;

        if (row is { HasPin: true })
        {
            Unpin(key, part.Name);
            return;
        }

        Open(new PinWindow(service, part), key, part.Name);
    }

    /// <summary>Показывает окно пина и разбирается с последствиями.</summary>
    private void Open(PinWindow window, string key, string label)
    {
        window.Owner = Window.GetWindow(this);
        window.ShowDialog();

        if (!window.Changed)
            return;

        RouteDirectIfPinned(key);

        Reload();
        this.Offer($"«{label}»: маршрут или пин изменены");
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
    private void RouteDirectIfPinned(string key)
    {
        try
        {
            if (key.Split('|', 2) is not [var kind, var value])
                return;

            var zones = RouteKeys.Zones(key, ZapretPaths.Discover()?.Root);

            if (!HostsEditor.Pins().Keys.Any(name => Covers(zones, name)))
                return;

            var file = UserRulesFile.Load();
            file.Set(RouteKeys.MatchOf(kind, value), value, RoutingMode.Direct);
            file.Save();
        }
        catch (Exception)
        {
            // Не записалось — пин всё равно поставлен, а маршрут виден
            // в списке и правится вручную.
        }
    }

    private void Unpin(string key, string label)
    {
        try
        {
            var zones = RouteKeys.Zones(key, ZapretPaths.Discover()?.Root);

            var ours = HostsEditor.Pins().Keys.Where(pin => Covers(zones, pin)).ToList();

            if (ours.Count == 0)
                return;

            var result = HostsEditor.Unpin(ours);
            HostsEditor.FlushDns();

            Reload();

            Status.Text = $"Снято имён: {ours.Count}. Осталось прибитых: {result.Pinned}. "
                + "Маршрут не трогали — он остался таким, каким был.";

            this.Offer($"Пин снят: {label}");
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

    /// <summary>Чем раскладывать; подробности — в <see cref="RouteOrder"/>.</summary>
    private RouteOrderBy _order;

    private void OnSort(object sender, SelectionChangedEventArgs e)
    {
        // Список поднимает событие прямо при разборе разметки — когда
        // применяется SelectedIndex="0", — а соседние элементы к этому
        // мгновению ещё не созданы. Отсюда NullReferenceException
        // на создании вкладки, который и поймала проверка.
        if (!IsInitialized || Sort is null || Sort.SelectedIndex < 0)
            return;

        _order = (RouteOrderBy)Sort.SelectedIndex;

        // Пересобираем показ, не перечитывая правила: порядок — дело показа,
        // и лезть за ним на диск незачем.
        Filter();
    }

    /// <summary>Раскладывает так, как выбрано в списке.</summary>
    private IReadOnlyList<ServiceRow> InChosenOrder(IEnumerable<ServiceRow> rows) =>
        RouteOrder.Apply(rows, _order);

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

    private void OnSearch(object sender, TextChangedEventArgs e) => Filter();

    /// <summary>Показывает то, что подходит под поиск.</summary>
    private void Filter()
    {
        var needle = Search.Text.Trim();

        if (needle.Length == 0)
        {
            AddOffer.Visibility = Visibility.Collapsed;
            Services.ItemsSource = InChosenOrder(_all);
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

        Services.ItemsSource = InChosenOrder(found);

        // Добавить предлагается, только когда искать больше нечего: вписанное
        // похоже на имя сайта, а в списке оно не нашлось ни частью, ни доменом.
        // Нашлось — значит, такое уже есть, и второе правило на него было бы
        // двойником, спорящим с первым.
        var domain = found.Count == 0 ? DomainInput.Normalize(needle) : null;

        AddOffer.Visibility = domain is null ? Visibility.Collapsed : Visibility.Visible;

        if (domain is not null)
            AddOfferText.Text = $"«{domain}» в списке нет. Добавить своим доменом — сперва «напрямую», дальше правится в его строке.";

        Status.Text = found.Count > 0
            ? $"Найдено частей: {found.Sum(s => s.Parts.Count)}."
            : domain is null
                ? $"По «{needle}» ничего нет. Чтобы добавить свой домен, впишите имя сайта, например example.com."
                : string.Empty;
    }

    private void OnSearchKey(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && AddOffer.Visibility == Visibility.Visible)
            AddOwn();
    }

    private static bool Matches(string service, PartRow part, string needle) =>
        service.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || part.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || part.Detail.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || (part.Example?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>«1 домен», «3 домена», «12 доменов».</summary>
    private static string Count(int n, string one, string few, string many)
    {
        int tens = n % 100;
        int last = n % 10;

        var word = tens is >= 11 and <= 14 ? many
            : last == 1 ? one
            : last is >= 2 and <= 4 ? few
            : many;

        return $"{n} {word}";
    }

    /// <summary>Файл списка, если он лежит на диске; <c>null</c> — открывать нечего.</summary>
    /// <remarks>
    /// Относительный путь считается от корня установки: окно при запуске
    /// уходит туда (InstallRoot), и так же его читает загрузчик правил.
    /// </remarks>
    private static string? ListFile(string? list)
    {
        if (string.IsNullOrWhiteSpace(list))
            return null;

        try
        {
            var full = System.IO.Path.GetFullPath(list);
            return System.IO.File.Exists(full) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Открывает список части в Блокноте — посмотреть и поправить.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Просьба владельца 26.09: вместо «например notion.so» — ссылка
    /// на сам файл. Правка вручную здесь законна: config\lists — наши
    /// списки, текстом, по имени в строке, и программа их не переписывает.
    /// </para>
    /// <para>
    /// Блокнотом, как файл hosts: свой редактор ради текстового списка —
    /// лишнее место, где что-то может разойтись. Правка вступает в силу
    /// при следующем запуске движков: списки читаются при сборке конфига.
    /// </para>
    /// </remarks>
    private void OnOpenList(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Documents.Hyperlink { DataContext: PartRow { ListPath: { } path } })
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            })?.Dispose();

            Status.Text = $"Открыт {System.IO.Path.GetFileName(path)}. Правка применится при следующем запуске движков; "
                + "раздел покажет её, когда откроете его заново.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось открыть список: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Подпись карточки «Свой домен».
    /// </summary>
    /// <remarks>
    /// Списка добавленных карточка больше не держит: с 0.6.3 свои домены
    /// стоят в общем списке ниже. Подпись осталась и говорит, сколько их
    /// и где они, — иначе человек, добавив домен, увидел бы, что карточка
    /// не изменилась, и добавил бы второй раз.
    /// </remarks>
    private void ShowOwn()
    {
        // Без звёздочки: правило хранится как «*.example.com», но вводил
        // человек «example.com», и показывать ему наше устройство хранения
        // вместо его же имени незачем. В списке ниже они и так без неё.
        var own = UserRulesFile.Load().Entries
            .Where(entry => entry.Match == MatchKind.HostList && OwnLists.IsOwn(entry.Value))
            .Select(entry => OwnLists.DomainOf(entry.Value))
            .ToList();

        OwnSummary.Text = own.Count == 0
            ? "Впишите сайт в поле ниже: если его нет в списке, программа предложит добавить."
            : $"Своих правил: {own.Count} — {string.Join(", ", own.Take(3))}"
              + (own.Count > 3 ? $" и ещё {own.Count - 3}" : string.Empty)
              + ". Они стоят в списке ниже вместе с сервисами.";
    }

    private void OnAddOwn(object sender, RoutedEventArgs e) => AddOwn();

    /// <summary>
    /// Записывает свой домен из поля поиска — «напрямую».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Звёздочка ставится сама: имя означает зону, и <c>example.com</c> должен
    /// покрывать поддомены — иначе человек напишет корень, а картинки с
    /// <c>cdn.example.com</c> пойдут мимо правила, и понять это по виду
    /// не выйдет.
    /// </para>
    /// <para>
    /// «Напрямую», а не выбранное заранее (владелец, 23.09). Режим, рецепт
    /// и пин правятся в строке, которая появится в списке, — там же, где
    /// у всех остальных частей; второй набор тех же выборов у поля был бы
    /// ещё одним местом, где они расходятся.
    /// </para>
    /// </remarks>
    private void AddOwn()
    {
        var name = DomainInput.Normalize(Search.Text);

        if (name is null)
        {
            Status.Text = "Это не похоже на имя сайта. Нужно что-то вроде example.com.";
            return;
        }

        try
        {
            var file = UserRulesFile.Load();
            // Файлом, а не одной строкой в yaml (владелец, 26.09): к нему
            // можно дописать другие имена того же сайта — ссылка «список».
            file.Set(MatchKind.HostList, OwnLists.Create(name), RoutingMode.Direct, recipe: null);
            file.Save();

            // Поле не очищается: по нему же отфильтрован список, и добавленный
            // домен остаётся на экране одной строкой — ровно той, где его
            // дальше править.
            Reload();

            Status.Text = $"Записано: {name} → напрямую. Режим меняется в его строке ниже. "
                + "Применится при следующем запуске движков.";

            this.Offer($"Добавлен маршрут: {name}");
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

    /// <summary>
    /// Убирает своё доменное правило.
    /// </summary>
    /// <remarks>
    /// Кнопка переехала из карточки «Свой домен» в строку общего списка,
    /// поэтому в <c>Tag</c> теперь ключ строки, а не голое имя. Разбирается
    /// он тем же способом, что у пина и выбора маршрута: один вид ключа
    /// на все действия над строкой.
    /// </remarks>
    private void OnRemoveOwn(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }
            || RouteKeys.Parse(key) is not (RouteKeys.Own, var value))
        {
            return;
        }

        try
        {
            var file = UserRulesFile.Load();
            // И правило, и файл: свой список без правила — мусор, который
            // никто не читает и никто не увидит.
            file.Remove(RouteKeys.MatchOf(RouteKeys.Own, value), value);
            file.Save();
            OwnLists.Delete(value);

            // Перечитываем целиком: строка ушла из общего списка,
            // а не только из карточки.
            Reload();
            var name = OwnLists.IsOwn(value) ? OwnLists.DomainOf(value) : value.TrimStart('*', '.');

            Status.Text = $"Убрано: {name} вместе с его списком. Применится при следующем запуске движков.";
            this.Offer($"Убран маршрут: {name}");
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
    /// <summary>
    /// Список закрыли, ничего не поменяв.
    /// </summary>
    /// <remarks>
    /// Выбрав «десинк» при уже выбранном «десинке», человек хочет одного —
    /// сменить рецепт. Но список не поднимает события: значение то же,
    /// менять нечего. Приходилось переключать на «напрямую» и обратно,
    /// то есть дважды переписывать правило ради окна выбора.
    ///
    /// Закрытие списка событие даёт всегда, и по нему видно, что человек
    /// в него лазил. Если выбран десинк и он же был выбран до этого —
    /// открываем окно рецептов, больше он там ничего и не мог хотеть.
    /// </remarks>
    private void OnRouteClosed(object sender, EventArgs e)
    {
        if (_filling || sender is not ComboBox box)
            return;

        if (box.DataContext is not PartRow row)
            return;

        // Ключ у строки, а не в Tag — по той же причине, что и в OnRoute.
        var key = row.Key;

        // Только десинк и только без смены: смену обработает OnRoute,
        // и она же спросит рецепт по дороге.
        if (box.SelectedIndex != 1 || row.Applied != 1)
            return;

        // У правил по адресам рецепт не спрашивается: он применяется по имени
        // в приветствии TLS, а в таких пакетах имени нет вовсе.
        if (row.Probe is not { Length: > 0 } example)
            return;

        // Свой домен наравне со списком: рецепт применяется по имени
        // в приветствии TLS, и своему имени он нужен ровно так же.
        // У правил по адресам не спрашиваем — имени в таких пакетах нет.
        if (RouteKeys.Parse(key) is (var kind, var value) && RouteKeys.TakesRecipe(kind))
            AskLater(key, value, example, RouteKeys.MatchOf(kind, value), RoutingMode.Desync);
    }

    private void OnRoute(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || sender is not ComboBox box)
            return;

        // Строка обязана быть на месте, и это не придирка к типу. Список
        // виртуализирован и переиспользует карточки: контейнер получает
        // то строку, то заглушку BindingOperations.DisconnectedSource,
        // и на заглушке SelectedIndex съезжает в -1, поднимая событие
        // выбора. Прежде проверка была написана так, что строка без типа
        // проваливалась дальше: сверять с Applied было не с чем, ключ брался
        // из Tag от прошлого жильца карточки, а режим доставался из ветки
        // по умолчанию — «через VPN». Прокрутка списка переписывала бы
        // правила молча.
        if (box.DataContext is not PartRow current)
            return;

        // Пустой выбор выбором не является: значение -1 ставит сам WPF,
        // отцепляя привязку, и ни один из трёх режимов ему не соответствует.
        if (box.SelectedIndex < 0)
            return;

        // Выбор того же самого — не выбор. Список получает SelectedIndex
        // при каждой пересборке строк, и без этой проверки открытие папки
        // писало правило и звало уведомление о перезапуске по разу
        // на каждую часть внутри.
        //
        // Сверяется Applied, а не Choice: в Choice привязка уже положила
        // новое значение, и сравнение с ним всегда говорило «то же самое».
        if (box.SelectedIndex == current.Applied)
            return;

        // Ключ берётся у строки, а не из Tag. Обе привязки обновляются
        // при смене строки, но порядок между ними не оговорён, и Tag может
        // ещё нести ключ прошлого жильца карточки. У строки он всегда свой.
        var key = current.Key;
        var parts = key.Split('|', 2);

        if (parts.Length != 2)
            return;

        var mode = box.SelectedIndex switch
        {
            0 => RoutingMode.Direct,
            1 => RoutingMode.Desync,
            _ => RoutingMode.Proxy,
        };

        var match = RouteKeys.MatchOf(parts[0], parts[1]);

        // Рецепт спрашиваем и у сервисов, а не только у своих доменов.
        // Именно здесь он и нужен чаще: сервис — это список из десятков имён,
        // и когда пресет их не открывает, руками разбираться не в чем.
        //
        // У списков адресов не спрашиваем: рецепт применяется по имени
        // в приветствии TLS, а в правиле по адресу имени нет вовсе.
        // Проверять надо на настоящем имени из списка, а не на названии
        // сервиса: «discord» не разрешается, и на нём любой рецепт отвечает
        // «не помогает».
        var example = current.Probe;

        if (mode == RoutingMode.Desync && match is MatchKind.HostList or MatchKind.Domain
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

            // VPN выбран, а имена прибиты — пин перебьёт маршрут (PinConflict).
            if (mode == RoutingMode.Proxy
                && PinConflict.Offer(Window.GetWindow(this), ZonesOf(match, parts[1])) is { } pinNote)
            {
                Status.Text += "  " + pinNote;
            }

            this.Offer("Маршрут изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Имена, которые покрывает правило: список раскрывается, домен — как есть.</summary>
    /// <remarks>У правил по адресам имён нет — и пинов под ними быть не может.</remarks>
    private IReadOnlyList<string> ZonesOf(MatchKind match, string value) => match switch
    {
        MatchKind.HostList => HostListReader.Read(value, _zapretRoot, out _),
        MatchKind.Domain => [value],
        _ => [],
    };

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
    /// <summary>Приёмы набора без настроек — то же, что показывает каталог.</summary>
    private static string Technique(IReadOnlyList<string> steps) =>
        string.Join(" + ", steps
            .Select(s => s.IndexOf(':') is var at && at > 0 ? s[..at] : s)
            .Distinct(StringComparer.Ordinal));

    /// <summary>
    /// Приёмы, которые умеет движок; пусто, если прочитать не вышло.
    /// </summary>
    /// <remarks>
    /// Читается раз на перезагрузку раздела, а не на каждую из восьмидесяти
    /// частей: модули лежат в пятнадцати файлах, и перечитывать их столько
    /// раз значило бы открывать раздел секундами — на этом уже обжигались
    /// со списками пресета.
    /// </remarks>
    private IReadOnlyDictionary<string, string>? _providers;

    private string Describe(ServiceRouting.PartStatus part)
    {
        if (part.Mode != RoutingMode.Desync)
            return part.DescribeMode();

        // Свой выбор важнее пресетовского: он и стоит первым в командной
        // строке. Хранится имя секции, показывается название приёма.
        if (RecipeFor(part.Part.List) is { } stored && !string.IsNullOrWhiteSpace(stored))
        {
            if (_preset is null)
                return $"десинк: {stored} — пресет не выбран";

            // По обоим источникам. Пока спрашивали один пресет, рецепт,
            // выбранный из каталога, объявлялся отсутствующим — сразу
            // после того, как человек его выбрал.
            var found = RecipeResolver.Find(_preset, stored, _providers);

            if (!found.Found)
                return $"десинк: {stored} — не найден ни в пресете, ни в каталоге";

            // Нехватка модуля названа отдельно и не сглажена: с таким
            // рецептом winws2 не поднимется вовсе, и без десинка останется
            // не одно это имя, а все.
            if (found.MissingModules.Count > 0)
            {
                return $"десинк: {stored} — нужен модуль "
                    + string.Join(", ", found.MissingModules);
            }

            return "десинк: " + Technique(found.Steps);
        }

        if (_preset is null)
            return "десинк: пресет не выбран";

        // Правила по адресам рецепта не получают: десинк отбирает трафик
        // по имени в приветствии TLS, а в таких пакетах имени нет.
        if (part.Part.ByAddress)
            return "десинк";

        // Через готовый указатель, а не перечитывая списки пресета заново.
        // Прежде этот вопрос задавался на каждую из восьмидесяти частей,
        // и каждый раз читался весь пресет: в russia-blacklist.txt сто
        // семнадцать тысяч имён, и он оказывался прочитан восемьдесят раз.
        // Раздел от этого открывался секундами.
        var section = _zones?.FirstFor(part.Domains);

        // Здесь 23.09 стояло «по факту напрямую: пресет не чинит / пропускает»,
        // серым, как «напрямую». Это была моя ошибка, и дорогая. Смотрелись
        // только секции пресета по именам, а секции по адресам (ipset)
        // и по классификатору продолжают работать с трафиком такой части
        // по IP. По этой подписи 30 частей перевели на «напрямую» — а он
        // со щитом выключает и эти секции. EA перестал подключаться
        // к серверам, Riot вёл себя через раз (владелец, 24.09: «мы очень
        // много вещей сломали, хоть и может казаться, что они работают»).
        // «Пропускает» — тем более не «напрямую»: это щит секции pass,
        // он держит имя от секций по адресам.
        if (section is null)
            return "десинк: по имени секции нет, по адресам — может сработать";

        return section.IsPassThrough
            ? "десинк: пресет пропускает — щит"
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
