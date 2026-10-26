using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Mobile.Shell;
using NetZapret.Zapret;

namespace NetZapret.Mobile.Screens;

/// <summary>Всё, что «Маршруты» читают для одной пересборки, — прочитанное в фоне.</summary>
internal sealed record RoutesData(
    AppSettings Settings,
    UserRulesFile Rules,
    IReadOnlyList<(ServiceDefinition Service, IReadOnlyList<ServiceRouting.PartStatus> Parts)> Services,
    IReadOnlyDictionary<string, string> AppLabels);

/// <summary>
/// «Маршруты»: куда идёт каждая часть сервиса, свои сайты и приложения.
/// </summary>
/// <remarks>
/// <para>
/// Те же данные и те же записи, что у «Маршрутов» Windows: состояние части —
/// <see cref="ServiceRouting.Describe"/> настоящим движком правил, запись —
/// <see cref="UserRulesFile.Set"/> с <c>before: ServicePart.Within</c>
/// (узкая часть раньше широкой, иначе не сработает никогда). Файл правил
/// один на обе системы, и маршрут, выбранный на телефоне, читается окном
/// так же.
/// </para>
/// <para>
/// Папки, их порядок и перетаскивание (P1–P2) пока не перенесены: сервисы
/// идут в порядке справочника, части раскрываются касанием.
/// </para>
/// </remarks>
internal sealed class RoutesSection : Section
{
    private readonly TextBox _search = new() { PlaceholderText = "Найти сервис или добавить сайт" };
    private readonly Button _add;
    private readonly StackPanel _filters = new();
    private readonly StackPanel _list = new() { Spacing = 10 };
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private RoutesData? _data;
    private int _filter;
    private int _reading;
    private DispatcherTimer? _typing;

    public RoutesSection(MainView shell) : base(shell)
    {
        _add = Ui.Button("Добавить", AddOwn, "primary");
        _add.IsVisible = false;

        var search = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        search.Children.Add(_search);
        Grid.SetColumn(_add, 1);
        search.Children.Add(_add);

        // Поиск сужает список не на каждую букву, а через паузу: сотня
        // карточек на каждое нажатие — заметная задержка на слабом телефоне.
        _search.TextChanged += (_, _) =>
        {
            _typing?.Stop();
            _typing = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _typing.Tick += (_, _) =>
            {
                _typing?.Stop();
                ShowList();
            };
            _typing.Start();
        };

        Body.Children.Add(Ui.Text("Куда идёт каждый сервис: через VPN, напрямую или с десинком. "
            + "Применяется при следующем запуске движков.", "muted", "small"));
        Body.Children.Add(search);
        Body.Children.Add(_filters);
        Body.Children.Add(_list);
    }

    public override string Title => "Маршруты";

    protected override void Shown()
    {
        if (_data is null)
        {
            _list.Children.Clear();
            _list.Children.Add(Placeholder("Читаю маршруты…"));
        }

        Reload();
    }

    private void Reload()
    {
        int reading = ++_reading;

        Load(() => ReadAll(Host), data =>
        {
            // Пересборок подряд бывает несколько — показывается последняя.
            if (reading != _reading)
                return;

            _data = data;
            ShowFilters();
            ShowList();
        });
    }

    internal static RoutesData ReadAll(IMobileHost host)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var zapretRoot = ZapretPaths.Discover()?.Root;
        var rules = UserRulesFile.Load();
        var engine = RuleSetLoader.LoadFor(settings);
        RuleSetExpander.Expand(engine.RuleSet, zapretRoot);

        var services = ServiceCatalog.All
            .Select(service => (service, ServiceRouting.Describe(service, engine, zapretRoot, rules)))
            .ToList();

        IReadOnlyDictionary<string, string> labels;

        try
        {
            labels = host.InstalledApps().GroupBy(a => a.Package).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);
        }
        catch (Exception)
        {
            labels = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return new RoutesData(settings, rules, services, labels);
    }

    private static readonly string[] FilterNames = ["Все", "VPN", "Десинк", "Напрямую"];

    private bool Passes(RoutingMode mode, bool off) => _filter switch
    {
        1 => !off && mode == RoutingMode.Proxy,
        2 => !off && mode == RoutingMode.Desync,
        3 => off || mode == RoutingMode.Direct,
        _ => true,
    };

    private void ShowFilters()
    {
        if (_data is null)
            return;

        var parts = _data.Services.SelectMany(s => s.Parts).ToList();
        int vpn = parts.Count(p => !p.Off && p.Mode == RoutingMode.Proxy);
        int desync = parts.Count(p => !p.Off && p.Mode == RoutingMode.Desync);
        int direct = parts.Count - vpn - desync;

        var names = new[] { FilterNames[0], $"VPN {vpn}", $"Десинк {desync}", $"Прямо {direct}" };

        _filters.Children.Clear();
        _filters.Children.Add(Ui.Segmented(names, _filter, index =>
        {
            _filter = index;
            ShowList();
        }));
    }

    private void ShowList()
    {
        if (_data is null)
            return;

        var query = _search.Text?.Trim() ?? string.Empty;
        _list.Children.Clear();

        // Ничего не нашлось, а написано имя сайта или адрес — предложить добавить.
        bool addable = query.Length > 0 && (DomainInput.Normalize(query) is not null || AddressInput.Normalize(query) is not null);

        _list.Children.Add(OwnCard(query));
        _list.Children.Add(AppsCard(query));

        int shown = 0;

        foreach (var (service, parts) in _data.Services)
        {
            bool nameMatches = query.Length == 0 || service.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

            var visible = parts
                .Where(p => p.DomainCount > 0 || p.Part.ByAddress)
                .Where(p => Passes(p.Mode, p.Off))
                .Where(p => nameMatches || p.Part.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || p.Domains.Any(d => d.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (visible.Count == 0)
                continue;

            shown++;
            _list.Children.Add(ServiceCard(service, visible, query.Length > 0));
        }

        _add.IsVisible = addable;

        if (shown == 0 && query.Length > 0)
        {
            _list.Children.Add(Ui.Card(Ui.Text(addable
                ? $"В справочнике «{query}» нет. «Добавить» запишет его своим маршрутом — напрямую, а путь меняется в его строке."
                : "Ничего не нашлось.", "muted")));
        }
    }

    private Control ServiceCard(ServiceDefinition service, IReadOnlyList<ServiceRouting.PartStatus> parts, bool searching)
    {
        bool open = searching || _expanded.Contains(service.Name);

        var summary = parts
            .GroupBy(p => p.DescribeMode())
            .Select(g => g.Key)
            .Distinct()
            .ToList();

        var chips = new WrapPanel { Orientation = Orientation.Horizontal };

        foreach (var word in summary)
        {
            var sample = parts.First(p => p.DescribeMode() == word);
            var pill = PathSheet.Pill(word, sample.Mode, sample.Off);
            pill.Margin = new Thickness(0, 4, 6, 0);
            chips.Children.Add(pill);
        }

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        head.Children.Add(Ui.Stack(2, Ui.Text(service.Name, "h3"), chips));

        var arrow = Ui.Icon(Icons.Chevron, 14, Ui.Brush("Muted"));
        arrow.RenderTransform = new RotateTransform(open ? -90 : 90);
        arrow.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(arrow, 1);
        head.Children.Add(arrow);

        var headButton = new Button { Content = head, Classes = { "row" } };
        headButton.Click += (_, _) =>
        {
            if (!_expanded.Add(service.Name))
                _expanded.Remove(service.Name);

            ShowList();
        };

        var stack = Ui.Stack(2, headButton);

        if (open)
        {
            foreach (var part in parts)
                stack.Children.Add(PartRow(service, part));
        }

        return Ui.Card(stack);
    }

    private Control PartRow(ServiceDefinition service, ServiceRouting.PartStatus part)
    {
        var detail = part.Part.ByAddress
            ? "по адресам"
            : Ui.Plural(part.DomainCount, "домен", "домена", "доменов");

        detail += part.Explicit ? " · выбрано вами" : " · по умолчанию";

        if (part.Part.Note is { Length: > 0 } note)
            detail += " · " + note;

        var row = RouteRow(part.Part.Name, detail, part.DescribeMode(), part.Mode, part.Off);

        row.Click += (_, _) => PathSheet.Show(
            Shell,
            $"{service.Name} — {part.Part.Name}",
            part.Explicit ? PathSheet.Of(part.Mode, part.Off) : PathPick.Default,
            part.DescribeDefault(),
            removable: false,
            offOffered: true,
            pick => WritePart(part.Part, pick));

        return row;
    }

    /// <summary>Строка маршрута: название, пояснение, пилюля пути справа.</summary>
    private static Button RouteRow(string title, string detail, string word, RoutingMode mode, bool off)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10 };
        grid.Children.Add(Ui.Stack(1, Ui.Text(title), Ui.Text(detail, "muted", "small")));

        var pill = PathSheet.Pill(word, mode, off);
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        return new Button { Content = grid, Classes = { "row" }, MinHeight = 52 };
    }

    private void WritePart(ServicePart part, PathPick pick)
    {
        var kind = part.ByAddress ? MatchKind.IpSet : MatchKind.HostList;

        Write(file =>
        {
            switch (pick)
            {
                case PathPick.Default:
                    file.Remove(kind, part.List);
                    break;

                case PathPick.Off:
                    file.SetOff(kind, part.List, before: part.Within);
                    break;

                default:
                    file.Set(kind, part.List, PathSheet.ModeOf(pick), recipe: null, before: part.Within);
                    break;
            }
        }, $"«{part.Name}» — {(pick == PathPick.Default ? "по умолчанию" : PathSheet.Word(pick))}");
    }

    private void Write(Action<UserRulesFile> change, string what)
    {
        try
        {
            var file = UserRulesFile.Load();
            change(file);
            file.Save();
            Shell.OfferRestart(what);
            Reload();
        }
        catch (Exception ex)
        {
            Shell.Say("Не записалось: " + ex.GetBaseException().Message);
        }
    }

    // ── Свои сайты ────────────────────────────────────────────────────────

    private Control OwnCard(string query)
    {
        var own = _data!.Rules.Entries
            .Where(e => e.Match is MatchKind.Domain or MatchKind.Ip
                || e.Match == MatchKind.HostList && OwnLists.IsOwn(e.Value))
            .Where(e => Passes(e.Mode, e.Off))
            .Where(e => query.Length == 0 || e.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var stack = Ui.Stack(2, Ui.Caption("Свои сайты и адреса"));

        if (own.Count == 0)
        {
            stack.Children.Add(Ui.Text(query.Length == 0
                ? "Пока нет. Впишите имя сайта или адрес в поиск и нажмите «Добавить»."
                : "Среди своих не нашлось.", "muted", "small"));
        }

        foreach (var entry in own)
        {
            var name = entry.Match == MatchKind.HostList ? "Список «" + OwnLists.NameOf(entry.Value) + "»" : entry.Value.TrimStart('*', '.');
            var detail = entry.Match switch
            {
                MatchKind.Ip => "адрес или подсеть",
                MatchKind.HostList => "свой список",
                _ => "имя и поддомены",
            };

            var row = RouteRow(name, detail, entry.DescribeMode(), entry.Mode, entry.Off);
            var captured = entry;

            row.Click += (_, _) => PathSheet.Show(
                Shell,
                name,
                PathSheet.Of(captured.Mode, captured.Off),
                defaultLabel: null,
                removable: true,
                offOffered: false,
                pick => WriteOwn(captured, name, pick));

            stack.Children.Add(row);
        }

        return Ui.Card(stack);
    }

    private void WriteOwn(UserRuleEntry entry, string name, PathPick pick)
    {
        Write(file =>
        {
            if (pick == PathPick.Remove)
            {
                file.Remove(entry.Match, entry.Value);

                if (entry.Match == MatchKind.HostList && OwnLists.IsOwn(entry.Value))
                    OwnLists.Delete(entry.Value);
            }
            else
            {
                file.Set(entry.Match, entry.Value, PathSheet.ModeOf(pick), recipe: null);
            }
        }, pick == PathPick.Remove ? $"Убран маршрут: {name}" : $"{name} — {PathSheet.Word(pick)}");
    }

    /// <summary>Своё имя или адрес из поиска — напрямую, как «Добавить» окна.</summary>
    private void AddOwn()
    {
        var text = _search.Text;

        if (AddressInput.Normalize(text) is { } subnet)
        {
            Write(file => file.Set(MatchKind.Ip, subnet, RoutingMode.Direct, recipe: null), $"Добавлен маршрут: {subnet} → напрямую");
        }
        else if (DomainInput.Normalize(text) is { } name)
        {
            Write(file => file.Set(MatchKind.Domain, "*." + name, RoutingMode.Direct, recipe: null), $"Добавлен маршрут: {name} → напрямую");
        }
        else
        {
            Shell.Say("Это не похоже на имя сайта. Нужно что-то вроде example.com.");
            return;
        }

        _search.Text = string.Empty;
    }

    // ── Приложения ────────────────────────────────────────────────────────

    private Control AppsCard(string query)
    {
        var apps = _data!.Rules.Entries.Where(e => e.Match == MatchKind.Process)
            .Where(e => Passes(e.Mode, e.Off))
            .Where(e => query.Length == 0 || e.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var stack = Ui.Stack(2, Ui.Caption("Приложения"));

        if (apps.Count == 0)
        {
            stack.Children.Add(Ui.Text("Весь трафик приложения — через VPN или мимо него, по имени пакета.", "muted", "small"));
        }

        var labels = _data.AppLabels;

        foreach (var entry in apps)
        {
            var label = labels.GetValueOrDefault(entry.Value) ?? entry.Value;
            var row = RouteRow(label, entry.Value, entry.DescribeMode(), entry.Mode, entry.Off);
            var captured = entry;

            row.Click += (_, _) => PathSheet.Show(
                Shell,
                label,
                PathSheet.Of(captured.Mode, captured.Off),
                defaultLabel: null,
                removable: true,
                offOffered: false,
                pick => WriteOwn(captured, label, pick));

            stack.Children.Add(row);
        }

        var add = Ui.Button("Добавить приложение", AddApp);
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        add.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(add);

        return Ui.Card(stack);
    }

    internal void AddApp() => Shell.Open(new AppsSection(Shell, (app, pick) =>
    {
        Write(file => file.Set(MatchKind.Process, app.Package, PathSheet.ModeOf(pick), recipe: null),
            $"{app.Label} — {PathSheet.Word(pick)}");
    }));

    // ── Для снимков окна просмотра ────────────────────────────────────────

    internal void ExpandFirst()
    {
        if (_data?.Services.FirstOrDefault(s => s.Parts.Count > 1) is { Service: { } service })
        {
            _expanded.Add(service.Name);
            ShowList();
        }
    }

    internal void ChooseFirst()
    {
        if (_data?.Services.FirstOrDefault(s => s.Parts.Count > 1) is { } first)
        {
            var part = first.Parts[0];
            PathSheet.Show(Shell, $"{first.Service.Name} — {part.Part.Name}",
                part.Explicit ? PathSheet.Of(part.Mode, part.Off) : PathPick.Default,
                part.DescribeDefault(), false, true, _ => { });
        }
    }
}
