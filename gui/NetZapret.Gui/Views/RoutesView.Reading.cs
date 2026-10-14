using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>
/// Всё, что «Маршруты» читают с диска для одной пересборки, — прочитанное в фоне.
/// </summary>
/// <param name="PinZones">Зоны строк по ключу — для пометки пинов: их чтение — сотня файлов списков.</param>
internal sealed record RoutesData(
    AppSettings Settings,
    string? ZapretRoot,
    UserRulesFile Rules,
    ZapretPreset? Preset,
    PresetZones? Zones,
    IReadOnlyDictionary<string, string>? Providers,
    RuleEngine Engine,
    IReadOnlyList<string> Problems,
    IReadOnlyList<(ServiceDefinition Service, IReadOnlyList<ServiceRouting.PartStatus> Parts)> Services,
    IReadOnlyList<string> Pins,
    IReadOnlyDictionary<string, IReadOnlyList<string>> PinZones,
    IReadOnlyList<RouteClash> Clashes);

/// <summary>
/// Пересборка «Маршрутов»: чтение в фоне, строки — в потоке окна.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 13.10: «выноси». Сторож подвисаний писал по разделу «поток окна
/// был занят» 470–916 мс на каждую смену маршрута — и до переделки
/// раздела карточками (18:07, 791 мс), и после. Карточки при пересборке
/// с тех пор не трогаются (раскладка 0 мс), а время уходило на чтение:
/// правила со списками, пресет с его списками, модули lua, hosts и зоны
/// сотни списков для пинов — всё в потоке окна.
/// </para>
/// <para>
/// Пересборок подряд бывает несколько (смена маршрута, следом значок,
/// следом отбор) — показывается последняя: <see cref="_reading"/>.
/// </para>
/// </remarks>
public partial class RoutesView
{
    /// <summary>Номер пересборки: прочитанное для устаревшей не показывается.</summary>
    private int _reading;

    private static RoutesView? _shared;

    /// <summary>Прочитанное заранее, при запуске окна (<see cref="Prepare"/>); <c>null</c> — нет или уже взято.</summary>
    private static Task<RoutesData>? _early;

    /// <summary>
    /// Читает «Маршруты» заранее, в фоне, через пару секунд после запуска окна.
    /// </summary>
    /// <remarks>
    /// Первое чтение после запуска — холодное: разбор правил, пресета и списков
    /// впервые, файлы не в кэше. В тесте 13.10 первые карточки появлялись через
    /// 4,1 с против 0,3 с у повторного чтения, и всё это время раздел стоял
    /// пустым с «Читаю маршруты…» (владелец: «не нравится, как выглядит»).
    /// Через две секунды после запуска — чтобы не отнимать диск у запуска
    /// самого окна и движков.
    /// </remarks>
    public static void Prepare() =>
        _early = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            return ReadAll();
        });

    /// <summary>
    /// Раскодирует значки с диска здесь, в фоне, — сборка строк берёт их из памяти.
    /// </summary>
    /// <remarks>
    /// Первая сборка строк после запуска читала с диска и раскодировала сотню
    /// значков в потоке окна: одна операция на 0,78 с в тесте 13.10. Значки
    /// заморожены (SiteIcons.Decode) — раскодированные в фоне годятся окну.
    /// Имя — то же, что берёт строка (Row).
    /// </remarks>
    private static void WarmIcons(RoutesData data)
    {
        var hosts = data.Services
            .SelectMany(s => s.Parts)
            .Select(part => (part.Example ?? part.Part.Name).TrimStart('*', '.'))
            .Concat(data.Rules.Entries.Where(e => e.Match == MatchKind.Domain).Select(e => e.Value.TrimStart('*', '.')));

        foreach (var host in hosts.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (host.Contains('.') && AddressInput.Normalize(host) is null)
                SiteIcons.Cached(host);
        }
    }

    /// <summary>
    /// Раздел один на всё время работы окна — заходы в него не строят карточки заново.
    /// </summary>
    /// <remarks>
    /// Владелец 13.10: «мне не нравится, как выглядит „читаю маршруты“». Новый
    /// раздел на каждый заход строил сотню карточек с нуля: первая порция —
    /// одна раскладка на 0,19–0,34 с в тесте, у владельца сторож подвисаний
    /// писал 0,38–0,84 с на заходе. Тот же раздел при повторном показе
    /// перечитывает правила в фоне (Loaded → Reload) и меняет только
    /// изменившиеся карточки (<see cref="Show"/>), так что прошлый заход
    /// не выдаётся за нынешний. Отбор, поиск и раскрытые папки переживают уход.
    /// </remarks>
    public static RoutesView Shared()
    {
        _shared ??= new RoutesView();

        // Окно пересоздано (раздел остался в прежнем) — отцепить, иначе WPF
        // не даст поставить его в новое: у элемента один родитель.
        if (_shared.Parent is System.Windows.Controls.ContentControl old)
            old.Content = null;

        return _shared;
    }

    private void Reload() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        int reading = ++_reading;
        var statusBefore = Status.Text;

        // Прочитанное заранее — показать сразу, а свежее дочитать следом:
        // за минуты после запуска правила могли поменяться, и вторая
        // пересборка обновит только изменившиеся карточки. Не готово ещё —
        // не ждать его, читать как обычно: прогрев к тому времени всё равно
        // пошёл.
        if (_early is { IsCompletedSuccessfully: true } early)
        {
            _early = null;
            Apply(early.Result, statusBefore);
            statusBefore = Status.Text;
        }

        RoutesData data;

        try
        {
            data = await Task.Run(ReadAll);
        }
        catch (Exception ex)
        {
            if (reading == _reading)
                Status.Text = "Правила не читаются: " + ex.GetBaseException().Message;

            return;
        }

        if (reading != _reading)
            return;

        Apply(data, statusBefore);
    }

    /// <summary>Читает всё для пересборки — без обращения к окну.</summary>
    private static RoutesData ReadAll()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var zapretRoot = ZapretPaths.Discover()?.Root;
        var userRules = UserRulesFile.Load();

        ZapretPreset? preset = null;
        PresetZones? zones = null;
        IReadOnlyDictionary<string, string>? providers = null;

        try
        {
            if (settings.PresetName is { } name && ZapretPaths.FindPreset(name) is { } path)
                preset = new PresetReader().Load(path);

            zones = preset is null ? null : PresetZones.Build(preset, zapretRoot);

            providers = ZapretPaths.Discover() is { } install
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

        var services = ServiceCatalog.All
            .Select(service => (service, ServiceRouting.Describe(service, engine, zapretRoot, userRules)))
            .ToList();

        IReadOnlyList<string> pins;

        try
        {
            pins = HostsEditor.Pins().Keys.ToList();
        }
        catch (Exception)
        {
            // Нечитаемый hosts не повод не показать раздел.
            pins = [];
        }

        // Зоны — только если есть что помечать: без пинов сотня списков не нужна.
        var pinZones = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        if (pins.Count > 0)
        {
            var keys = services
                .SelectMany(s => s.Item2)
                .Where(part => !part.Part.ByAddress && !part.Part.Bulk)
                .Select(part => RouteKeys.Make(RouteKeys.HostList, part.Part.List))
                .Concat(userRules.Entries
                    .Where(e => e.Match == MatchKind.Domain || (e.Match == MatchKind.HostList && OwnLists.IsOwn(e.Value)))
                    .Select(e => RouteKeys.Make(RouteKeys.Own, e.Value)));

            foreach (var key in keys.Distinct(StringComparer.Ordinal))
                pinZones[key] = RouteKeys.Zones(key, zapretRoot);
        }

        IReadOnlyList<RouteClash> clashes;

        try
        {
            clashes = RouteClashes.FromRules(userRules.Entries, pins);
        }
        catch (Exception)
        {
            clashes = [];
        }

        var data = new RoutesData(
            settings, zapretRoot, userRules, preset, zones, providers, engine, problems,
            services, pins, pinZones, clashes);

        WarmIcons(data);

        return data;
    }
}
