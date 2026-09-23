using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Откуда взять адрес для пина.</summary>
public sealed record SourceRow(string Id, string Name, string Note);

/// <summary>Одна проверка кандидата в таблице автоподбора — и выбор для пина.</summary>
/// <remarks>
/// Адрес <c>null</c> — строка «не прибивать это имя»: подбор решает за
/// человека, но последнее слово за ним, и оставить имя как есть тоже выбор.
/// </remarks>
public sealed class CheckLine(string host, string? address, string source, string detail, string time, Brush brush, bool selectable)
    : INotifyPropertyChanged
{
    private bool _selected;

    public string Host { get; } = host;
    public string? Address { get; } = address;
    public string Shown => Address ?? "—";
    public string Source { get; } = source;
    public string Detail { get; set; } = detail;
    public string Time { get; } = time;
    public Brush Brush { get; } = brush;
    public bool Selectable { get; } = selectable;

    public bool IsSelected
    {
        get => _selected;
        set
        {
            _selected = value;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new(nameof(Mark)));
        }
    }

    public string Mark => _selected ? "✓" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Имя и проверки его кандидатов; заголовок меняется, когда подбор решил.</summary>
public sealed class CheckBlock : INotifyPropertyChanged
{
    private string _title;
    private Brush _titleBrush;

    public CheckBlock(string host, Brush brush)
    {
        Host = host;
        _title = host + " — проверяю…";
        _titleBrush = brush;
    }

    public string Host { get; }

    public ObservableCollection<CheckLine> Lines { get; } = [];

    public string Title
    {
        get => _title;
        set { _title = value; PropertyChanged?.Invoke(this, new(nameof(Title))); }
    }

    public Brush TitleBrush
    {
        get => _titleBrush;
        set { _titleBrush = value; PropertyChanged?.Invoke(this, new(nameof(TitleBrush))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Пин в hosts и маршрут сервиса — в одном окне.
/// </summary>
/// <remarks>
/// <para>
/// Пин и маршрут вместе, а не порознь, потому что порознь они не работают.
/// Пин задаёт адрес, но не дорогу: TUN вынюхивает имя из рукопожатия,
/// и доменное правило срабатывает поверх прибитого адреса. Пин при маршруте
/// «через VPN» — строка в файле, которая ни на что не влияет. Так и терялся
/// ChatGPT: адрес был жив, а правило уводило соединение в туннель, который
/// в тот час не работал.
/// </para>
/// </remarks>
public partial class PinWindow : Window
{
    /// <summary>
    /// Что прибиваем и куда пишем маршрут.
    /// </summary>
    /// <remarks>
    /// Заведено, когда в окно пришли свои домены. Прежде оно знало ровно
    /// одну цель — часть сервиса из каталога, — и всё в нём было написано
    /// через <c>ServicePart.List</c>: путь к файлу списка. У своего домена
    /// файла нет, зона у него одна и та, что человек вписал.
    ///
    /// Различие собрано здесь, а не россыпью проверок «если домен» по всему
    /// окну: таких мест вышло бы шесть, и каждое пришлось бы держать
    /// в согласии с остальными.
    /// </remarks>
    private sealed record PinTarget
    {
        /// <summary>Заголовок окна: «Discord · Голос» либо сам домен.</summary>
        public required string Title { get; init; }

        /// <summary>Короткое имя для записок в hosts и сообщений.</summary>
        public required string Short { get; init; }

        /// <summary>Откуда взят состав — строкой для подписи.</summary>
        public required string Source { get; init; }

        /// <summary>Имена, считающиеся принадлежащими цели.</summary>
        public required IReadOnlyList<string> Zones { get; init; }

        /// <summary>Каким правилом записывается маршрут.</summary>
        public required MatchKind Match { get; init; }

        /// <summary>Значение правила: путь к списку либо домен.</summary>
        public required string Value { get; init; }

        /// <summary>Задано подсетями — прибивать нечего.</summary>
        public required bool ByAddress { get; init; }

        /// <summary>Куда идёт сейчас; читается при показе, а не при создании.</summary>
        public required Func<string> Current { get; init; }
    }

    private readonly PinTarget _target;

    private ZapretCatalog? _catalog;
    private IReadOnlyList<string> _catalogServices = [];
    private IReadOnlyList<OwnCatalogEntry> _mine = [];

    /// <summary>Что-то изменилось — разделу «Маршруты» надо перечитать.</summary>
    public bool Changed { get; private set; }

    public PinWindow(ServiceDefinition service, ServicePart part)
        : this(new PinTarget
        {
            Title = $"{service.Name} · {part.Name}",
            Short = part.Name,
            Source = $"Список: {part.List}",
            Zones = HostListReader.Read(part.List, ZapretPaths.Discover()?.Root, out _)
                .Select(d => d.TrimStart('*', '.'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Match = part.ByAddress ? MatchKind.IpSet : MatchKind.HostList,
            Value = part.List,
            ByAddress = part.ByAddress,
            Current = () => Describe(service, part),
        })
    {
    }

    /// <summary>
    /// Пин своего домена.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Зона одна — та, что вписал человек. Поддомены она покрывает сама:
    /// <see cref="Covers"/> считает <c>cdn.example.com</c> принадлежащим
    /// <c>example.com</c> так же, как у списков.
    /// </para>
    /// <para>
    /// Показывается имя без звёздочки, а пишется со звёздочкой. Правило
    /// хранится как <c>*.example.com</c> — так его записал раздел, — и
    /// написать иначе значит не найти прежнее и завести рядом второе.
    /// Человеку же звёздочка ничего не говорит: он вводил <c>example.com</c>.
    /// </para>
    /// </remarks>
    public PinWindow(string rule)
        : this(new PinTarget
        {
            Title = rule.TrimStart('*', '.'),
            Short = rule.TrimStart('*', '.'),
            Source = "Свой домен",
            Zones = [rule.TrimStart('*', '.')],
            Match = MatchKind.Domain,
            Value = rule,
            ByAddress = false,
            Current = () => Describe(rule),
        })
    {
    }

    private PinWindow(PinTarget target)
    {
        InitializeComponent();

        _target = target;

        ServiceName.Text = target.Title;

        Loaded += (_, _) => Fill();
    }

    /// <summary>Куда идёт часть сервиса — теми же словами, что в списке маршрутов.</summary>
    private static string Describe(ServiceDefinition service, ServicePart part)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var root = ZapretPaths.Discover()?.Root;

            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            RuleSetExpander.Expand(engine.RuleSet, root);

            return ServiceRouting.Describe(service, engine, root, UserRulesFile.Load())
                .FirstOrDefault(p => p.Part.List == part.List)?.DescribeMode()
                ?? "неизвестно";
        }
        catch (Exception ex)
        {
            return "правила не читаются: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Куда идёт свой домен — по его собственному правилу.
    /// </summary>
    /// <remarks>
    /// Через <c>ServiceRouting</c> не пойдёшь: тот описывает части каталога,
    /// а своего домена в каталоге нет. Правило у него одно и читается прямо.
    /// </remarks>
    private static string Describe(string domain)
    {
        try
        {
            var entry = UserRulesFile.Load().Entries.FirstOrDefault(e =>
                e.Match == MatchKind.Domain
                && string.Equals(e.Value, domain, StringComparison.OrdinalIgnoreCase));

            return entry?.Mode switch
            {
                RoutingMode.Direct => "напрямую",
                RoutingMode.Desync => "десинк",
                RoutingMode.Proxy => "через VPN",
                _ => "правила нет",
            };
        }
        catch (Exception ex)
        {
            return "правила не читаются: " + ex.GetBaseException().Message;
        }
    }

    private void Fill()
    {
        ServiceState.Text = $"Сейчас: {_target.Current()}. {_target.Source} — "
            + $"имён {_target.Zones.Count}"
            + (_target.Zones.Count > 0 ? $", например {_target.Zones[0]}." : ".");

        ShowPins();

        if (_target.ByAddress)
        {
            Status.Text = "Часть задана подсетями, а hosts понимает только имена — "
                + "прибивать нечего.";

            PinButton.IsEnabled = false;
        }
    }

    /// <summary>Показывает, что уже прибито по этому сервису.</summary>
    private void ShowPins()
    {
        var ours = Pinned();

        UnpinButton.Visibility = ours.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (ours.Count > 0)
        {
            UnpinDetail.Text = $"Прибито имён: {ours.Count} — {string.Join(", ", ours.Take(4))}"
                + (ours.Count > 4 ? " и ещё…" : ".");
        }
    }

    /// <summary>Наши прибитые имена, попадающие в зоны этого сервиса.</summary>
    private IReadOnlyList<string> Pinned()
    {
        try
        {
            return HostsEditor.Pins().Keys.Where(Covers).ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Покрывает ли какая-нибудь зона сервиса это имя.
    /// </summary>
    /// <remarks>
    /// По зоне, а не по точному совпадению: списки хранят <c>openai.com</c>,
    /// каталог — <c>api.openai.com</c> и ещё три десятка поддоменов. При точном
    /// сравнении не совпало бы ни одно имя, и окно ответило бы «в каталоге нет
    /// адресов» на сервисе, который каталог покрывает целиком.
    /// </remarks>
    private bool Covers(string name) =>
        _target.Zones.Any(zone => string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase));

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string what })
            return;

        try
        {
            var file = UserRulesFile.Load();
            var kind = _target.Match;

            if (what == "reset")
                file.Remove(kind, _target.Value);
            else
                file.Set(kind, _target.Value, Mode(what));

            file.Save();
            Changed = true;

            Status.Text = what == "reset"
                ? "Свой выбор убран: снова действует правило из поставки. "
                  + "Применится при следующем запуске движков."
                : $"Записано: «{_target.Short}» → {Describe(Mode(what))}. "
                  + "Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    private static RoutingMode Mode(string what) => what switch
    {
        "direct" => RoutingMode.Direct,
        "desync" => RoutingMode.Desync,
        _ => RoutingMode.Proxy,
    };

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Direct => "напрямую",
        RoutingMode.Desync => "десинк",
        _ => "через VPN",
    };

    /// <summary>Собирает источники адреса и показывает второй шаг.</summary>
    private void OnPin(object sender, RoutedEventArgs e)
    {
        var own = OwnCatalog.Load();

        foreach (var problem in own.Problems)
            Status.Text = "Каталог NetZapret: " + problem;

        _mine = own.For(_target.Zones);
        _catalog = ZapretCatalog.Discover();

        _catalogServices = _catalog?.NamesByService()
            .Where(pair => pair.Value.Any(Covers))
            .Select(pair => pair.Key)
            .ToList() ?? [];

        var rows = new List<SourceRow>
        {
            // Первой — подбор: он проверяет всех остальных сам и отвечает
            // на вопрос, который прежде задавался человеку, — какой из
            // источников сейчас жив и не отказывает по стране.
            new("auto",
                "Подобрать автоматически",
                "Проверяет настоящий адрес, свой каталог и посредников Zapret живым запросом "
                + "и берёт для каждого имени того, кто отвечает сам и не отказывает по стране. "
                + "Настоящий адрес — первым, если он работает."),

            new("honest",
                "Спросить честный резолвер сейчас",
                "Настоящий адрес по DoH, взятый на этой машине в момент закрепления, "
                + "а не в день сборки. Каждый найденный адрес проверяется рукопожатием — "
                + "ответ-петля отбрасывается."),
        };

        // Свой каталог идёт вперёд чужого: он для того и заведён, что нужного
        // имени в чужом обычно нет вовсе.
        rows.AddRange(_mine.Select((entry, index) => new SourceRow(
            "own:" + index,
            entry.Name,
            entry.Resolve
                ? "Каталог NetZapret, адрес спрашивается сейчас."
                : $"Каталог NetZapret, адрес задан: {string.Join(", ", entry.Addresses)}.")));

        if (_catalog is not null && _catalogServices.Count > 0)
        {
            rows.AddRange(_catalog.Profiles()
                .Select(profile => (profile, covered: _catalog.Answers(_catalogServices, profile.Id)
                    .Count(a => Covers(a.Key))))
                .Where(x => x.covered > 0)
                .Select(x => new SourceRow(
                    "set:" + x.profile.Id,
                    x.profile.Name,
                    $"Чужой прокси из каталога Zapret, покрывает имён: {x.covered}. "
                    + "Годится, когда сайт отказывает по стране, — но живёт ровно столько, "
                    + "сколько живёт узел за ним.")));
        }

        if (rows.Count == 1 && _catalog is null)
        {
            Status.Text = "Каталог адресов Zapret не найден — остаётся честный резолвер.";
        }

        SourceList.ItemsSource = rows;

        Checks.Visibility = Visibility.Collapsed;
        Actions.Visibility = Visibility.Collapsed;
        Sources.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        Sources.Visibility = Visibility.Collapsed;
        Actions.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
    }

    private async void OnSource(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
            return;

        IsEnabled = false;
        Status.Text = "Спрашиваю адреса и проверяю каждый…";
        _autoReport = null;

        try
        {
            // Подбор не прибивает сам: он показывает проверку и отмечает
            // лучших, а прибивает кнопка — после того, как человек посмотрел
            // и, может быть, выбрал иначе. Просьба владельца 23.09.
            if (id == "auto")
            {
                await AutoAsync();

                bool any = _blocks.Any(b => b.Lines.Any(l => l.IsSelected && l.Address is not null));

                Status.Text = _autoReport
                    + (any
                        ? "\n\nВыбранные отмечены ✓ — щелчок по строке выбирает другой адрес. "
                          + "Записывает «Прибить выбранное»."
                        : string.Empty);

                PinChosenButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            var answers = await AnswersAsync(id);

            if (answers.Count == 0)
            {
                // Молчание всех адресов — тоже ответ. Так ведёт себя сервис,
                // закрытый по самому адресу: честное имя разрешается, а до
                // адреса из России не достучаться. Пин тут бессилен по
                // существу — он задаёт адрес, а не дорогу к нему.
                Status.Text = id.StartsWith("set:", StringComparison.Ordinal)
                    ? "Этот набор не покрывает ни одного имени сервиса."
                    : "Ни один адрес не ответил. Похоже, сервис закрыт по самому адресу, "
                      + "а не по имени: пин тут не поможет — нужен туннель либо набор "
                      + "с чужим прокси.";

                OnBack(sender, e);
                return;
            }

            Pin(answers, Source(id));
            OnBack(sender, e);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось закрепить: " + ex.GetBaseException().Message;
            OnBack(sender, e);
        }
        finally
        {
            IsEnabled = true;
        }
    }

    /// <summary>Прибивает выбранное в таблице автоподбора.</summary>
    private void OnPinChosen(object sender, RoutedEventArgs e)
    {
        var answers = _blocks
            .Select(b => b.Lines.FirstOrDefault(l => l.IsSelected))
            .Where(l => l?.Address is not null)
            .ToDictionary(l => l!.Host, l => l!.Address!, StringComparer.OrdinalIgnoreCase);

        if (answers.Count == 0)
        {
            Status.Text = "Ни для одного имени не выбран адрес — прибивать нечего.";
            return;
        }

        try
        {
            Pin(answers, "автоподбор");
            PinChosenButton.Visibility = Visibility.Collapsed;
            OnBack(sender, e);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось закрепить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Выбор в таблице сменился — заголовок блока говорит о новом.</summary>
    private void OnPickChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CheckLine line })
            return;

        var block = _blocks.FirstOrDefault(b => b.Host == line.Host);

        if (block is null)
            return;

        block.Title = line.Address is null
            ? $"{line.Host} — не прибивать"
            : $"{line.Host} → {line.Address}  ·  {line.Source}";

        block.TitleBrush = line.Address is null ? (Brush)FindResource("Muted") : line.Brush;
    }

    /// <summary>Пишет пин и уводит маршрут части напрямую.</summary>
    private void Pin(Dictionary<string, string> answers, string source)
    {
        var result = HostsEditor.Pin(answers, note: $"{_target.Short} — {source}");

        // Маршрут уводится напрямую тем же движением. Это не довесок,
        // а условие работы пина: доменное правило срабатывает поверх
        // прибитого адреса и уводит соединение мимо него.
        var file = UserRulesFile.Load();
        file.Set(_target.Match, _target.Value, RoutingMode.Direct);
        file.Save();
        HostsEditor.FlushDns();

        Changed = true;
        ShowPins();

        // Откатили — говорим только это: «прибито» про несуществующее
        // хуже молчания.
        Status.Text = result.Reverted
            ?? $"Прибито имён: {result.Pinned}. Маршрут части уведён напрямую — "
            + "иначе правило сработало бы поверх адреса."
            + (result.Backup is null ? string.Empty : $" Копия прежнего файла: {result.Backup}.")

            // Чужая строка на то же имя никуда не делась: наш блок стоит
            // выше и разбирается первым, но человек, снявший наш пин,
            // получит её и решит, что снятие не сработало.
            + (result.Shadowed.Count == 0
                ? string.Empty
                : $" Ниже в файле есть чужие строки на те же имена: {string.Join(", ", result.Shadowed.Take(3))}.");
    }

    private string Source(string id) =>
        id == "auto" ? "автоподбор"
        : id == "honest" ? "честный резолвер"
        : id.StartsWith("own:", StringComparison.Ordinal) ? _mine[int.Parse(id[4..])].Name
        : "набор " + id[4..];

    private async Task<Dictionary<string, string>> AnswersAsync(string id)
    {
        if (id.StartsWith("set:", StringComparison.Ordinal))
        {
            return _catalog!.Answers(_catalogServices, id[4..])
                .Where(pair => Covers(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        if (id.StartsWith("own:", StringComparison.Ordinal))
        {
            var entry = _mine[int.Parse(id[4..])];
            var names = entry.Names.Select(n => n.TrimStart('*', '.')).ToList();

            return entry.Resolve
                ? await HonestAsync(names)
                : names.ToDictionary(n => n, _ => entry.Addresses[0], StringComparer.OrdinalIgnoreCase);
        }

        var known = _catalog?.Answers(_catalogServices, null).Keys.Where(Covers).ToList() ?? [];

        return await HonestAsync(known.Concat(_target.Zones)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    /// <summary>Что сказал последний автоподбор — дописывается к итогу закрепления.</summary>
    private string? _autoReport;

    /// <summary>
    /// Подбирает адрес каждому имени сервиса (PinPicker).
    /// </summary>
    /// <remarks>
    /// Имена те же, что у прочих источников: зоны сервиса и имена каталога
    /// внутри них. Подробности по каждому имени — в журнал: в окне место
    /// только главному.
    /// </remarks>
    private async Task AutoAsync()
    {
        var own = OwnCatalog.Load();

        var zones = _target.Zones.Select(n => n.TrimStart('*', '.')).ToList();

        // К голой зоне — её www: hosts зон не знает, и прибитое голое имя
        // www не покрывает, а сайты сплошь переадресуют именно туда. Прочие
        // переадресации подбор находит сам.
        //
        // И имена из кэша DNS под теми же зонами: открытый и не загрузившийся
        // сайт оставляет там всё, что ему было нужно, — sso, api, static, —
        // а из списка сервиса этого не видно (DnsCache).
        var cached = DnsCache.Under(DnsCache.Names(), zones);

        // И имена своего каталога под этими зонами: там записано то, что сайту
        // нужно заведомо, — у crunchyroll sso и beta-api, без которых страница
        // встаёт пустой, а браузер до них и не доходит.
        var listed = DnsCache.Under(own.Services.SelectMany(s => s.Names), zones);

        var names = (_catalog?.NamesByService().Values.SelectMany(v => v).Where(Covers) ?? [])
            .Concat(zones)
            .Concat(zones.Where(z => z.Count(c => c == '.') == 1).Select(z => "www." + z))
            .Concat(cached)
            .Concat(listed)
            .Select(n => n.TrimStart('*', '.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var blocks = new Dictionary<string, CheckBlock>(StringComparer.OrdinalIgnoreCase);

        _blocks = [];
        CheckList.ItemsSource = _blocks;
        Checks.Visibility = Visibility.Visible;
        Sources.Visibility = Visibility.Collapsed;
        PinChosenButton.Visibility = Visibility.Collapsed;

        CheckBlock Block(string host)
        {
            if (!blocks.TryGetValue(host, out var block))
            {
                block = new CheckBlock(host, (Brush)FindResource("Muted"));
                blocks[host] = block;
                _blocks.Add(block);
            }

            return block;
        }

        foreach (var name in names)
            Block(name);

        // Счёт растёт на ходу: переадресации добавляют имена, которых
        // в начале не было.
        var progress = new Progress<int>(n =>
            Status.Text = $"Подбираю адрес: готово {n} из {Math.Max(n, blocks.Count)}…");

        var probes = new Progress<(string Host, PinProbe Probe)>(p => Block(p.Host).Lines.Add(Line(p.Host, p.Probe)));

        // Посредники — из живого каталога Zapret, где он стоит, и из снимка,
        // который едет с программой: без Zapret остался бы только второй.
        var picks = await PinPicker.PickAsync(
            names,
            host => [.. own.PinCandidates(host), .. _catalog?.AnswersFor(host) ?? []],
            [.. _catalog?.Intermediaries() ?? [], .. own.Intermediaries],
            progress,
            CancellationToken.None,
            probes);

        // Итог: выбранный подбором — первым и отмеченным, остальные —
        // в порядке, в каком подбор их оценил, последней — «не прибивать».
        // Выбрать можно любого, кто ответил; отказавших и молчащих — нет.
        foreach (var pick in picks)
        {
            var block = Block(pick.Host);

            block.Lines.Clear();

            if (pick.Chosen is { } best)
                block.Lines.Add(Line(pick.Host, best));

            foreach (var probe in pick.Rejected)
                block.Lines.Add(Line(pick.Host, probe));

            var skip = new CheckLine(pick.Host, null, string.Empty, "не прибивать это имя", string.Empty,
                (Brush)FindResource("Muted"), selectable: true);

            block.Lines.Add(skip);

            // Отметка ставится после наполнения: переключатель, отмеченный
            // до появления в дереве, группы ещё не знает.
            (pick.Chosen is null ? skip : block.Lines[0]).IsSelected = true;

            block.Title = pick.Chosen is { } chosen
                ? $"{pick.Host} → {chosen.Candidate.Address}  ·  {chosen.Candidate.Label}"
                : $"{pick.Host} — рабочего адреса нет, не прибивать";

            block.TitleBrush = pick.Chosen is null ? (Brush)FindResource("Danger") : block.Lines[0].Brush;
        }

        foreach (var pick in picks)
        {
            Journal.Write("пин", pick.Chosen is { } chosen
                ? $"{pick.Host}: {chosen.Candidate.Address} ({chosen.Candidate.Label}, {chosen.Detail}); "
                  + $"отвергнуто {pick.Rejected.Count}"
                : $"{pick.Host}: адрес не подобран, проверено {pick.Rejected.Count}");
        }

        _autoReport = PinPicker.Summarize(picks, _target.Zones.FirstOrDefault()?.TrimStart('*', '.'));
    }

    /// <summary>Блоки таблицы проверки — по одному на имя.</summary>
    private ObservableCollection<CheckBlock> _blocks = [];

    /// <summary>Строка таблицы проверки: цвет по исходу, выбрать можно ответившего.</summary>
    private CheckLine Line(string host, PinProbe probe) => new(
        host,
        probe.Candidate.Address,
        probe.Candidate.Label,
        probe.Detail,
        $"{probe.Elapsed.TotalMilliseconds:0} мс",
        (Brush)FindResource(probe.Verdict switch
        {
            PinVerdict.Works => "Accent",
            PinVerdict.Challenge => "Warn",
            PinVerdict.Refused => "Danger",
            _ => "Muted",
        }),
        selectable: probe.Usable);

    /// <summary>
    /// Спрашивает адрес у честного резолвера и проверяет каждый ответ.
    /// </summary>
    /// <remarks>
    /// Проверяется каждый кандидат, а не берётся первый. У сети доставки
    /// шардов десятки, отвечают не все, и какой достанется — зависит от того,
    /// кого спросили: у <c>image.tmdb.org</c> один резолвер отдавал петлю,
    /// другой — шард, отдающий постер.
    /// </remarks>
    private async Task<Dictionary<string, string>> HonestAsync(IReadOnlyList<string> names)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var slots = new SemaphoreSlim(6);

        int done = 0;

        var work = names.Select(async name =>
        {
            await slots.WaitAsync();

            try
            {
                var candidates = await DohResolver.CandidatesAsync(name, http, CancellationToken.None);

                if (candidates.Count == 0)
                    return;

                var checks = candidates
                    .Select(async a => (Address: a, Works: await DohResolver.ServesAsync(a, name, CancellationToken.None)))
                    .ToList();

                var outcomes = await Task.WhenAll(checks);
                var working = outcomes.FirstOrDefault(o => o.Works).Address;

                lock (found)
                {
                    if (working is not null)
                        found[name] = working;
                }
            }
            catch (Exception)
            {
                // Имя без ответа просто не попадёт в пин.
            }
            finally
            {
                slots.Release();

                int at = Interlocked.Increment(ref done);
                Dispatcher.Invoke(() => Status.Text = $"Спрошено {at} из {names.Count}…");
            }
        });

        await Task.WhenAll(work).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return found;
    }

    private void OnUnpin(object sender, RoutedEventArgs e)
    {
        try
        {
            var ours = Pinned();

            if (ours.Count == 0)
                return;

            var result = HostsEditor.Unpin(ours);
            HostsEditor.FlushDns();

            Changed = true;
            ShowPins();

            Status.Text = $"Снято имён: {ours.Count}. Осталось прибитых: {result.Pinned}. "
                + "Маршрут при этом не трогали — он остался «напрямую».";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось снять: " + ex.GetBaseException().Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
