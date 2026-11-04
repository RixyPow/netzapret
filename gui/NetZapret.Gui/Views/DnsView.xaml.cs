using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Резолвер в списке выбора.</summary>
public sealed class ResolverRow
{
    public required string Name { get; init; }

    /// <summary>Ключ выбора: адрес DoH либо <c>dnscrypt:режим</c> (<see cref="DnsView.CryptTag"/>).</summary>
    public required string Address { get; init; }

    /// <summary>Что написать под именем; <c>null</c> — сам адрес.</summary>
    public string? Display { get; init; }

    public string Shown => Display ?? Address;

    public required string Note { get; init; }

    /// <summary>Отклик либо причина молчания; до замера — прочерк.</summary>
    public string Latency { get; set; } = "—";

    public string Key { get; set; } = "Faint";

    public bool Chosen { get; set; }

    /// <summary>Свой резолвер (<see cref="CustomDns"/>) — его можно убрать.</summary>
    public bool Own { get; init; }

    /// <summary>Годится ли в апстрим туннеля: у своего без DoH — нет.</summary>
    public bool Choosable { get; init; } = true;

    /// <summary>Адрес обычного DNS — по нему свой резолвер и убирается.</summary>
    public string Udp { get; init; } = string.Empty;

    public Visibility RemoveShown => Own ? Visibility.Visible : Visibility.Collapsed;

    public Brush Color => (Brush)Application.Current.FindResource(Key);

    public Brush Edge => (Brush)Application.Current.FindResource(Chosen ? "Accent" : "Border");

    public Visibility MarkShown => Chosen ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Ярлыки из пояснения провайдера: «надёжный, без фильтрации» — два ярлыка.
    /// </summary>
    public IReadOnlyList<string> Tags => Note
        .Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(tag => char.ToUpper(tag[0]) + tag[1..])
        .ToList();

    /// <summary>Логотип с сайта резолвера; <c>null</c> — вместо него буква.</summary>
    public System.Windows.Media.Imaging.BitmapImage? Icon { get; set; }

    /// <summary>Сайт, с которого берётся логотип; <c>null</c> — брать неоткуда.</summary>
    public string? Site { get; init; }

    public string Letter => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "·";

    public Visibility IconShown => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility LetterShown => Icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Пометка по обзору у этого человека: «блокируется» — DoH не прошёл,
    /// «UDP подменяют» — открытый DNS перехвачен; <c>null</c> — нечего сказать.
    /// </summary>
    /// <remarks>
    /// Zapret GUI ставит такие пометки списком (04.11: Cloudflare и Google —
    /// «блокируется»), а у владельца 20.10 те же 8.8.4.4 и 1.1.1.1 по DoH
    /// отвечали честно, подменялся только UDP. Поэтому — по замеру, а не
    /// заранее: туннелю важен DoH, системе Windows с этим адресом — UDP.
    /// </remarks>
    public string? Badge { get; set; }

    public string BadgeKey { get; set; } = "Danger";

    public string? BadgeTip { get; set; }

    public Brush BadgeBrush => (Brush)Application.Current.FindResource(BadgeKey);

    public Visibility BadgeShown => Badge is null ? Visibility.Hidden : Visibility.Visible;

    /// <summary>Сайты сервисов посредника (<see cref="DnsProvider.Services"/>).</summary>
    public IReadOnlyList<string> ServiceSites { get; init; } = [];

    /// <summary>Значки этих сервисов — из кэша значков сайтов, по мере загрузки.</summary>
    public IReadOnlyList<System.Windows.Media.Imaging.BitmapImage> ServiceIcons { get; set; } = [];
}

/// <summary>Раздел списка резолверов (<see cref="DnsGroup"/>).</summary>
public sealed class ResolverGroup
{
    public required string Title { get; init; }

    public string Caption { get; init; } = string.Empty;

    public required IReadOnlyList<ResolverRow> Rows { get; init; }

    public string Count => Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public Visibility CaptionShown => Caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static (string Title, string Caption) Describe(DnsGroup group) => group switch
    {
        DnsGroup.Intermediary => ("Посредники", "закрытые по стране сервисы отдают на свои прокси; автовыбор их не берёт"),
        DnsGroup.Popular => ("Популярные", string.Empty),
        DnsGroup.Open => ("Без фильтрации", string.Empty),
        DnsGroup.Filtering => ("С фильтрацией", "отсекают рекламу, вредоносное или сайты 18+"),
        DnsGroup.Encrypted => ("Шифрованные", "DNSCrypt и ODoH: запрос не подменить и не закрыть по имени"),
        _ => ("Свои", string.Empty),
    };
}

/// <summary>Строка обзора резолверов.</summary>
/// <remarks>
/// Цвета — по смыслу, как на снимке владельца: зелёное прошло, красное
/// перехвачено или подменено, серое не мерилось.
/// </remarks>
public sealed class SurveyRow
{
    private SurveyRow(DnsSurveyRow row, bool pending = false)
    {
        Source = row;
        Pending = pending;
    }

    public DnsSurveyRow Source { get; }

    /// <summary>
    /// Строка-заготовка: провайдер ещё не ответил.
    /// </summary>
    /// <remarks>
    /// Таблица с первого мгновения обзора — во весь список провайдеров, ответ
    /// меняет строку на месте. Прежде строки дописывались по мере ответов, и
    /// таблица, а с ней всё ниже неё, росла на каждом ответе.
    /// </remarks>
    public bool Pending { get; }

    public static SurveyRow Waiting(DnsProvider provider) => new(new DnsSurveyRow { Provider = provider }, pending: true);

    public string Name => Source.Provider.Name;

    public string Doh => Pending ? "…" : Ms(Source.DohMs, Source.DohFailure);

    public string Dot => Pending ? "…" : Ms(Source.DotMs, Source.DotFailure);

    public string Udp
    {
        get
        {
            if (Pending)
                return "…";

            var text = Ms(Source.UdpMs, Source.UdpFailure);

            return Source.UdpMs is not null && Source.UdpAnswered < Source.Provider.Udp.Count
                ? $"{text} {Source.UdpAnswered}/{Source.Provider.Udp.Count}"
                : text;
        }
    }

    public string Real => Pending ? "…" : Source.RealResolver is null
        ? "—"
        : $"{Source.RealResolver} → {Source.RealNetwork ?? "?"}";

    public string Spoof => Pending ? "…" : Source.SpoofChecked == 0 ? "—" : $"{Source.Spoofed}/{Source.SpoofChecked}";

    public Brush DohColor => Paint(Source.DohMs, Source.DohFailure);

    public Brush DotColor => Paint(Source.DotMs, Source.DotFailure);

    public Brush UdpColor => Paint(Source.UdpMs, Source.UdpFailure);

    public Brush RealColor => Brush(Source.RealResolver is null ? "Faint" : Source.Intercepted ? "Danger" : "Accent");

    public Brush SpoofColor => Brush(Source.SpoofChecked == 0 ? "Faint" : Source.Spoofed > 0 ? "Danger" : "Accent");

    public static SurveyRow From(DnsSurveyRow row) => new(row);

    private static string Ms(double? ms, string failure) =>
        ms is { } value ? $"{value:0.0} мс" : failure.Length > 0 ? failure : "—";

    private static Brush Paint(double? ms, string failure) =>
        Brush(ms is not null ? "Accent" : failure.Length > 0 ? "Danger" : "Faint");

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}

/// <summary>
/// Апстрим DNS туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Речь только о том резолвере, которого спрашивает sing-box. Настройки DNS
/// самой Windows не трогаются ни здесь, ни где-либо ещё: подмена системного
/// резолвера ломает то, что от него зависит — корпоративные имена, принтеры,
/// сетевые диски, — и переживает удаление программы.
/// </para>
/// <para>
/// Замер — один, обзор резолверов (<see cref="DnsSurvey"/>): настоящими
/// запросами DoH, DoT и UDP через адаптер, мимо туннеля. Прежде рядом
/// стояла вторая кнопка «Проверить» с замером одного DoH по шести
/// резолверам — обзор меряет то же и больше, и две кнопки только путали.
/// </para>
/// </remarks>
public partial class DnsView : UserControl
{
    private CancellationTokenSource? _work;

    /// <summary>Последний обзор за сеанс: строки, итог словами и время.</summary>
    private sealed record LastSurvey(IReadOnlyList<DnsSurveyRow> Rows, string Status, DateTime At);

    /// <summary>
    /// Последний обзор — общий для всех заходов на вкладку.
    /// </summary>
    /// <remarks>
    /// Вкладка пересоздаётся при каждом заходе, и задержки, намеренные
    /// обзором, пропадали, стоило уйти на другую вкладку и вернуться
    /// (владелец, 04.10). Только на время работы окна: задержка с прошлого
    /// запуска уже ни о чём не говорит.
    /// </remarks>
    private static LastSurvey? _last;

    /// <summary>Показывает последний обзор: таблицу и задержки в списке.</summary>
    private void ShowLast()
    {
        if (_last is not { } last)
            return;

        var choices = _rows;

        foreach (var row in last.Rows)
            Fill(choices, row);

        Survey.ItemsSource = last.Rows.Select(SurveyRow.From).ToList();
        SurveyHead.Visibility = Visibility.Visible;
        SaySurvey($"{last.Status} Проверено в {last.At:HH:mm}.");
        Status.Text = $"Задержка — из обзора в {last.At:HH:mm}.";

        Redraw();
    }

    private CancellationTokenSource? _logos;

    /// <summary>Приставка ключа выбора у шифрованных (<see cref="DnsCrypt"/>).</summary>
    public const string CryptTag = "dnscrypt:";

    /// <summary>Все карточки резолверов подряд — разделы только для показа.</summary>
    private IReadOnlyList<ResolverRow> _rows = [];

    /// <summary>
    /// Сайт, чей значок — логотип резолвера.
    /// </summary>
    /// <remarks>
    /// Значок берётся у самого сайта (SiteIcons), как у сервисов в «Маршрутах».
    /// Имя DoH для этого годится не всегда: у dns.quad9.net и
    /// common.dot.dns.yandex.net значка нет, он на главном сайте. Неизвестным
    /// и своим — по имени DoH без первой части; нет и его — буква.
    /// Буква и у XBOX DNS (значок только SVG, окно его не рисует) — замер 05.10.
    /// </remarks>
    private static string? LogoSite(DnsProvider provider)
    {
        var name = provider.Name;

        if (name.StartsWith("Google", StringComparison.OrdinalIgnoreCase)) return "dns.google";
        if (name.StartsWith("Cloudflare", StringComparison.OrdinalIgnoreCase)) return "one.one.one.one";
        if (name.StartsWith("Quad9", StringComparison.OrdinalIgnoreCase)) return "quad9.net";
        if (name.StartsWith("AdGuard", StringComparison.OrdinalIgnoreCase)) return "adguard-dns.io";
        if (name.StartsWith("Yandex", StringComparison.OrdinalIgnoreCase)) return "dns.yandex.ru";
        // С www: голый opendns.com переадресует на http://www… — с https на http
        // загрузчик не идёт, и логотипа не было (05.10).
        if (name.StartsWith("OpenDNS", StringComparison.OrdinalIgnoreCase)) return "www.opendns.com";
        if (name.StartsWith("CleanBrowsing", StringComparison.OrdinalIgnoreCase)) return "cleanbrowsing.org";
        if (name.StartsWith("NextDNS", StringComparison.OrdinalIgnoreCase)) return "nextdns.io";
        if (name.StartsWith("ControlD", StringComparison.OrdinalIgnoreCase)) return "controld.com";
        if (name.StartsWith("Alibaba", StringComparison.OrdinalIgnoreCase)) return "alidns.com";

        if (provider.TlsName is not { } tls)
            return null;

        var labels = tls.Split('.');

        return labels.Length > 2 ? string.Join('.', labels[^2..]) : tls;
    }

    /// <summary>Логотипы: из кэша сразу, остальные — в фоне, по одному.</summary>
    private void StartLogos()
    {
        _logos?.Cancel();
        _logos = new CancellationTokenSource();

        var token = _logos.Token;
        var rows = _rows;

        foreach (var row in rows)
        {
            row.Icon = row.Site is { } site ? SiteIcons.Cached(site) : null;
            row.ServiceIcons = ServiceIconsOf(row);
        }

        Redraw();

        _ = Task.Run(async () =>
        {
            foreach (var row in rows)
            {
                if (token.IsCancellationRequested)
                    return;

                foreach (var service in row.ServiceSites)
                {
                    if (token.IsCancellationRequested || SiteIcons.Known(service))
                        continue;

                    if (await SiteIcons.ForAsync(service, token) is null || token.IsCancellationRequested)
                        continue;

                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        row.ServiceIcons = ServiceIconsOf(row);
                        Redraw();
                    });
                }

                if (row.Site is not { } site || SiteIcons.Known(site))
                    continue;

                var icon = await SiteIcons.ForAsync(site, token);

                if (icon is null || token.IsCancellationRequested)
                    continue;

                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (token.IsCancellationRequested)
                        return;

                    row.Icon = icon;
                    Redraw();
                });
            }
        }, token);
    }

    private static IReadOnlyList<System.Windows.Media.Imaging.BitmapImage> ServiceIconsOf(ResolverRow row) =>
        row.ServiceSites.Select(SiteIcons.Cached).OfType<System.Windows.Media.Imaging.BitmapImage>().ToList();

    public DnsView()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();
        Unloaded += (_, _) =>
        {
            _work?.Cancel();
            _logos?.Cancel();
        };
    }

    private void Reload()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowChosen(settings);
        ShowSystem();
        ShowAutoRepin(settings);
        ShowPinWatch(settings.AutoRepin, PinRefresh.Watch());

        // Те же провайдеры, что в обзоре, — все, кого sing-box может
        // спрашивать по DoH. Прежде список был своим, из шести, и мерился
        // своей кнопкой: две «Проверить» на одной вкладке, и чем они
        // отличаются, было не понять.
        //
        // Свои — и без DoH тоже: выбрать такой туннелю нельзя, но убрать
        // надо где-то уметь, а спрятанный он остался бы в файле навсегда.
        var shown = DnsSurvey.All
            .Where(p => p.Choosable || p.Own)
            .Select(p => (Group: p.Own ? DnsGroup.Own : p.Group, Row: new ResolverRow
            {
                Name = p.Name,
                Address = p.TlsAddress ?? p.Udp[0],
                Note = p.Note ?? string.Empty,
                Chosen = !settings.UseRouterDns && settings.DnsCryptActive is null && p.Choosable
                    && string.Equals(p.TlsAddress, settings.DnsServer, StringComparison.Ordinal),
                Own = p.Own,
                Choosable = p.Choosable,
                Udp = p.Udp.Count > 0 ? p.Udp[0] : string.Empty,
                Site = LogoSite(p),
                ServiceSites = p.Services ?? [],
            }))
            .ToList();

        // Шифрованные — dnscrypt-proxy на петле (DnsCrypt): обзор их не меряет,
        // это не адрес DoH, а свой движок рядом с sing-box.
        var crypt = settings.DnsCryptActive;

        shown.AddRange(DnsCrypt.Modes.Select(mode =>
        {
            var (title, note) = DnsCrypt.Describe(mode);

            return (Group: DnsGroup.Encrypted, Row: new ResolverRow
            {
                Name = title,
                Address = CryptTag + DnsCrypt.Key(mode),
                Display = $"{DnsCrypt.Address}:{DnsCrypt.Port}",
                Note = note,
                Chosen = crypt == mode,
                Latency = "своим движком",
            });
        }));

        _rows = shown.Select(x => x.Row).ToList();

        // Разделами, как у Zapret GUI: посредники наверху, свои внизу.
        Resolvers.ItemsSource = shown
            .GroupBy(x => x.Group)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var (title, caption) = ResolverGroup.Describe(g.Key);
                // Посредник со своими сервисами (Zapret DNS) — первым, как «сервер проекта» у Zapret GUI.
                return new ResolverGroup
                {
                    Title = title,
                    Caption = caption,
                    Rows = g.OrderBy(x => x.Row.ServiceSites.Count == 0).Select(x => x.Row).ToList(),
                };
            })
            .ToList();

        Status.Text = "Задержку покажет обзор выше.";

        ShowLast();

        StartLogos();
    }

    private void ShowChosen(AppSettings settings)
    {
        var known = DnsSurvey.ByAddress(settings.DnsServer);

        ChosenName.Text = known is null
            ? settings.DnsServer
            : $"{known.Name} · {known.TlsAddress}";

        if (settings.UseRouterDns)
            ChosenName.Text = $"Роутер · {settings.RouterDnsAddress}:53 · UDP/LAN";
        else if (settings.DnsCryptActive is { } crypt)
            ChosenName.Text = $"{DnsCrypt.Describe(crypt).Title} · dnscrypt-proxy {DnsCrypt.Address}:{DnsCrypt.Port}";
        else if (settings.DnsAuto)
            ChosenName.Text += " · автовыбор";
        var fallback = RouterDnsConnection.ConfigWarning(settings);
        if (fallback is not null)
        {
            ChosenName.Text = "Запасной DoH · DNS роутера не применяется в текущем конфиге";
            ShowRouterStatus(fallback, RouterDnsFit.Unknown);
        }
        else if (!settings.UseRouterDns)
        {
            ShowRouterStatus("Перед включением программа проверит роутер: отвечает ли он и не подменяет ли заблокированные имена.", null);
        }
        else if (_lastCheck is { } last && last.Address == settings.RouterDnsAddress)
        {
            ShowRouterStatus(Enabled(last.Check), last.Check.Fit);
        }
        else
        {
            // Включён до этого запуска окна: проверить заново, чтобы предупреждение
            // о подмене не пропадало с перезапуском. Раз на адрес за запуск.
            ShowRouterStatus("Включён. Действует после перезапуска движков.", null);
            _lastCheck = null;
            if (_recheckedFor != settings.RouterDnsAddress)
            {
                _recheckedFor = settings.RouterDnsAddress;
                _ = CheckRouterAsync(settings.RouterDnsAddress, enabled: true);
            }
        }
        RouterDnsSwitch.IsChecked = settings.UseRouterDns;
        RouterDnsIp.Text = settings.RouterDnsAddress;
        TunnelMode.IsEnabled = !settings.UseRouterDns;

        // Автовыбор при DNS роутера хранится, но не действует (RouterDnsSettings.Select).
        AutoDnsSwitch.IsChecked = settings.DnsAuto && !settings.UseRouterDns;

        // Заполняем, не поднимая события выбора: иначе показ состояния
        // тут же записал бы его обратно в настройки и позвал уведомление
        // о перезапуске — при каждом заходе на вкладку.
        _filling = true;
        TunnelMode.SelectedIndex = settings.DnsVia switch
        {
            DnsRoute.Direct => 1,
            DnsRoute.Tunnel => 2,
            _ => 0,
        };
        _filling = false;

        // Цена пути — те же слова, что в мастере первого запуска.
        TunnelHint.Text = DnsRoutes.Explain(settings);
    }

    /// <summary>
    /// Последняя проверка роутера за этот запуск окна — чтобы предупреждение
    /// стояло и при новом заходе на вкладку (вкладка создаётся заново).
    /// </summary>
    private static (string Address, RouterDnsCheck Check)? _lastCheck;

    /// <summary>Адрес, который уже перепроверялся при показе вкладки.</summary>
    private static string? _recheckedFor;

    /// <summary>Подпись под карточкой: жёлтая — предупреждение, красная — отказ.</summary>
    private void ShowRouterStatus(string text, RouterDnsFit? fit)
    {
        RouterDnsStatus.Text = text;

        if (fit is RouterDnsFit.Spoofs or RouterDnsFit.Unknown)
            RouterDnsStatus.SetResourceReference(TextBlock.ForegroundProperty, "Warn");
        else if (fit is RouterDnsFit.Silent)
            RouterDnsStatus.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        else
            RouterDnsStatus.ClearValue(TextBlock.ForegroundProperty);
    }

    /// <summary>Итог проверки для включённого DNS роутера.</summary>
    private static string Enabled(RouterDnsCheck check) => check.Fit switch
    {
        RouterDnsFit.Spoofs => check.Text + " DNS роутера всё равно включён: если сайты под десинком или напрямую перестанут открываться, выключите его.",
        RouterDnsFit.Silent => check.Text + " DNS роутера включён, но сейчас не отвечает.",
        _ => check.Text + " DNS роутера включён — действует после перезапуска движков.",
    };

    /// <summary>Проверить роутер и включить его DNS — с предупреждением, если он подменяет.</summary>
    /// <remarks>
    /// Включение идёт через проверку: роутер, подменяющий закрытые имена,
    /// молча закрыл бы сайты под десинком (замер 15.10 у владельца,
    /// <see cref="RouterDnsConnection.CheckAsync"/>). Не запрещает — только
    /// молчащий роутер (<see cref="RouterDnsCheck.Allows"/>).
    /// </remarks>
    private async Task<bool> SaveRouterDnsAsync()
    {
        if (!RouterDnsSettings.TryNormalize(RouterDnsIp.Text, out var address))
        {
            ShowRouterStatus("Введите адрес роутера в домашней сети, например 192.168.1.1. IPv6, имена и публичные DNS здесь не подходят.", RouterDnsFit.Silent);
            return false;
        }

        var check = await CheckRouterAsync(address, enabled: false);
        if (check is null || !check.Allows)
            return false;

        try
        {
            var settings = RouterDnsSettings.Select(AppSettings.Load(), address);
            settings.Save(AppSettings.DefaultPath);
            ShowChosen(settings);
            Status.Text = check.Warns
                ? "DNS роутера включён с предупреждением — смотрите карточку. Перезапустите движки."
                : "DNS роутера включён. Перезапустите движки.";
            this.Offer("DNS роутера включён");
            return true;
        }
        catch (Exception ex)
        {
            ShowRouterStatus("Не удалось сохранить: " + ex.GetBaseException().Message, RouterDnsFit.Silent);
            return false;
        }
    }

    /// <summary>Проверка роутера с запертыми на её время кнопками; <c>null</c> — сорвалась.</summary>
    /// <param name="enabled">DNS роутера уже включён — подпись говорит о включённом.</param>
    private async Task<RouterDnsCheck?> CheckRouterAsync(string address, bool enabled)
    {
        RouterDnsSwitch.IsEnabled = RouterDnsUse.IsEnabled = RouterDnsTest.IsEnabled = false;
        ShowRouterStatus("Проверяю роутер: отвечает ли он и не подменяет ли заблокированные имена…", null);
        try
        {
            var check = await RouterDnsConnection.CheckAsync(address, CancellationToken.None);
            _lastCheck = (address, check);
            ShowRouterStatus(enabled ? Enabled(check) : check.Allows ? check.Text : check.Text + " DNS роутера не включён.", check.Fit);
            return check;
        }
        catch (Exception ex)
        {
            ShowRouterStatus("Проверка не выполнена: " + ex.GetBaseException().Message, RouterDnsFit.Silent);
            return null;
        }
        finally
        {
            RouterDnsSwitch.IsEnabled = RouterDnsUse.IsEnabled = RouterDnsTest.IsEnabled = true;
        }
    }

    private async void OnRouterDnsUse(object sender, RoutedEventArgs e)
    {
        if (await SaveRouterDnsAsync())
            RouterDnsSwitch.IsChecked = true;
    }

    private async void OnRouterDnsToggle(object sender, RoutedEventArgs e)
    {
        if (RouterDnsSwitch.IsChecked == true)
        {
            // Выключатель встаёт сам, только когда проверка пропустила.
            RouterDnsSwitch.IsChecked = false;
            RouterDnsSwitch.IsChecked = await SaveRouterDnsAsync();
            return;
        }
        try
        {
            var settings = AppSettings.Load() with { UseRouterDns = false };
            settings.Save(AppSettings.DefaultPath);
            ShowChosen(settings);
            ShowRouterStatus($"DNS роутера выключен. Вернулись прежние: {ChosenName.Text}, путь — {DnsRoutes.Word(settings.DnsVia)}. Перезапустите движки.", null);
            this.Offer("DNS роутера выключен");
        }
        catch (Exception ex)
        { RouterDnsSwitch.IsChecked = true; ShowRouterStatus("Не удалось сохранить: " + ex.GetBaseException().Message, RouterDnsFit.Silent); }
    }

    private async void OnRouterDnsTest(object sender, RoutedEventArgs e)
    {
        if (!RouterDnsSettings.TryNormalize(RouterDnsIp.Text, out var address))
        { ShowRouterStatus("Введите адрес роутера в домашней сети, например 192.168.1.1.", RouterDnsFit.Silent); return; }

        var enabled = AppSettings.Load() is { UseRouterDns: true } now && now.RouterDnsAddress == address;
        await CheckRouterAsync(address, enabled);
    }

    /// <summary>Показывает, кого спрашивает сама Windows.</summary>
    private void ShowSystem()
    {
        try
        {
            var found = SystemResolvers.Discover();

            SystemValue.Text = found.Count == 0
                ? "не нашлись"
                : string.Join(", ", found);
        }
        catch (Exception ex)
        {
            SystemValue.Text = "не читаются: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Задержка DoH из обзора — в строку списка выбора.</summary>
    private static void Fill(IReadOnlyList<ResolverRow> rows, DnsSurveyRow result)
    {
        var row = rows.FirstOrDefault(r => r.Address == result.Provider.TlsAddress);

        if (row is null)
            return;

        (row.Badge, row.BadgeKey, row.BadgeTip) = result switch
        {
            { DohMs: null, DohFailure.Length: > 0 } => ("блокируется", "Danger",
                $"DoH к нему у вас не проходит ({result.DohFailure}): туннель через него имён не узнает."),
            { Intercepted: true } or { Spoofed: > 0 } => ("UDP подменяют", "Warn",
                "Открытый DNS к нему у вас перехватывают по дороге. Туннель спрашивает по DoH — ему это не мешает; "
                + "Windows, у которой этот адрес записан резолвером, получает подменённые ответы."),
            _ => ((string?)null, "Danger", (string?)null),
        };

        if (result.DohMs is { } ms)
        {
            row.Latency = $"{ms:0} мс";

            // Порог не про качество связи, а про ощущение: до полусекунды
            // задержка резолвера теряется в открытии страницы, дальше уже
            // заметна на каждом новом имени.
            row.Key = ms < 500 ? "Accent" : "Warn";

            return;
        }

        row.Latency = result.DohFailure.Length > 0 ? result.DohFailure : "не отвечает";
        row.Key = "Danger";
    }
    private void Redraw()
    {
        var shown = Resolvers.ItemsSource;

        Resolvers.ItemsSource = null;
        Resolvers.ItemsSource = shown;
    }

    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string address })
            return;

        if (sender is FrameworkElement { DataContext: ResolverRow { Choosable: false } refused })
        {
            Status.Text = $"{refused.Name} без DoH, а туннель спрашивает только по DoH — выбрать его нельзя. "
                + "Уберите и добавьте заново с именем DoH, если резолвер его умеет.";
            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Шифрованный — режим dnscrypt-proxy, адрес DoH при этом хранится:
            // выбор обычного резолвера вернёт DoH.
            settings = address.StartsWith(CryptTag, StringComparison.Ordinal)
                ? settings with { DnsCrypt = address[CryptTag.Length..], DnsAuto = false, UseRouterDns = false }
                : settings with { DnsServer = address, DnsCrypt = null, DnsAuto = false, UseRouterDns = false };
            settings.Save(AppSettings.DefaultPath);

            ShowChosen(settings);

            foreach (var row in _rows)
                row.Chosen = row.Address == address;

            Redraw();

            var name = _rows.FirstOrDefault(r => r.Address == address)?.Name ?? address;

            Status.Text = $"Апстрим: {name}. Применится при следующем запуске движков.";
            this.Offer($"Резолвер сменён на {name}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Проверяет введённый резолвер обзором и, если он ответил, записывает.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Меряется до записи, а не после: резолвер, не ответивший ничем, в списке
    /// выглядел бы исправным, пока его не выберут и туннель не встанет.
    /// Ответил хоть чем-то — пишется, с тем, что показал замер.
    /// </para>
    /// <para>
    /// Подменяющий по UDP не отвергается: подмену делает оператор по дороге,
    /// а не сам резолвер, и по DoH он может отвечать честно. Но сказано
    /// об этом прямо.
    /// </para>
    /// </remarks>
    private async void OnAddOwn(object sender, RoutedEventArgs e)
    {
        var (provider, problem) = CustomDns.Build(
            OwnName.Text, OwnAddress.Text, OwnDoh.Text, dohPath: null, OwnSecondary.Text);

        OwnStatus.Visibility = Visibility.Visible;

        if (provider is null)
        {
            OwnStatus.Text = problem;
            return;
        }

        OwnAddButton.IsEnabled = false;
        OwnStatus.Text = $"Проверяю {provider.Name}: UDP{(provider.TlsName is null ? string.Empty : ", DoT и DoH")}…";

        try
        {
            var row = (await DnsSurvey.SurveyAllAsync([provider])).Single();

            bool doh = row.DohMs is not null;
            bool udp = row.UdpMs is not null;

            if (!doh && !udp && row.DotMs is null)
            {
                OwnStatus.Text = $"{provider.Name} не ответил ничем: UDP — {Why(row.UdpFailure)}"
                    + (provider.TlsName is null ? string.Empty : $", DoH — {Why(row.DohFailure)}")
                    + ". Не добавлен.";
                return;
            }

            CustomDns.Save(provider);

            var parts = new List<string>();

            if (udp)
                parts.Add($"UDP {row.UdpMs:0} мс");

            if (provider.TlsName is not null)
                parts.Add(doh ? $"DoH {row.DohMs:0} мс" : $"DoH не ответил ({Why(row.DohFailure)})");

            var verdict = provider.TlsName is null
                ? "Для туннеля не годится — нет DoH; в обзоре будет."
                : doh
                    ? "Можно выбрать строкой в списке выше."
                    : "В список попал, но DoH не ответил — туннель через него не заработает, пока DoH молчит.";

            if (row.Spoofed > 0)
                verdict += $" По UDP ответы подменены ({row.Spoofed}/{row.SpoofChecked}) — это оператор по дороге; пользуйтесь DoH.";

            OwnStatus.Text = $"Добавлен {provider.Name}: {string.Join(", ", parts)}. {verdict}";

            OwnName.Clear();
            OwnAddress.Clear();
            OwnSecondary.Clear();
            OwnDoh.Clear();

            Reload();
        }
        catch (Exception ex)
        {
            OwnStatus.Text = "Проверка не удалась: " + ex.GetBaseException().Message;
        }
        finally
        {
            OwnAddButton.IsEnabled = true;
        }
    }

    private static string Why(string failure) => failure.Length > 0 ? failure : "нет ответа";

    private void OnRemoveOwn(object sender, RoutedEventArgs e)
    {
        // Кнопка внутри строки-кнопки: без этого нажатие дошло бы до строки
        // и выбрало бы убираемый резолвер апстримом.
        e.Handled = true;

        if (sender is not Button { Tag: string udp, DataContext: ResolverRow row } || udp.Length == 0)
            return;

        try
        {
            CustomDns.Remove(udp);

            // Выбранный туннелю — остаётся в настройках адресом без имени,
            // и DoH к нему не поднимется. Сказать, а не молчать.
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            Reload();

            Status.Text = row.Chosen || settings.DnsServer == row.Address
                ? $"Убран {row.Name}. Он был выбран туннелю — выберите другой, иначе DoH не поднимется."
                : $"Убран {row.Name}.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось убрать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Включает и выключает автовыбор; включённый сразу запускает обзор.
    /// </summary>
    private void OnAutoDns(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            // При DNS роутера выключатель показан выключенным, хотя автовыбор
            // мог сохраниться включённым: щелчок по нему — «включить», а не
            // переворот сохранённого.
            var next = settings with { DnsAuto = settings.UseRouterDns || !settings.DnsAuto, UseRouterDns = false };

            // Автовыбор меряет только DoH — шифрованный режим он снимает.
            if (next.DnsAuto)
                next = next with { DnsCrypt = null };

            next.Save(AppSettings.DefaultPath);
            ShowChosen(next);

            if (next.DnsAuto)
            {
                Status.Text = "Автовыбор включён — замеряю резолверы…";
                OnSurvey(sender, e);
            }
            else
            {
                Status.Text = "Автовыбор выключен: остаётся " + ChosenName.Text + ".";
            }
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// После обзора, при включённом автовыборе, ставит быстрейший резолвер.
    /// </summary>
    /// <remarks>
    /// Решает библиотека (<see cref="DnsSurvey.Fastest"/>): окно только
    /// записывает выбор и отмечает строку. Тот же — не выбор: без смены
    /// не зовём и перезапуск.
    /// </remarks>
    private void PickFastest(IReadOnlyList<DnsSurveyRow> all, IReadOnlyList<ResolverRow> choices)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        if (!settings.DnsAuto || settings.UseRouterDns)
            return;

        if (DnsSurvey.Fastest(all) is not { TlsAddress: { } address } best)
        {
            Status.Text = "Автовыбор: ни один резолвер для туннеля не ответил по DoH — выбор не менялся.";
            return;
        }

        if (settings.DnsServer == address)
        {
            Status.Text = $"Автовыбор: быстрейший и так стоит — {best.Name}.";
            return;
        }

        var next = settings with { DnsServer = address };
        next.Save(AppSettings.DefaultPath);

        foreach (var row in choices)
            row.Chosen = row.Address == address;

        Redraw();
        ShowChosen(next);

        Status.Text = $"Автовыбор: {best.Name} · {address}. Применится при следующем запуске движков.";
        this.Offer($"Резолвер сменён на {best.Name}");
    }

    /// <summary>
    /// Идёт заполнение списка, а не выбор человека.
    /// </summary>
    /// <remarks>
    /// Список пишет выбор привязкой и поднимает событие при каждом показе
    /// вкладки. Без этой заслонки заход на вкладку записывал бы настройку
    /// обратно и звал уведомление о перезапуске — на ровном месте.
    /// </remarks>
    private bool _filling;

    private void OnThroughTunnel(object sender, RoutedEventArgs e)
    {
        // IsInitialized — от события, которое список поднимает прямо
        // при разборе разметки: соседние элементы к тому мгновению ещё
        // не созданы. На вкладке маршрутов это стоило падения при
        // создании вкладки, и повторять его здесь незачем.
        if (!IsInitialized || _filling || AppSettings.Load().UseRouterDns || sender is not ComboBox box || box.SelectedIndex < 0)
            return;

        try
        {
            if (box.SelectedItem is not ComboBoxItem { Tag: string tag } || !Enum.TryParse<DnsRoute>(tag, out var route))
                return;

            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Выбор того же самого — не выбор.
            if (settings.DnsVia == route)
                return;

            var next = settings with { DnsVia = route };

            next.Save(AppSettings.DefaultPath);

            ShowChosen(next);

            Status.Text = (next.NeedsDnsEngine
                ? "Имена будет разрешать движок без выхода, по DoH."
                : route switch
                {
                    DnsRoute.Tunnel => "Имена будут разрешаться внутри туннеля.",
                    DnsRoute.Direct => "Имена будут разрешаться напрямую.",
                    _ => "Имена — напрямую; откажет DoH — по TCP к тому же резолверу, затем через туннель.",
                }) + " Применится при следующем запуске движков.";

            this.Offer($"DNS: {DnsRoutes.Word(route)}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Обзор резолверов — кто отвечает на самом деле и подменяет ли.
    /// </summary>
    /// <remarks>
    /// Строки появляются по мере готовности: провайдеров пятнадцать, и таблица,
    /// молчащая до последнего, выглядела бы зависшей.
    /// </remarks>
    private async void OnSurvey(object sender, RoutedEventArgs e)
    {
        SurveyButton.IsEnabled = false;
        SurveyHead.Visibility = Visibility.Visible;

        _work?.Cancel();
        _work = new CancellationTokenSource();

        // Один список и для заготовки, и для обзора: строки ищутся по провайдеру.
        var providers = DnsSurvey.All;
        var rows = new System.Collections.ObjectModel.ObservableCollection<SurveyRow>(providers.Select(SurveyRow.Waiting));
        var place = providers.Select((p, i) => (p, i)).ToDictionary(x => (object)x.p, x => x.i, ReferenceEqualityComparer.Instance);
        var done = new List<DnsSurveyRow>();
        Survey.ItemsSource = rows;
        SaySurvey("Проверяю… Запросы идут мимо туннеля, через адаптер.");

        var choices = _rows;

        foreach (var choice in choices)
        {
            choice.Latency = "…";
            choice.Key = "Faint";
        }

        Redraw();

        try
        {
            var progress = new Progress<DnsSurveyRow>(row =>
            {
                if (place.TryGetValue(row.Provider, out var at))
                    rows[at] = SurveyRow.From(row);

                done.Add(row);
                Fill(choices, row);
                Redraw();
            });

            var all = await DnsSurvey.SurveyAllAsync(providers, progress, _work.Token);

            // Порядок — как в списке провайдеров, а не как пришли ответы.
            Survey.ItemsSource = all.Select(SurveyRow.From).ToList();

            PickFastest(all, choices);

            var intercepted = all.Where(r => r.Spoofed > 0).Select(r => r.Provider.Name).ToList();

            SaySurvey(intercepted.Count == 0
                ? "Подмены по UDP не найдено."
                : $"По UDP подменяют ответы: {string.Join(", ", intercepted)}. "
                  + "Обычный DNS к ним перехвачен по дороге — пользуйтесь DoH или DoT.");

            _last = new LastSurvey(all, SurveyStatus.Text, DateTime.Now);
        }
        catch (OperationCanceledException)
        {
            SaySurvey("Обзор прерван.");

            // Прерван уходом с вкладки — что успели, то и запомнили.
            if (done.Count > 0)
                _last = new LastSurvey(done.ToList(), $"Обзор прерван: проверено {done.Count} из {DnsSurvey.All.Count}.", DateTime.Now);
        }
        catch (Exception ex)
        {
            SaySurvey("Обзор не удался: " + ex.GetBaseException().Message);

            // В три строки карточки длинное сообщение исключения не влезает.
            Journal.Write("DNS", "обзор резолверов не удался: " + ex.GetBaseException().Message);
        }
        finally
        {
            SurveyButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Проверяет адреса пинов и меняет умершие на те, что посредники отдают сейчас.
    /// </summary>
    /// <remarks>
    /// Не прерывается уходом с вкладки: посреди неё может идти запись hosts,
    /// а итог всё равно ляжет в журнал. Токен поэтому не раздела, а пустой.
    /// </remarks>
    private async void OnPins(object sender, RoutedEventArgs e)
    {
        PinsButton.IsEnabled = false;

        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool engines = state is not null && state.IsSupervisorAlive();

        SayPins("Проверяю: по одному соединению на первый адрес каждого имени и вопрос посредникам…");

        try
        {
            // Кнопка — тоже проверка: плановая после неё начнёт отсчёт заново
            // (отметку ставит сама проверка, под своим замком).
            var result = await Task.Run(() => PinRefresh.RunAsync(CancellationToken.None, byHand: true));

            PinRefreshWatch.Write("кнопкой", result);

            SayPins(Describe(result)
                + (engines ? string.Empty : " Движки не подняты — часть посредников без десинка не отвечает, и мёртвыми могли показаться живые."));
        }
        catch (Exception ex)
        {
            SayPins("Проверка не удалась: " + ex.GetBaseException().Message);
            Journal.Write("пин", "проверка адресов не удалась: " + ex.GetBaseException().Message);
        }
        finally
        {
            PinsButton.IsEnabled = true;
            ShowPinWatch(AppSettings.Load(AppSettings.DefaultPath).AutoRepin, PinRefresh.Watch());
        }
    }

    /// <summary>
    /// Итог в поле постоянной высоты: не влезший в три строки виден подсказкой целиком
    /// (и лежит в журнале — <see cref="PinRefreshWatch.Write"/>).
    /// </summary>
    private void SayPins(string text)
    {
        PinsStatus.Text = text;
        PinsStatus.ToolTip = text;
    }

    /// <summary>
    /// Итог обзора — в поле постоянной высоты, целиком — подсказкой.
    /// </summary>
    /// <remarks>
    /// Поле прежде росло от строки «Проверяю…» до трёх строк итога, и карточку не
    /// раздвигало лишь потому, что соседняя в том же ряду выше, — защита держалась
    /// на чужой высоте.
    /// </remarks>
    private void SaySurvey(string text)
    {
        SurveyStatus.Text = text;
        SurveyStatus.ToolTip = text;
    }

    /// <summary>Строки состояния автозамены: когда проверялось, кто под подозрением и под присмотром.</summary>
    private void ShowPinWatch(bool auto, PinWatch watch)
    {
        PinsWhen.Text = WhenLine(watch, auto, DateTimeOffset.Now);
        // Строка режется многоточием на узком окне — подсказка несёт её целиком.
        PinsWhen.ToolTip = PinsWhen.Text + (watch.Suspects.Count > 0
            ? "\n\nНе ответили в прошлой проверке: " + string.Join(", ", watch.Suspects)
              + ". Промолчат и в следующей — пин заменится живым адресом."
            : string.Empty);

        PinsWatched.Text = WatchedLine(watch);
        PinsWatched.ToolTip = PinsWatched.Text;
    }

    /// <summary>«Проверено в 14:05, следующая около 20:05 · под подозрением: 2 (…)».</summary>
    internal static string WhenLine(PinWatch watch, bool auto, DateTimeOffset now)
    {
        string text;

        if (watch.Last is not { } last)
        {
            text = auto
                ? "Ещё не проверялось — первая проверка, когда движки проработают 10 минут"
                : "Ещё не проверялось, автозамена выключена";
        }
        else
        {
            var local = last.ToLocalTime();
            var today = now.ToLocalTime().Date;
            string when = local.Date == today ? $"в {local:HH:mm}" : $"{local:dd.MM} в {local:HH:mm}";

            if (!auto)
            {
                text = $"Проверено {when}, автозамена выключена";
            }
            else
            {
                // Сторож проверяет только при поднятых движках и раз в 5 минут: «около»,
                // а просроченная ждёт движков, а не названного часа.
                var next = (last + PinRefresh.Every).ToLocalTime();
                text = next <= now
                    ? $"Проверено {when}, следующая — как только движки поработают 10 минут"
                    : $"Проверено {when}, следующая около {(next.Date == today ? string.Empty : $"{next:dd.MM} ")}{next:HH:mm}";
            }
        }

        if (watch.Suspects.Count > 0)
        {
            text += $" · под подозрением: {watch.Suspects.Count} ({string.Join(", ", watch.Suspects.Take(3))}"
                + (watch.Suspects.Count > 3 ? $" и ещё {watch.Suspects.Count - 3})" : ")");
        }

        return text;
    }

    /// <summary>«Под присмотром: Comss DNS 30, Zapret DNS 21, без посредника 1».</summary>
    internal static string WatchedLine(PinWatch watch) =>
        watch.Groups.Count == 0
            ? "Пинов к посредникам в hosts нет — присматривать не за чем"
            : "Под присмотром: " + string.Join(", ", watch.Groups.Select(g => $"{g.Intermediary ?? "без посредника"} {g.Count}"));

    private void ShowAutoRepin(AppSettings settings)
    {
        AutoRepinSwitch.IsChecked = settings.AutoRepin;
        AutoRepinWord.Text = settings.AutoRepin ? "вкл." : "выкл.";
    }

    /// <summary>Выключатель автозамены раз в 6 часов; сторож читает его на каждом круге.</summary>
    private void OnAutoRepin(object sender, RoutedEventArgs e)
    {
        bool on = AutoRepinSwitch.IsChecked == true;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { AutoRepin = on };
            settings.Save(AppSettings.DefaultPath);
            ShowAutoRepin(settings);
            ShowPinWatch(on, PinRefresh.Watch());

            SayPins(on
                ? "Мёртвые пины будут меняться сами раз в 6 часов, пока движки работают."
                : "Сама программа пины больше не меняет — только кнопкой «Проверить».");
        }
        catch (Exception ex)
        {
            ShowAutoRepin(AppSettings.Load(AppSettings.DefaultPath));
            SayPins("Настройка не сохранилась: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Итог проверки словами — что сделано и чего не сделано.</summary>
    internal static string Describe(PinRefreshResult result)
    {
        if (result.Checked == 0 && result.Error is null)
            return "В hosts нет пинов к адресам посредников — проверять нечего.";

        var parts = new List<string>();

        if (result.Error is { } error)
            parts.Add($"Ничего не заменено: {error}.");

        if (result.Changes.Count > 0)
            parts.Add($"Заменено: {string.Join("; ", result.Changes)}.");

        if (result.Moved.Count > 0)
            parts.Add($"Посредник переехал: {string.Join("; ", result.Moved)}.");

        if (result.Reordered.Count > 0)
            parts.Add($"Живой адрес поставлен первым: {string.Join("; ", result.Reordered)}.");

        if (result.Dead.Count > 0)
            parts.Add($"Живой замены не нашлось: {string.Join(", ", result.Dead.Take(6))}"
                + (result.Dead.Count > 6 ? $" и ещё {result.Dead.Count - 6}." : "."));

        if (result.Silent.Count > 0)
            parts.Add($"Не ответили: {string.Join(", ", result.Silent)}.");

        if (result.Changes.Count == 0 && result.Moved.Count == 0 && result.Reordered.Count == 0
            && result.Dead.Count == 0 && result.Error is null)
        {
            parts.Add($"Проверено адресов: {result.Checked}, все отвечают — менять нечего.");
        }

        return string.Join(" ", parts);
    }
}
