using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Programs;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Часть сервиса и её маршрут.</summary>
/// <remarks>
/// Извещает об изменениях (13.10): карточки «Маршрутов» с тех пор не
/// виртуализированы, и подгруженный значок или пин обновляют строку на месте —
/// прежде каждый значок пересобирал весь список, а это теперь секунда.
/// </remarks>
public sealed record PartRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }

    /// <summary>Имя из списка: по нему берётся значок и ищет поиск.</summary>
    public string? Example { get; init; }

    /// <summary>Полный путь к файлу списка; <c>null</c> — ссылки «список» нет.</summary>
    public string? ListPath { get; init; }

    /// <remarks>
    /// У своего домена ссылка есть и без файла: нажатие предлагает его
    /// завести. Сам файл не заводится (владелец, 26.09) — см. OwnLists.
    /// </remarks>
    public Visibility ListShown => ListPath is not null || Own ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>
    /// Куда идёт часть, одним словом — для шапки папки.
    /// </summary>
    /// <remarks>
    /// Без приёма: «десинк», а не «десинк: hostfakesplit_multi». Приём
    /// назван в строке самой части. В шапке он делил одну дорогу на столько
    /// групп, сколько у сервиса разных приёмов, и итог Discord не влезал
    /// в строку (владелец, 01.10: «уже по конкретным маршрутам пиши»).
    /// По маршруту на момент сборки, как и <see cref="Mode"/>: шапка
    /// пересобирается вместе с разделом, а не по выбору в списке.
    /// </remarks>
    public string Route => Applied switch
    {
        0 => HasPin ? "прибит в hosts" : "напрямую",
        1 => "десинк",
        Off => "выключено",
        _ => "VPN",
    };

    /// <summary>Номер пункта «выключено» в списке маршрутов (<see cref="RoutingRule.Off"/>).</summary>
    public const int Off = 3;

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
    public bool HasPin
    {
        get => _hasPin;
        set
        {
            _hasPin = value;
            Changed();
        }
    }

    private bool _hasPin;

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

    public BitmapImage? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Changed();
        }
    }

    private BitmapImage? _icon;

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

    /// <summary>Папка по каталогу; <c>null</c> — маршрут сам по себе.</summary>
    /// <remarks>
    /// Человек может переложить маршрут в другую папку или вынуть его
    /// (RouteFolders, владелец 13.10), и «Вернуть на место» возвращает сюда.
    /// </remarks>
    public string? HomeFolder { get; init; }

    /// <summary>Папка, в которой маршрут показан сейчас; <c>null</c> — вне папки.</summary>
    public string? Folder { get; init; }

    /// <summary>Можно ли перекладывать: маршруты программы живут в её папке.</summary>
    public bool Movable { get; init; } = true;

    public Visibility MoreShown => Movable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Выбранный путь словом — на кнопке выбора (макет владельца 13.10).</summary>
    public string RouteLabel => Choice switch
    {
        0 => "напрямую",
        1 => "десинк",
        Off => "выключено",
        _ => "через VPN",
    };

    /// <summary>
    /// Место пустой папки в порядке, а не маршрут: у пустой, заведённой
    /// человеком папки маршрутов нет, а место в списке и перетаскивание нужны.
    /// </summary>
    public bool Placeholder { get; init; }

    /// <summary>Ручка перетаскивания — у того, что можно тянуть.</summary>
    public Visibility GripShown => Movable ? Visibility.Visible : Visibility.Hidden;
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
public sealed record ServiceRow(string Name, IReadOnlyList<PartRow> Parts) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Перечитать все привязки карточки: раскрытие, значок первой части.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public bool Open
    {
        get => _open;
        set
        {
            if (_open == value)
                return;

            _open = value;
            Refresh();
        }
    }

    private bool _open;

    private IReadOnlyList<PartRow>? _narrowed;

    /// <summary>
    /// Маршруты на экране: при поиске и отборе — найденные, иначе все.
    /// </summary>
    /// <remarks>
    /// Поиск сужает ту же карточку, а не подменяет её копией (13.10): копии
    /// при сбросе поиска пересоздавали все сто карточек — 1,15 с на замере.
    /// </remarks>
    public IReadOnlyList<PartRow> ShownParts => _narrowed ?? Parts;

    private bool _forced;
    private bool _hidden;

    /// <summary>Сузить до найденного; <c>null</c> — показать всё. Найденное раскрыто.</summary>
    /// <remarks>
    /// Ничего не найдено — карточка скрывается, но строки свои держит; найдено
    /// всё — список строк прежний. Подменяется он, только когда найдена часть:
    /// иначе при сбросе поиска строились бы заново строки всех карточек
    /// (0,7 с на замере 13.10).
    /// </remarks>
    public void Narrow(IReadOnlyList<PartRow>? found)
    {
        bool hidden = found is { Count: 0 };
        bool forced = found is { Count: > 0 };
        var narrowed = found is null || hidden || found.Count == Parts.Count ? null : found;

        if (narrowed is not null && _narrowed is not null && narrowed.SequenceEqual(_narrowed))
            narrowed = _narrowed;

        if (hidden == _hidden && forced == _forced && ReferenceEquals(narrowed, _narrowed))
            return;

        _hidden = hidden;
        _forced = forced;
        _narrowed = narrowed;
        Refresh();
    }

    /// <summary>Карточка скрыта: поиск в ней ничего не нашёл.</summary>
    public Visibility CardShown => _hidden ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Сколько маршрутов найдено поиском и отбором (у скрытой — ноль).</summary>
    public int Found => _hidden ? 0 : ShownParts.Count;

    private bool Expanded => Open || _forced;

    /// <summary>
    /// Всё, что видно на карточке, одной строкой — чтобы при пересборке оставить
    /// прежнюю карточку, если в ней ничего не поменялось.
    /// </summary>
    /// <remarks>
    /// Значок не входит: его подгружают позже, и у оставленной карточки он уже есть.
    /// </remarks>
    internal string Signature() => string.Join('',
        new[] { Name, Label ?? string.Empty, IsFolder.ToString(), Program ?? string.Empty, AutoFill.ToString(), Made.ToString(), string.Join(',', Keys) }
            .Concat(Parts.Select(p => string.Join('',
                p.Key, p.Title, p.Detail, p.Mode, p.Choice, p.Applied, p.HasPin, p.CanRoute, p.CanPin, p.Movable, p.Own,
                p.ListPath ?? string.Empty, p.Folder ?? string.Empty, p.HomeFolder ?? string.Empty, p.Probe ?? string.Empty,
                (p.Color as SolidColorBrush)?.Color.ToString() ?? string.Empty, p.Letter))));

    /// <summary>
    /// Папка, даже если маршрут в ней остался один.
    /// </summary>
    /// <remarks>
    /// Из YouTube вынули три маршрута из четырёх — оставшийся всё равно
    /// лежит в папке YouTube, и без шапки его имя («Интерфейс») ни о чём
    /// бы не говорило.
    /// </remarks>
    public bool IsFolder { get; init; }

    /// <summary>Папка программы (ProgramRoutes, 13.10); <c>null</c> — не программа.</summary>
    public string? Program { get; init; }

    /// <summary>Пополняется ли маршрут программы по наблюдению.</summary>
    public bool AutoFill { get; init; }

    public Visibility ProgramShown => Program is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Имя папки на экране; переименованная хранит прежнее в <see cref="Name"/>.</summary>
    public string? Label { get; init; }

    public string Shown => Label ?? Name;

    /// <summary>
    /// Все ключи папки, и место пустой тоже, — от них считается перетаскивание.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Parts"/>: поиск оставляет в папке только
    /// найденные маршруты, а переносить надо её целиком.
    /// </remarks>
    public IReadOnlyList<string> Keys { get; init; } = [];

    /// <summary>Заведённая человеком папка: её можно переименовать и удалить, пустая не исчезает.</summary>
    public bool Made { get; init; }

    /// <summary>Пустая папка: в ней пока нет маршрутов.</summary>
    public Visibility EmptyShown => IsFolder && Open && !_forced && Parts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>«+ Добавить маршрут в эту папку» — у раскрытой папки, кроме программ.</summary>
    public Visibility AddShown => IsFolder && Open && !_forced && Program is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Под именем папки: сколько маршрутов и куда идут.</summary>
    public string Subtitle => Parts.Count == 0 ? "пусто" : $"{Count} · {Summary}";

    public string AutoFillHint =>
        "Автопополнение: пока идёт «Наблюдение», всё, что открывает программа, дописывается — "
        + "имена в «Домены», сети /24 в «Адреса». Маршруты сами не включаются: путь выбираете вы.";

    /// <summary>
    /// Разворачивать нечего: часть одна, и она же вся строка.
    /// </summary>
    /// <remarks>
    /// Папка вокруг единственной части обещает содержимое, которого нет:
    /// раскрыл — а внутри то же самое имя, только названное «Всё». Такие
    /// показываются одной строкой без шапки и без стрелки, а имя сервиса
    /// переходит в саму строку.
    /// </remarks>
    public bool Single => !IsFolder && Parts.Count == 1;

    /// <summary>Шапка — только у того, что вправду сворачивается.</summary>
    public Visibility HeaderShown => Single ? Visibility.Collapsed : Visibility.Visible;

    public Visibility PartsShown =>
        (Single || Expanded) && ShownParts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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

    public Visibility IconShown => Icon is null || Made ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null && !Made ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Значок папки — у заведённой человеком: своего сайта у неё нет.</summary>
    public Visibility FolderGlyphShown => Made ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Поворот значка раскрытия.
    /// </summary>
    /// <remarks>
    /// Угол, а не знак: стрелка рисуется фигурой — см. <see cref="Chevrons"/>.
    /// </remarks>
    public double ChevronAngle => Chevrons.Angle(Expanded);

    public string Count => Parts.Count + " " + Ending(Parts.Count);

    /// <summary>Куда идут части: одним словом, если все одинаково.</summary>
    /// <remarks>Без приёмов десинка — см. <see cref="PartRow.Route"/>.</remarks>
    public string Summary
    {
        get
        {
            var modes = Parts
                .GroupBy(p => p.Route)
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
        Parts.Select(p => p.Route).Distinct().Count() == 1 && Parts.Count > 0
            ? Parts[0].Color
            : (Brush)Application.Current.FindResource("Muted");

    private static string Ending(int count)
    {
        int tail = count % 100;

        if (tail is >= 11 and <= 14)
            return "маршрутов";

        return (count % 10) switch
        {
            1 => "маршрут",
            2 or 3 or 4 => "маршрута",
            _ => "маршрутов",
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

            var engine = RuleSetLoader.LoadFor(settings);

            var problems = RuleSetExpander.Expand(engine.RuleSet, zapretRoot);

            // Маршруты по порядку, с папкой каталога: перекладка человека
            // (RouteFolders, 13.10) раскладывает их поверх.
            var placed = new List<(string? Folder, string Key, PartRow Part)>();

            // Заведённые папки и программы — первыми по умолчанию: их заводит
            // сам человек, и новую папку надо видеть сразу.
            placed.AddRange(settings.MadeFolders.Select(name => ((string?)name, FolderPlace(name), new PartRow
            {
                Key = FolderPlace(name),
                Title = name,
                Detail = string.Empty,
                Mode = string.Empty,
                Color = Brushes.Transparent,
                Choice = 0,
                Applied = 0,
                CanRoute = false,
                CanPin = false,
                Letter = string.Empty,
                Movable = false,
                Placeholder = true,
                HomeFolder = name,
                Folder = name,
            })));

            placed.AddRange(ProgramParts(settings, userRules));

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
                    placed.AddRange(parts.Select(p => ((string?)null, p.Key, p)));
                    continue;
                }

                // Сервис из одной части папкой не становится: разворачивать
                // нечего, а стрелка обещает содержимое, которого нет. Имя
                // сервиса переходит в саму строку — часть у таких зовётся
                // «Всё», и это ничего не говорит в общем списке.
                if (parts.Count == 1)
                {
                    placed.Add((null, parts[0].Key, parts[0] with { Title = service.Name }));
                    continue;
                }

                placed.AddRange(parts.Select(p => ((string?)service.Name, p.Key, p with { HomeFolder = service.Name })));
            }

            // Свои домены — в общий список, наравне с каталожными.
            // Решение владельца 20.09: им так же нужны значок, пин и рецепт,
            // а отдельная карточка ничего этого не давала и вдобавок делила
            // список надвое по признаку, который человеку безразличен —
            // по тому, знали мы это имя заранее или нет.
            placed.AddRange(OwnRows(userRules).Select(p => ((string?)null, p.Key, p)));

            // Порядок человека (перетаскивание) — поверх порядка каталога.
            var ordered = RouteFolders.Order(placed, settings.RouteOrder);
            _keyOrder = ordered.Select(item => item.Key).ToList();

            var services = Arranged(ordered, settings);

            MarkPins(services, zapretRoot);

            // Раскрытые папки переживают перерисовку. Прежде любое действие
            // внутри папки — пин, свой маршрут, обновление списка — собирало
            // строки заново, и всё захлопывалось: человек выбирал маршрут
            // одной части, а искать следующую приходилось с начала.
            foreach (var service in services)
                service.Open = _open.Contains(service.Name);

            // Неизменившиеся карточки — прежние: Show тогда их не пересоздаёт.
            services = Reuse(services);
            _all = services;

            // Отбор переживает пересборку. Любое действие внутри строки —
            // смена маршрута, пин, выбор рецепта — собирает список заново,
            // и без этого он возвращался целиком: найденное пропадало,
            // и приходилось дописывать пробел в поле, чтобы отбор случился
            // ещё раз.
            // Показывает Filter — одним разом: второй показ подряд достраивал
            // бы порционный первый целиком (Show).
            Filter();

            ShowOwn();

            Status.Text = problems.Count == 0
                // Про то, куда пишется выбор, теперь говорит подсказка
                // у заголовка: строка эта стояла над списком постоянно,
                // а нужна была один раз. Счёт сервисов остался — он
                // меняется и отвечает на вопрос «всё ли прочиталось».
                ? $"Папок и отдельных маршрутов: {services.Count}, маршрутов всего: {services.Sum(s => s.Parts.Count)}."
                : $"Часть списков не нашлась, и эти правила не действуют: {string.Join("; ", problems.Take(3))}";

            ShowOrder(engine);

            ShowShare();
            StartIcons(services);

            _engine = engine;

            // Пришли сюда щелчком по имени в проверке блокировок — показать его часть.
            if (PendingReveal is { } wanted)
            {
                PendingReveal = null;
                Reveal(wanted);
            }
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

        var (color, choice) = part.Off ? ("Muted", PartRow.Off) : part.Mode switch
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
                        service.Refresh();
                    });
                }

                if (token.IsCancellationRequested)
                    return;

                Dispatcher.Invoke(() =>
                {
                    if (LendServiceIcon(service))
                        service.Refresh();
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
    /// Ключ начинается с <c>own</c>. Значение — само правило
    /// <c>*.example.com</c>, либо путь своего списка, если человек его завёл
    /// ссылкой «список» (<see cref="OwnLists"/>, 26.09). По ключу
    /// обработчики узнают, что строка своя: её можно убрать, и убирается
    /// она вместе с файлом.
    /// </para>
    /// </remarks>
    private IReadOnlyList<PartRow> OwnRows(UserRulesFile userRules)
    {
        var rows = new List<PartRow>();

        // Списки программ — тоже свои, но показываются в папке программы (ProgramRows).
        foreach (var entry in userRules.Entries.Where(e =>
            e.Match == MatchKind.Domain
            || (e.Match == MatchKind.HostList && OwnLists.IsOwn(e.Value) && !ProgramRoutes.IsProgramList(e.Value))))
        {
            var listed = entry.Match == MatchKind.HostList;
            var zones = RouteKeys.Zones("own|" + entry.Value, _zapretRoot);
            var count = zones.Count;

            // У списка заголовок — его название, а имя для значка и проб —
            // первое из файла: название человек выбирал сам, и сайтом оно
            // быть не обязано.
            var title = listed ? OwnLists.NameOf(entry.Value) : entry.Value.TrimStart('*', '.');
            var domain = listed ? zones.FirstOrDefault() ?? title : title;
            var addresses = listed ? OwnLists.Names(entry.Value).Count(n => AddressInput.Normalize(n) is not null) : 0;

            var (color, choice) = entry.Off ? ("Muted", PartRow.Off) : entry.Mode switch
            {
                RoutingMode.Direct => ("Muted", 0),
                RoutingMode.Desync => ("Warn", 1),
                _ => ("Accent", 2),
            };

            var part = new PartRow
            {
                Icon = SiteIcons.Cached(domain),
                Key = "own|" + entry.Value,
                Title = title,
                Probe = domain,

                // Рецепт называется прямо в подписи: выбранный однажды,
                // он иначе пропадал бы из виду, и понять, чем чинится имя,
                // можно было бы только заглянув в yaml.
                Detail = (listed
                        ? "свой список · " + Count(count, "домен", "домена", "доменов")
                            + (addresses > 0 ? " · " + Count(addresses, "адрес", "адреса", "адресов") : string.Empty)
                        : "свой домен")
                    + (entry.Mode == RoutingMode.Desync
                        && !string.IsNullOrWhiteSpace(entry.Recipe)
                            ? $" · рецепт: {entry.Recipe}"
                            : string.Empty),

                Example = domain,

                // У домена без файла ссылка тоже есть — она предлагает файл
                // завести (OnOpenList); у списка — открывает его.
                ListPath = listed ? ListFile(entry.Value) : null,

                Mode = entry.Off ? "выключено — правило не действует" : Describe(entry.Mode),
                Color = (Brush)Application.Current.FindResource(color),
                Choice = choice,
                Applied = choice,
                CanRoute = true,
                CanPin = true,
                Own = true,
                Letter = domain.Length > 0 ? domain[..1].ToUpperInvariant() : "·",
                Lead = 24,
            };

            rows.Add(part);
        }

        // Свои программы — правило по имени процесса (обсуждение №10, 29.09).
        // Пина у программы нет: у неё нет имени сайта, только процесс.
        foreach (var entry in userRules.Entries.Where(e => e.Match == MatchKind.Process))
        {
            var (color, choice) = entry.Off ? ("Muted", PartRow.Off) : entry.Mode switch
            {
                RoutingMode.Direct => ("Muted", 0),
                RoutingMode.Desync => ("Warn", 1),
                _ => ("Accent", 2),
            };

            var part = new PartRow
            {
                Key = RouteKeys.Make(RouteKeys.Program, entry.Value),
                Title = entry.Value,
                Detail = entry.Mode == RoutingMode.Proxy
                    ? "программа · пока правило стоит, туннель забирает весь трафик машины: всё без маршрута идёт мимо VPN, но через движок туннеля. Сети Riot остаются мимо него"
                    : "программа",
                Mode = entry.Off ? "выключено — правило не действует" : Describe(entry.Mode),
                Color = (Brush)Application.Current.FindResource(color),
                Choice = choice,
                Applied = choice,
                CanRoute = true,
                CanPin = false,
                Own = true,
                Letter = entry.Value.Length > 0 ? entry.Value[..1].ToUpperInvariant() : "·",
                Icon = ProgramIcon(entry.Value),
                Lead = 24,
            };

            rows.Add(part);
        }

        // Свои подсети и адреса — правило ip (12.10). Пина и рецепта нет:
        // имени у них нет, рецепт десинка выбирается по имени в TLS.
        foreach (var entry in userRules.Entries.Where(e => e.Match == MatchKind.Ip))
        {
            var (color, choice) = entry.Off ? ("Muted", PartRow.Off) : entry.Mode switch
            {
                RoutingMode.Direct => ("Muted", 0),
                RoutingMode.Desync => ("Warn", 1),
                _ => ("Accent", 2),
            };

            var part = new PartRow
            {
                Key = RouteKeys.Make(RouteKeys.Subnet, entry.Value),
                Title = entry.Value,
                Detail = entry.Value.Contains('/') ? "своя подсеть" : "свой адрес",
                Example = entry.Value,
                Mode = entry.Off ? "выключено — правило не действует" : Describe(entry.Mode),
                Color = (Brush)Application.Current.FindResource(color),
                Choice = choice,
                Applied = choice,
                CanRoute = true,
                CanPin = false,
                Own = true,
                Letter = "#",
                Lead = 24,
            };

            rows.Add(part);
        }

        return rows;
    }

    /// <summary>
    /// Маршруты по папкам — с перекладкой человека (<see cref="RouteFolders"/>).
    /// </summary>
    /// <remarks>
    /// Вынутый из папки маршрут несёт её имя в заголовке: «Видеопоток» сам
    /// по себе ни о чём не говорит, «YouTube · Видеопоток» — говорит. То же
    /// у переложенного в чужую папку.
    /// </remarks>
    /// <remarks>
    /// Папка программы — только её «Домены» и «Адреса»: перекладка в неё
    /// и из неё не действует, иначе выключатель пополнения в шапке стоял бы
    /// над чужими маршрутами.
    /// </remarks>
    private static List<ServiceRow> Arranged(
        IReadOnlyList<(string? Folder, string Key, PartRow Part)> placed,
        AppSettings settings)
    {
        var programs = settings.ProgramRoutes.ToDictionary(p => p.Program, StringComparer.OrdinalIgnoreCase);
        var fixedKeys = placed.Where(p => !p.Part.Movable).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var moved = settings.RouteFolders
            .Where(m => !fixedKeys.Contains(m.Key) && !programs.ContainsKey(m.Value.Trim()))
            .ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);

        var rows = new List<ServiceRow>();
        var made = settings.MadeFolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? Label(string folder) => settings.FolderLabels.TryGetValue(folder, out var label) ? label : null;

        foreach (var (folder, all) in RouteFolders.Arrange(placed, moved))
        {
            var keys = all.Select(p => p.Key).ToList();
            var parts = all.Where(p => !p.Placeholder).ToList();

            if (folder is not null && programs.TryGetValue(folder, out var program))
            {
                rows.Add(new ServiceRow(folder, parts.Select(p => p with { Lead = 0 }).ToList())
                {
                    IsFolder = true,
                    Program = program.Program,
                    AutoFill = program.AutoFill,
                    Keys = keys,
                });

                continue;
            }

            string Title(PartRow part) =>
                part.HomeFolder is { } home && !string.Equals(home, folder, StringComparison.OrdinalIgnoreCase)
                    ? $"{Label(home) ?? home} · {part.Title}"
                    : part.Title;

            if (folder is null)
            {
                var single = parts[0] with { Title = Title(parts[0]), Folder = null, Lead = 0 };
                rows.Add(new ServiceRow(single.Title, [single]) { Keys = keys });
                continue;
            }

            rows.Add(new ServiceRow(folder, parts.Select(p => p with { Title = Title(p), Folder = folder, Lead = 0 }).ToList())
            {
                IsFolder = true,
                Keys = keys,
                Label = Label(folder),
                Made = made.Contains(folder),
            });
        }

        return rows;
    }

    /// <summary>
    /// Папки программ: «Домены» и «Адреса» (<see cref="ProgramRoutes"/>, 13.10).
    /// </summary>
    /// <summary>Ключ места пустой папки в порядке.</summary>
    private static string FolderPlace(string folder) => "folder|" + folder;

    private static IEnumerable<(string? Folder, string Key, PartRow Part)> ProgramParts(AppSettings settings, UserRulesFile userRules)
    {
        foreach (var program in settings.ProgramRoutes)
        {
            foreach (var domains in new[] { true, false })
            {
                var list = domains ? ProgramRoutes.DomainsPath(program.Program) : ProgramRoutes.AddressesPath(program.Program);
                var part = ProgramPart(program.Program, list, domains, userRules);

                yield return (program.Program, part.Key, part);
            }
        }
    }

    /// <remarks>
    /// Ключ — обычный <c>hostlist|путь</c>: выбор пути, «выключено» и рецепт
    /// работают тем же кодом, что у части сервиса. Рецепт спрашивается
    /// только у доменов — проба нужна по имени, у адресов его нет.
    /// </remarks>
    private static PartRow ProgramPart(string program, string list, bool domains, UserRulesFile userRules)
    {
        var entry = userRules.Entries.FirstOrDefault(e => e.Match == MatchKind.HostList && e.Matches(list));
        var lines = ProgramRoutes.Entries(list);
        var first = domains ? lines.FirstOrDefault() : null;

        var (color, choice) = entry is null || entry.Off ? ("Muted", PartRow.Off) : entry.Mode switch
        {
            RoutingMode.Direct => ("Muted", 0),
            RoutingMode.Desync => ("Warn", 1),
            _ => ("Accent", 2),
        };

        var detail = lines.Count == 0
            ? domains ? "пусто — сюда лягут имена, которые открывает программа" : "пусто — сюда лягут сети серверов без имени"
            : (domains ? Count(lines.Count, "домен", "домена", "доменов") : Count(lines.Count, "сеть", "сети", "сетей"))
                + ": " + string.Join(", ", lines.Take(3)) + (lines.Count > 3 ? " …" : string.Empty);

        return new PartRow
        {
            Key = RouteKeys.Make(RouteKeys.HostList, list),
            Title = domains ? "Домены" : "Адреса",
            Detail = detail
                + (entry is { Mode: RoutingMode.Desync, Off: false } && !string.IsNullOrWhiteSpace(entry.Recipe)
                    ? $" · рецепт: {entry.Recipe}"
                    : string.Empty),
            Probe = first,
            Example = first,
            ListPath = ListFile(list),
            Mode = entry is null || entry.Off ? "выключено — маршрут не действует" : Describe(entry.Mode),
            Color = (Brush)Application.Current.FindResource(color),
            Choice = choice,
            Applied = choice,
            CanRoute = true,
            CanPin = false,
            Movable = false,
            Letter = domains ? "@" : "#",
            Icon = ProgramIcon(program),
            HomeFolder = program,
            Folder = program,
        };
    }

    /// <summary>Карточки на экране — меняются по месту (<see cref="Show"/>).</summary>
    private readonly ObservableCollection<ServiceRow> _cards = [];

    /// <summary>
    /// Показывает карточки, трогая только изменившиеся.
    /// </summary>
    /// <remarks>
    /// Новый список вместо прежнего пересоздаёт все карточки — сотня их
    /// без виртуализации стоила 0,8–1,2 с (замер 13.10), и так после каждой
    /// смены пути. Здесь та же карточка остаётся на месте, сдвигается или
    /// уходит, и строится заново только новая.
    /// </remarks>
    private void Show(IReadOnlyList<ServiceRow> rows)
    {
        if (Services.ItemsSource != _cards)
            Services.ItemsSource = _cards;

        // Любой показ отменяет недостроенный первый: он сам доводит список до нужного.
        int generation = ++_fill;

        // Первый показ — порциями: сотня карточек разом держала окно ~0,65 с
        // сверх чтения правил (замер 13.10: повторный заход 1,0 с против
        // 0,35–0,42 у прежнего виртуального списка). Первые видны сразу,
        // остальные достраиваются в фоне, и окно между порциями живо.
        if (_cards.Count == 0 && rows.Count > FirstCards)
        {
            foreach (var row in rows.Take(FirstCards))
                _cards.Add(row);

            FillLater(rows, FirstCards, generation);
            return;
        }

        var wanted = rows.ToHashSet(ReferenceEqualityComparer.Instance);

        for (int i = _cards.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(_cards[i]))
                _cards.RemoveAt(i);
        }

        for (int i = 0; i < rows.Count; i++)
        {
            if (i < _cards.Count && ReferenceEquals(_cards[i], rows[i]))
                continue;

            int at = -1;

            for (int j = i + 1; j < _cards.Count; j++)
            {
                if (ReferenceEquals(_cards[j], rows[i]))
                {
                    at = j;
                    break;
                }
            }

            if (at >= 0)
                _cards.Move(at, i);
            else
                _cards.Insert(i, rows[i]);
        }
    }

    /// <summary>Сколько карточек строить сразу — хватает на экран и чуть ниже.</summary>
    private const int FirstCards = 24;

    /// <summary>Сколько достраивать за раз в фоне.</summary>
    private const int LaterCards = 16;

    /// <summary>Номер показа: недостроенный первый показ сверяется с ним и уступает новому.</summary>
    private int _fill;

    private void FillLater(IReadOnlyList<ServiceRow> rows, int from, int generation)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (generation != _fill)
                return;

            foreach (var row in rows.Skip(from).Take(LaterCards))
                _cards.Add(row);

            if (from + LaterCards < rows.Count)
                FillLater(rows, from + LaterCards, generation);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Прежние карточки вместо новых, если на них ничего не поменялось (<see cref="ServiceRow.Signature"/>).
    /// </summary>
    private List<ServiceRow> Reuse(List<ServiceRow> fresh)
    {
        var old = new Dictionary<string, ServiceRow>(StringComparer.Ordinal);

        foreach (var row in _all)
            old.TryAdd((row.IsFolder ? "f|" : "r|") + row.Name, row);

        var result = new List<ServiceRow>(fresh.Count);

        foreach (var row in fresh)
        {
            if (old.TryGetValue((row.IsFolder ? "f|" : "r|") + row.Name, out var was) && was.Signature() == row.Signature())
            {
                was.Open = row.Open;
                result.Add(was);
            }
            else
            {
                result.Add(row);
            }
        }

        return result;
    }

    /// <summary>Пути запущенных программ по имени файла — для значков строк.</summary>
    private static Dictionary<string, string>? _programPaths;

    private static DateTime _programPathsAt;

    /// <summary>
    /// Значок своей программы, как в Happ: из файла, если вписан путь, иначе —
    /// из запущенного процесса с тем же именем. Не запущена — буква.
    /// </summary>
    /// <remarks>
    /// Список процессов запоминается на полминуты: раздел пересобирается
    /// при каждой смене маршрута, и перебирать все процессы всякий раз —
    /// лишняя работа в потоке окна.
    /// </remarks>
    private static BitmapImage? ProgramIcon(string value)
    {
        if (value.Contains('\\'))
            return ProgramIcons.Of(value);

        if (_programPaths is null || DateTime.UtcNow - _programPathsAt > TimeSpan.FromSeconds(30))
        {
            _programPaths = RunningPrograms.List()
                .GroupBy(program => program.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Path, StringComparer.OrdinalIgnoreCase);
            _programPathsAt = DateTime.UtcNow;
        }

        return _programPaths.TryGetValue(value, out var path) ? ProgramIcons.Of(path) : null;
    }

    /// <summary>
    /// Имя программы из поля: «Fallout76.exe», «C:\Games\x.exe»; <c>null</c> — не программа.
    /// </summary>
    internal static string? ProgramInput(string text)
    {
        var value = text.Trim().Trim('"');

        if (!value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || value.Length <= 4)
            return null;

        // Путь оставляется путём: sing-box различает process_path и process_name.
        return value.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 ? null : value;
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

            Open(new PinWindow(value, RouteKeys.Zones(key, _zapretRoot)), key, OwnLists.NameOf(value));
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

            // Спрашиваем (владелец, 26.09). Прежде повторное нажатие снимало
            // молча — снятие казалось дешёвым, «вернуть как было». Но вернуть
            // не выходит: 26.09 шесть пинов Crunchyroll ушли одним нажатием,
            // а автоподбор следом не нашёл адреса ни одному из четырёх имён.
            // Снятое заново может и не подобраться.
            var sample = string.Join("\n", ours.Take(6).Select(n => "  " + n))
                + (ours.Count > 6 ? $"\n  … и ещё {ours.Count - 6}" : string.Empty);

            var answer = MessageBox.Show(
                $"Снять пины с «{label}» — {Count(ours.Count, "имя", "имени", "имён")}?\n\n{sample}\n\n"
                + "Адрес для них потом придётся подбирать заново, и найдётся он не всегда.",
                "NetZapret",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
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

    /// <summary>Все ключи маршрутов в показанном порядке — от него считает перетаскивание.</summary>
    private List<string> _keyOrder = [];

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
            // На месте, а не заменой списка: замена пересоздаёт все карточки,
            // а их сотня без виртуализации (13.10).
            foreach (var card in _cards)
                card.Refresh();
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

    /// <summary>«Настройки маршрутов» — отдельным окном, как у вкладки VPN.</summary>
    private void OnSettings(object sender, RoutedEventArgs e) =>
        new RoutesSettingsWindow { Owner = Window.GetWindow(this) }.ShowDialog();

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

    /// <summary>Отбор по маршруту: <c>null</c> — все, иначе <see cref="PartRow.Applied"/> (0 — напрямую, 1 — десинк, 2 — VPN).</summary>
    private int? _route;

    private void OnRouteFilter(object sender, RoutedEventArgs e)
    {
        _route = sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var route) ? route : null;
        _reveal = null;
        Filter();
    }

    /// <summary>
    /// Имя, чью часть показать при следующей сборке раздела, — заявка из проверки блокировок.
    /// </summary>
    /// <remarks>
    /// Заявкой, а не вызовом: раздел создаётся заново при переходе и собирает
    /// список после загрузки, а до того показывать нечего.
    /// </remarks>
    public static string? PendingReveal { get; set; }

    /// <summary>Движок правил последней сборки — им ищется часть для имени.</summary>
    private RuleEngine? _engine;

    /// <summary>Показ одной части: сервис и ключ части; <c>null</c> — список как обычно.</summary>
    private (string Service, string Key, string Host)? _reveal;

    /// <summary>
    /// Показывает часть, чьё правило решает это имя (владелец, 04.10: «имена
    /// в блокчеке кликабельными, ведущими на их маршрут»).
    /// </summary>
    /// <remarks>
    /// Тем же движком правил, что <c>nz where</c>: первое совпавшее правило —
    /// и та часть, что стоит на его списке, домене или сети. Не попало ни в
    /// одно — имя ставится в поиск: «Маршруты» сами предложат добавить его
    /// своим доменом.
    /// </remarks>
    private void Reveal(string host)
    {
        _route = null;
        FilterAll.IsChecked = true;

        if (_engine is not null
            && _engine.Evaluate(NetZapret.Core.Connections.ConnectionEvent.Describe(host)).Rule is { } rule)
        {
            foreach (var service in _all)
            {
                var part = service.Parts.FirstOrDefault(p =>
                    RouteKeys.Parse(p.Key) is (_, var value) && SameValue(value, rule.Value));

                if (part is null)
                    continue;

                _reveal = (service.Name, part.Key, host);
                Search.Text = string.Empty;
                Filter();
                return;
            }
        }

        _reveal = null;
        Search.Text = host;
    }

    private static bool SameValue(string a, string b) =>
        string.Equals(a.Replace('\\', '/').Trim(), b.Replace('\\', '/').Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Числа на кнопках отбора — частей на каждом маршруте, по всему списку, без поиска.</summary>
    private void ShowRouteCounts()
    {
        var parts = _all.SelectMany(s => s.Parts).ToList();

        CountAll.Text = parts.Count.ToString();
        CountVpn.Text = parts.Count(p => p.Applied == 2).ToString();
        CountDesync.Text = parts.Count(p => p.Applied == 1).ToString();
        CountDirect.Text = parts.Count(p => p.Applied == 0).ToString();
    }

    /// <summary>Узко — отбор под полем поиска, широко — рядом с ним.</summary>
    private void OnSearchRowSize(object sender, SizeChangedEventArgs e)
    {
        // Порог — чтобы полю рядом с отбором и двумя кнопками осталось место
        // под подсказку хотя бы наполовину: отбор с кнопками — около семисот точек.
        bool below = e.NewSize.Width < 1060;

        Grid.SetRow(SearchTools, below ? 1 : 0);
        Grid.SetColumn(SearchTools, below ? 0 : 1);
        SearchTools.Margin = below ? new Thickness(0, 8, 0, 0) : new Thickness(10, 0, 0, 0);
    }

    /// <summary>«в 1 сервисе», «в 21 сервисе», иначе «сервисах».</summary>
    private static string InServices(int count) =>
        count % 10 == 1 && count % 100 != 11 ? "сервисе" : "сервисах";

    private static string RouteWords(int route) => route switch
    {
        0 => "напрямую",
        1 => "на десинке",
        _ => "через VPN",
    };

    /// <summary>Показывает то, что подходит под поиск и под отбор по маршруту.</summary>
    private void Filter()
    {
        var needle = Search.Text.Trim();

        ShowRouteCounts();

        // Карточки всегда одни и те же — поиск и отбор только сужают их
        // (ServiceRow.Narrow): подмена копиями пересоздавала весь список.
        Show(InChosenOrder(_all));

        // Показ одной части — пока его не сменили поиском или отбором.
        if (_reveal is { } reveal && needle.Length == 0)
        {
            foreach (var s in _all)
                s.Narrow(s.Name == reveal.Service ? s.Parts.Where(p => p.Key == reveal.Key).ToList() : []);

            var shown = _all.Where(s => s.Found > 0).ToList();

            if (shown.Count > 0)
            {
                AddOffer.Visibility = Visibility.Collapsed;

                var part = shown[0].ShownParts[0];
                Status.Text = $"«{reveal.Host}» решает «{reveal.Service} → {part.Title}»: {part.Mode}. "
                    + "Весь список — кнопкой «Все» или поиском.";
                return;
            }
        }

        _reveal = null;

        if (needle.Length == 0 && _route is null)
        {
            AddOffer.Visibility = Visibility.Collapsed;

            foreach (var s in _all)
                s.Narrow(null);

            return;
        }

        // Ищем и по названию сервиса, и по имени части, и по домену из примера:
        // человек помнит «где у меня ютуб» одним из трёх, и заставлять его
        // угадывать нужное — значит сделать поиск бесполезным.
        //
        // Найденное раскрывается само: искали часть, а не папку, и заставлять
        // открывать её вручную значит отдать половину найденного обратно.
        foreach (var s in _all)
        {
            s.Narrow(s.Parts
                .Where(p => needle.Length == 0 || Matches(s.Shown, p, needle) || Matches(s.Name, p, needle))
                .Where(p => _route is null || p.Applied == _route)
                .ToList());
        }

        var found = _all.Where(s => s.Found > 0).ToList();

        // Один отбор, без поиска: предлагать добавить нечего, сказать — сколько.
        if (needle.Length == 0)
        {
            AddOffer.Visibility = Visibility.Collapsed;
            Status.Text = found.Count > 0
                ? $"Маршрутов {RouteWords(_route!.Value)}: {found.Sum(s => s.Found)} — в {found.Count} {InServices(found.Count)}."
                : $"Ничего не идёт {RouteWords(_route!.Value)}.";
            return;
        }

        // Добавить предлагается, только когда искать больше нечего: вписанное
        // похоже на имя сайта, а в списке оно не нашлось ни частью, ни доменом.
        // Нашлось — значит, такое уже есть, и второе правило на него было бы
        // двойником, спорящим с первым.
        var program = found.Count == 0 ? ProgramInput(needle) : null;
        var subnet = found.Count == 0 && program is null ? AddressInput.Normalize(needle) : null;
        var domain = found.Count == 0 && program is null && subnet is null ? DomainInput.Normalize(needle) : null;

        // Программа с 13.10 — папка из доменов и адресов (ProgramRoutes), а не
        // правило по процессу, снятое 04.10: его и предлагаем.
        AddOffer.Visibility = domain is null && subnet is null && program is null ? Visibility.Collapsed : Visibility.Visible;

        if (program is not null)
            AddOfferText.Text = $"«{program}» — программа. Добавить её папку: домены и адреса, пустые и выключенные, пополняются по наблюдению.";

        if (domain is not null)
            AddOfferText.Text = $"«{domain}» в списке нет. Добавить своим доменом — сперва «напрямую», дальше правится в его строке.";

        if (subnet is not null)
            AddOfferText.Text = $"«{subnet}» — {(subnet.Contains('/') ? "подсеть" : "адрес")}. Добавить своим правилом — сперва «напрямую», дальше правится в его строке.";

        Status.Text = found.Count > 0
            ? $"Найдено маршрутов: {found.Sum(s => s.Found)}."
            : domain is null && subnet is null && program is null
                    ? $"По «{needle}» ничего нет. Чтобы добавить свой домен, впишите имя сайта, например example.com, или подсеть — 203.0.113.0/24."
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

    /// <summary>
    /// Предлагает своему домену завести файл списка.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Только по нажатию, не сам (владелец, 26.09): одно имя в двух файлах —
    /// два правила, спорящих о нём. Окно называет списки, где домен уже есть,
    /// до создания.
    /// </para>
    /// <para>
    /// Создав, переводим правило на список на его же месте — с режимом
    /// и рецептом, — и открываем файл: за ним и нажимали.
    /// </para>
    /// </remarks>
    private void OfferList(PartRow row)
    {
        if (RouteKeys.Parse(row.Key) is not (RouteKeys.Own, var value) || OwnLists.IsOwn(value))
            return;

        var domain = value.TrimStart('*', '.');

        var window = new ListNameWindow(domain, ListsCovering(domain))
        {
            Owner = Window.GetWindow(this),
        };

        if (window.ShowDialog() != true || window.Chosen is not { } name)
            return;

        try
        {
            var file = UserRulesFile.Load();
            int index = file.Entries.ToList().FindIndex(en => en.Match == MatchKind.Domain && en.Matches(value));

            if (index < 0)
            {
                Status.Text = $"Правила для {domain} уже нет — перечитываю раздел.";
                Reload();
                return;
            }

            var path = OwnLists.Create(name, [domain]);
            file.ReplaceAt(index, file.Entries[index] with { Match = MatchKind.HostList, Value = path });
            file.Save();

            Reload();

            if (ListFile(path) is { } full)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{full}\"")
                {
                    UseShellExecute = true,
                })?.Dispose();
            }

            Status.Text = $"Заведён список «{name}» с {domain}. Допишите имена и сохраните — "
                + "применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось завести список: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Списки, где имя уже есть: части сервисов и свои.
    /// </summary>
    /// <remarks>
    /// По зоне, как у пина: список хранит <c>example.com</c>, а спрашивают
    /// и про <c>www.example.com</c>. Совпадение — это будущий спор правил.
    /// </remarks>
    private IReadOnlyList<string> ListsCovering(string domain)
    {
        var found = new List<string>();

        try
        {
            foreach (var service in ServiceCatalog.All)
            {
                foreach (var part in service.Parts.Where(p => !p.ByAddress))
                {
                    var zones = RouteKeys.Zones(RouteKeys.Make(RouteKeys.HostList, part.List), _zapretRoot);

                    if (RouteKeys.Covers(zones, domain))
                        found.Add(service.Parts.Count > 1 ? $"{service.Name} · {part.Name}" : service.Name);
                }
            }

            foreach (var entry in UserRulesFile.Load().Entries.Where(en =>
                en.Match == MatchKind.HostList && OwnLists.IsOwn(en.Value)))
            {
                if (RouteKeys.Covers(RouteKeys.Zones("own|" + entry.Value, _zapretRoot), domain))
                    found.Add($"свой «{OwnLists.NameOf(entry.Value)}»");
            }
        }
        catch (Exception)
        {
            // Не прочиталось — предупреждения не будет, создать всё равно можно.
        }

        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

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
        if (sender is not System.Windows.Documents.Hyperlink { DataContext: PartRow row })
            return;

        if (row.ListPath is not { } path)
        {
            if (row.Own)
                OfferList(row);

            return;
        }

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
        // Свои программы — в том же счёте (с 01.10): они стоят в том же
        // списке ниже, и счёт без них говорил бы «своих правил нет» тому,
        // у кого заведена только игра.
        var own = UserRulesFile.Load().Entries
            .Where(entry => entry.Match is MatchKind.Domain or MatchKind.Process or MatchKind.Ip
                || (entry.Match == MatchKind.HostList && OwnLists.IsOwn(entry.Value) && !ProgramRoutes.IsProgramList(entry.Value)))
            .Select(entry => entry.Match switch
            {
                MatchKind.Domain => entry.Value.TrimStart('*', '.'),
                MatchKind.Process or MatchKind.Ip => entry.Value,
                _ => OwnLists.NameOf(entry.Value),
            })
            // Программа — одной строкой счёта, а не двумя списками.
            .Concat(AppSettings.Load(AppSettings.DefaultPath).ProgramRoutes.Select(p => p.Program))
            .ToList();

        OwnSummary.Text = own.Count == 0
            ? "Впишите сайт в поле ниже: если его нет в списке, появится «Добавить»."
            : $"Своих правил: {own.Count} — {string.Join(", ", own.Take(3))}"
              + (own.Count > 3 ? $" и ещё {own.Count - 3}" : string.Empty)
              + ". Они в списке ниже вместе с сервисами; строки и папки перетаскиваются мышью.";

        // Снятые правила по программе (Rules.ProgramRulesOff, 04.10) — словами,
        // а не молча: человек ставил игру «через VPN», и правило пропало.
        if (AppSettings.Load(AppSettings.DefaultPath).RemovedProgramRules is { Count: > 0 } removed)
            OwnSummary.Text += $" Прежние правила по программе сняты 04.10: {string.Join(", ", removed)}. Добавьте программу заново — теперь это папка из доменов и адресов.";
    }

    private void OnAddOwn(object sender, RoutedEventArgs e) => AddOwn();

    /// <summary>
    /// «Добавить правило» (макет владельца 13.10): свой сайт, адрес или подсеть —
    /// полем поиска; программа — из запущенных или файлом, как в Happ.
    /// </summary>
    private void OnAddProgram(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = AddProgramButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        menu.Items.Add(MenuEntry("", "Сайт, адрес или подсеть…", () =>
        {
            Search.Text = string.Empty;
            Search.Focus();
            Status.Text = "Впишите сайт (example.com), адрес или подсеть в поле поиска — если такого нет, появится «Добавить».";
        }));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(MenuEntry("", "Программа из запущенных…", PickRunningProgram));
        menu.Items.Add(MenuEntry("", "Программа файлом…", PickProgramFile));
        menu.IsOpen = true;
    }

    private static MenuItem MenuEntry(string glyph, string text, Action act)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

        var item = new MenuItem { Header = text, Icon = icon };
        item.Click += (_, _) => act();

        return item;
    }

    private void PickRunningProgram()
    {
        var window = new ProgramPickerWindow { Owner = Window.GetWindow(this) };

        if (window.ShowDialog() == true && window.Chosen is { } chosen)
            AddProgram(chosen.Name);
    }

    private void PickProgramFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Программа",
            Filter = "Программы|*.exe",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            AddProgram(System.IO.Path.GetFileName(dialog.FileName));
    }

    /// <summary>
    /// Заводит папку программы — «Домены» и «Адреса», пустые и выключенные (владелец 13.10).
    /// </summary>
    /// <remarks>
    /// <para>
    /// По имени файла, а не по пути, и из окна выбора, и из файла: путь
    /// в WindowsApps меняется с каждым обновлением (у Happ на снимке
    /// владельца восемь строк claude.exe), имя переживает обновление.
    /// </para>
    /// <para>
    /// Не правило по процессу, а два своих списка (<see cref="ProgramRoutes"/>):
    /// правило по процессу в «Гибриде» не уводило игру в VPN (снято 04.10).
    /// Наблюдение начинается сразу — без него пополнять нечем.
    /// </para>
    /// </remarks>
    private void AddProgram(string program)
    {
        try
        {
            if (!ProgramRoutes.Add(program, AppSettings.DefaultPath, UserRulesFile.DefaultPath))
            {
                Search.Text = program;
                Status.Text = $"«{program}» уже есть — её папка ниже.";
                return;
            }

            _open.Add(program);
            Search.Text = program;
            Reload();

            Status.Text = WatchView.StartSession()
                ? $"Добавлена «{program}»: домены и адреса пустые и выключены. Запустите программу — "
                    + "что она открывает, допишется само, пока идёт «Наблюдение». Путь выберите в строках ниже."
                : $"Добавлена «{program}», но наблюдение не началось, и пополнять нечем: откройте «Наблюдение».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Выключатель автопополнения в шапке папки программы.</summary>
    private void OnAutoFill(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string program } toggle)
            return;

        bool on = toggle.IsChecked == true;

        try
        {
            ProgramRoutes.SetAutoFill(program, on, AppSettings.DefaultPath);

            Status.Text = !on
                ? $"«{program}»: автопополнение выключено, списки остаются как есть."
                : WatchView.StartSession()
                    ? $"«{program}»: автопополнение включено — дописывается, пока идёт «Наблюдение»."
                    : $"«{program}»: автопополнение включено, но наблюдение не началось: откройте «Наблюдение».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>«Убрать» в шапке папки программы: маршруты, списки и запись — разом.</summary>
    private void OnRemoveProgram(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string program })
            RemoveProgram(program);
    }

    private void RemoveProgram(string program)
    {
        if (MessageBox.Show(Window.GetWindow(this),
                $"Убрать «{program}» вместе со списками доменов и адресов?",
                "NetZapret", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            ProgramRoutes.Remove(program, AppSettings.DefaultPath, UserRulesFile.DefaultPath);
            Reload();

            Status.Text = $"Убрана «{program}». Применится при следующем запуске движков.";
            this.Offer($"Убран маршрут: {program}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось убрать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// «⋯» у маршрута: переложить в папку, вынуть из неё, вернуть на место (владелец 13.10).
    /// </summary>
    /// <remarks>
    /// Перекладка — только вид (<see cref="RouteFolders"/>): правило, его
    /// место в файле и путь маршрута не меняются, перезапуск не нужен.
    /// </remarks>
    private void OnRouteMore(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } anchor
            || _all.SelectMany(s => s.Parts).FirstOrDefault(p => p.Key == key) is not { } part)
        {
            return;
        }

        // В существующую папку — перетаскиванием: подменю со списком папок
        // наше меню не раскрывает (13.10, «переложить в папку ничего не
        // делает»), а список из двадцати папок в меню и не нужен.
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        menu.Items.Add(MenuEntry("", "В новую папку…", () =>
        {
            var window = new FolderNameWindow($"Новая папка для «{part.Title}»", ProgramNames()) { Owner = Window.GetWindow(this) };

            if (window.ShowDialog() == true && window.Chosen is { } chosen)
                MoveRoute(part, chosen);
        }));

        if (part.Folder is not null)
            menu.Items.Add(MenuEntry("", "Вынуть из папки", () => MoveRoute(part, string.Empty)));

        if (!string.Equals(part.Folder ?? string.Empty, part.HomeFolder ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            menu.Items.Add(MenuEntry("", part.HomeFolder is { } home ? $"Вернуть в «{home}»" : "Вернуть на место",
                () => MoveRoute(part, part.HomeFolder ?? string.Empty)));
        }

        // «убрать» стоял кнопкой в строке; по макету 13.10 строка несёт только путь, пин и «⋯».
        if (part.Own)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
            menu.Items.Add(MenuEntry(string.Empty, "Убрать маршрут", () => RemoveOwn(part.Key)));
        }

        menu.IsOpen = true;
    }

    private void MoveRoute(PartRow part, string folder)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            (settings with { RouteFolders = RouteFolders.Move(settings.RouteFolders, part.Key, folder, part.HomeFolder) })
                .Save(AppSettings.DefaultPath);

            if (folder.Length > 0)
                _open.Add(folder.Trim());

            Reload();

            Status.Text = folder.Length == 0
                ? $"«{part.Title}» — вне папки. Путь маршрута не менялся."
                : $"«{part.Title}» — в папке «{folder.Trim()}». Путь маршрута не менялся.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переложить: " + ex.GetBaseException().Message;
        }
    }

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
        // Программа — папкой из доменов и адресов (ProgramRoutes, 13.10).
        if (ProgramRoutes.Normalize(ProgramInput(Search.Text)) is { } program)
        {
            AddProgram(program);
            return;
        }

        if (AddressInput.Normalize(Search.Text) is { } subnet)
        {
            AddSubnet(subnet);
            return;
        }

        var name = DomainInput.Normalize(Search.Text);

        if (name is null)
        {
            Status.Text = "Это не похоже на имя сайта. Нужно что-то вроде example.com.";
            return;
        }

        try
        {
            var file = UserRulesFile.Load();
            // Одной строкой, без файла: файл заводится только по просьбе —
            // ссылкой «список» в строке домена (владелец, 26.09).
            file.Set(MatchKind.Domain, "*." + name, RoutingMode.Direct, recipe: null);
            file.Save();
            PlaceAdded("own|*." + name);

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

    /// <summary>Своя подсеть или адрес — правилом ip, сперва «напрямую», как свой домен.</summary>
    private void AddSubnet(string subnet)
    {
        try
        {
            var file = UserRulesFile.Load();
            file.Set(MatchKind.Ip, subnet, RoutingMode.Direct, recipe: null);
            file.Save();
            PlaceAdded(RouteKeys.Make(RouteKeys.Subnet, subnet));

            Reload();

            Status.Text = $"Записано: {subnet} → напрямую. Режим меняется в его строке ниже. "
                + "Применится при следующем запуске движков.";

            this.Offer($"Добавлен маршрут: {subnet}");
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
    /// <param name="own">
    /// Свой домен: «решает пресет» предлагается, только если пресет это имя
    /// знает. Иначе «десинк» без рецепта оставил бы его без обхода вовсе.
    /// </param>
    private bool AskRecipe(string title, string domain, bool own, out string? recipe)
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

            var window = new RecipeWindow(title, domain, new PresetReader().Load(presetPath), presetOnlyIfKnown: own)
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
        if (sender is Button { Tag: string key })
            RemoveOwn(key);
    }

    private void RemoveOwn(string key)
    {
        if (RouteKeys.Parse(key) is not var (kind, value)
            || kind is not (RouteKeys.Own or RouteKeys.Program or RouteKeys.Subnet))
        {
            return;
        }

        try
        {
            var file = UserRulesFile.Load();
            // И правило, и файл: свой список без правила — мусор, который
            // никто не читает и никто не увидит.
            file.Remove(RouteKeys.MatchOf(kind, value), value);
            file.Save();

            if (kind == RouteKeys.Own)
                OwnLists.Delete(value);

            // Перечитываем целиком: строка ушла из общего списка,
            // а не только из карточки.
            Reload();
            var name = OwnLists.IsOwn(value) ? OwnLists.NameOf(value) : value.TrimStart('*', '.');

            Status.Text = kind == RouteKeys.Own
                ? $"Убрано: {name} вместе с его списком. Применится при следующем запуске движков."
                : $"Убрано: {name}. Применится при следующем запуске движков.";
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
    /// Кнопка пути в строке: меню из четырёх путей и, у десинка, «Рецепт…».
    /// </summary>
    /// <remarks>
    /// <para>
    /// Кнопкой, а не списком выбора (13.10): карточки «Маршрутов» стоят
    /// в две колонки без виртуализации (MasonryPanel), а список выбора —
    /// самый тяжёлый элемент строки, и сотня их разом тормозила раздел ещё
    /// до виртуализации.
    /// </para>
    /// <para>
    /// «Рецепт…» — отдельным пунктом. Прежде, чтобы сменить рецепт, выбирали
    /// «десинк» при уже выбранном «десинке» и ловили закрытие списка: сам
    /// список при выборе того же значения события не поднимает.
    /// </para>
    /// </remarks>
    private void OnRouteMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PartRow row } anchor || !row.CanRoute)
            return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        foreach (var (choice, text) in new[] { (0, "напрямую"), (1, "десинк"), (2, "через VPN"), (PartRow.Off, "выключено") })
        {
            // Отметка — значком: наше меню IsChecked не рисует.
            menu.Items.Add(MenuEntry(row.Applied == choice ? "" : string.Empty, text, () => Choose(row, choice)));
        }

        if (row.Applied == 1 && row.Probe is { Length: > 0 } example
            && RouteKeys.Parse(row.Key) is (var kind, var value) && RouteKeys.TakesRecipe(kind))
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
            menu.Items.Add(MenuEntry("", "Рецепт…", () =>
                AskLater(row.Key, value, example, RouteKeys.MatchOf(kind, value), RoutingMode.Desync)));
        }

        menu.IsOpen = true;
    }

    /// <summary>Записывает выбранный путь строки.</summary>
    /// <remarks>
    /// Применяется перезапуском, и об этом сказано прямо. Сами не
    /// перезапускаем: движки несут весь трафик машины, и ронять их в ответ
    /// на выбор в списке — не та цена, на которую человек соглашался.
    /// </remarks>
    private void Choose(PartRow current, int choice)
    {
        // Выбор того же самого — не выбор: правило не переписываем.
        if (choice == current.Applied)
            return;

        var key = current.Key;
        var parts = key.Split('|', 2);

        if (parts.Length != 2)
            return;

        var match = RouteKeys.MatchOf(parts[0], parts[1]);

        if (choice == PartRow.Off)
        {
            WriteOff(key, match);
            return;
        }

        var mode = choice switch
        {
            0 => RoutingMode.Direct,
            1 => RoutingMode.Desync,
            _ => RoutingMode.Proxy,
        };

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
                if (!AskRecipe(ServiceName(list), example, key.StartsWith(RouteKeys.Own + "|", StringComparison.Ordinal), out var recipe))
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

            // Часть внутри более широкого списка — раньше его правила (ServicePart.Within).
            var within = ServiceCatalog.All.SelectMany(s => s.Parts)
                .FirstOrDefault(p => string.Equals(p.List, parts[1], StringComparison.OrdinalIgnoreCase))?.Within;

            file.Set(match, parts[1], mode, recipe, before: within);
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

    /// <summary>
    /// Выключает часть: не действует ни своё правило, ни заводское (<see cref="RoutingRule.Off"/>).
    /// </summary>
    /// <remarks>
    /// Запись ставится туда же, куда и выбор режима, — раньше более широкого
    /// списка (<c>ServicePart.Within</c>): место в файле у неё ничего не решает,
    /// но и прыгать по файлу при переключении туда-обратно незачем.
    /// </remarks>
    private void WriteOff(string key, MatchKind match)
    {
        var parts = key.Split('|', 2);

        try
        {
            var file = UserRulesFile.Load();

            var within = ServiceCatalog.All.SelectMany(s => s.Parts)
                .FirstOrDefault(p => string.Equals(p.List, parts[1], StringComparison.OrdinalIgnoreCase))?.Within;

            file.SetOff(match, parts[1], before: within);
            file.Save();
            Reload();

            Status.Text = $"Выключено: {parts[1]}. Его имена теперь решают другие правила, "
                + "а нет таких — умолчание режима. Применится при следующем запуске движков.";

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
        // Режим выключенной части — чужой: тот, куда её имена уводит
        // следующее совпавшее правило или умолчание. Его и называем, иначе
        // «выключено» читалось бы как «никуда».
        if (part.Off)
            return $"выключено — решают другие правила: {Describe(part.Mode)}";

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
