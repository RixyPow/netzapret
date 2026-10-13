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

    private void Reload() => _ = ReloadAsync();

    private async Task ReloadAsync()
    {
        int reading = ++_reading;
        var statusBefore = Status.Text;

        // Первый заход: список пуст, пусть строка скажет, что идёт.
        if (_all.Count == 0)
        {
            Status.Text = "Читаю маршруты…";
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
                .Where(part => !part.Part.ByAddress)
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

        return new RoutesData(
            settings, zapretRoot, userRules, preset, zones, providers, engine, problems,
            services, pins, pinZones, clashes);
    }
}
