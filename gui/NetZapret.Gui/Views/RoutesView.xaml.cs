using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;
using NetZapret.Zapret;

namespace NetZapret.Gui.Views;

/// <summary>Часть сервиса и её маршрут.</summary>
public sealed class PartRow
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required string Mode { get; init; }
    public required Brush Color { get; init; }
    public required int Choice { get; init; }
    public required bool CanRoute { get; init; }
    public required string Letter { get; init; }

    public BitmapImage? Icon { get; set; }

    public Visibility IconShown => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Сервис со своими частями.</summary>
public sealed record ServiceRow(string Name, IReadOnlyList<PartRow> Parts);

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

            Services.ItemsSource = services;

            Status.Text = problems.Count == 0
                ? $"Сервисов: {services.Count}. Выбор пишется в config\\rules.user.yaml и применяется перезапуском."
                : $"Часть списков не нашлась, и эти правила не действуют: {string.Join("; ", problems.Take(3))}";

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

    private static PartRow Row(ServiceRouting.PartStatus part)
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
            Detail = detail,
            Mode = part.DescribeMode(),
            Color = (Brush)Application.Current.FindResource(color),
            Choice = choice,

            // Адресные части значка не получают: у них нет имени, у которого
            // его можно было бы спросить.
            Letter = host.Length > 0 ? host[..1].ToUpperInvariant() : "·",
            CanRoute = true,
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

        _ = Task.Run(async () =>
        {
            foreach (var part in services.SelectMany(s => s.Parts))
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

                // Перерисовываем весь список: строки — простые объекты
                // без уведомлений, и городить их ради значка не стоит.
                Dispatcher.Invoke(() =>
                {
                    part.Icon = icon;

                    Services.ItemsSource = null;
                    Services.ItemsSource = services;
                });
            }
        }, token);
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

        var parts = key.Split('|', 2);

        if (parts.Length != 2)
            return;

        var mode = box.SelectedIndex switch
        {
            0 => RoutingMode.Direct,
            1 => RoutingMode.Desync,
            _ => RoutingMode.Proxy,
        };

        try
        {
            var file = UserRulesFile.Load();
            file.Set(parts[0] == "ipset" ? MatchKind.IpSet : MatchKind.HostList, parts[1], mode);
            file.Save();

            Status.Text = $"Записано: {parts[1]} → {Describe(mode)}. Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    private static string Describe(RoutingMode mode) => mode switch
    {
        RoutingMode.Direct => "напрямую",
        RoutingMode.Desync => "десинк",
        _ => "через VPN",
    };
}
