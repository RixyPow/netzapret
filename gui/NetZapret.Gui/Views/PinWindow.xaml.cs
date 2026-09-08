using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Откуда взять адрес для пина.</summary>
public sealed record SourceRow(string Id, string Name, string Note);

/// <summary>
/// Пин в hosts и маршрут сервиса — в одном окне.
/// </summary>
/// <remarks>
/// <para>
/// Повторяет пункт меню консоли «Сервисы и маршруты» → сервис. Логика
/// дублируется намеренно: так решено про весь интерфейс, чтобы не править
/// проверенный код консоли ради окна.
/// </para>
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
    private readonly ServiceDefinition _service;
    private readonly ServicePart _part;
    private readonly IReadOnlyList<string> _zones;

    private ZapretCatalog? _catalog;
    private IReadOnlyList<string> _catalogServices = [];
    private IReadOnlyList<OwnCatalogEntry> _mine = [];

    /// <summary>Что-то изменилось — разделу «Маршруты» надо перечитать.</summary>
    public bool Changed { get; private set; }

    public PinWindow(ServiceDefinition service, ServicePart part)
    {
        InitializeComponent();

        _service = service;
        _part = part;

        _zones = HostListReader.Read(part.List, ZapretPaths.Discover()?.Root, out _)
            .Select(d => d.TrimStart('*', '.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ServiceName.Text = $"{service.Name} · {part.Name}";

        Loaded += (_, _) => Fill();
    }

    private void Fill()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var root = ZapretPaths.Discover()?.Root;

        string mode;

        try
        {
            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            RuleSetExpander.Expand(engine.RuleSet, root);

            mode = ServiceRouting.Describe(_service, engine, root, UserRulesFile.Load())
                .FirstOrDefault(p => p.Part.List == _part.List)?.DescribeMode()
                ?? "неизвестно";
        }
        catch (Exception ex)
        {
            mode = "правила не читаются: " + ex.GetBaseException().Message;
        }

        ServiceState.Text = $"Сейчас: {mode}. Список: {_part.List} — имён {_zones.Count}"
            + (_zones.Count > 0 ? $", например {_zones[0]}." : ".");

        ShowPins();

        if (_part.ByAddress)
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
        _zones.Any(zone => string.Equals(zone, name, StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase));

    private void OnMode(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string what })
            return;

        try
        {
            var file = UserRulesFile.Load();
            var kind = _part.ByAddress ? MatchKind.IpSet : MatchKind.HostList;

            if (what == "reset")
                file.Remove(kind, _part.List);
            else
                file.Set(kind, _part.List, Mode(what));

            file.Save();
            Changed = true;

            Status.Text = what == "reset"
                ? "Свой выбор убран: снова действует правило из поставки. "
                  + "Применится при следующем запуске движков."
                : $"Записано: «{_part.Name}» → {Describe(Mode(what))}. "
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

        _mine = own.For(_zones);
        _catalog = ZapretCatalog.Discover();

        _catalogServices = _catalog?.NamesByService()
            .Where(pair => pair.Value.Any(Covers))
            .Select(pair => pair.Key)
            .ToList() ?? [];

        var rows = new List<SourceRow>
        {
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

        try
        {
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

            var result = HostsEditor.Pin(answers, note: $"{_part.Name} — {Source(id)}");

            // Маршрут уводится напрямую тем же движением. Это не довесок,
            // а условие работы пина: доменное правило срабатывает поверх
            // прибитого адреса и уводит соединение мимо него.
            var file = UserRulesFile.Load();
            file.Set(MatchKind.HostList, _part.List, RoutingMode.Direct);
            file.Save();
            HostsEditor.FlushDns();

            Changed = true;
            ShowPins();

            Status.Text = $"Прибито имён: {result.Pinned}. Маршрут части уведён напрямую — "
                + "иначе правило сработало бы поверх адреса."
                + (result.Backup is null ? string.Empty : $" Копия прежнего файла: {result.Backup}.")

                // Чужая строка на то же имя никуда не делась: наш блок стоит
                // выше и разбирается первым, но человек, снявший наш пин,
                // получит её и решит, что снятие не сработало.
                + (result.Shadowed.Count == 0
                    ? string.Empty
                    : $" Ниже в файле есть чужие строки на те же имена: {string.Join(", ", result.Shadowed.Take(3))}.");

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

    private string Source(string id) =>
        id == "honest" ? "честный резолвер"
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

        return await HonestAsync(known.Concat(_zones)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

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
