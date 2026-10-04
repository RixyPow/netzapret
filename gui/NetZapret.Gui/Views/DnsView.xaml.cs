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
    public required string Address { get; init; }
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
}

/// <summary>Строка обзора резолверов.</summary>
/// <remarks>
/// Цвета — по смыслу, как на снимке владельца: зелёное прошло, красное
/// перехвачено или подменено, серое не мерилось.
/// </remarks>
public sealed class SurveyRow
{
    private SurveyRow(DnsSurveyRow row) => Source = row;

    public DnsSurveyRow Source { get; }

    public string Name => Source.Provider.Name;

    public string Doh => Ms(Source.DohMs, Source.DohFailure);

    public string Dot => Ms(Source.DotMs, Source.DotFailure);

    public string Udp
    {
        get
        {
            var text = Ms(Source.UdpMs, Source.UdpFailure);

            return Source.UdpMs is not null && Source.UdpAnswered < Source.Provider.Udp.Count
                ? $"{text} {Source.UdpAnswered}/{Source.Provider.Udp.Count}"
                : text;
        }
    }

    public string Real => Source.RealResolver is null
        ? "—"
        : $"{Source.RealResolver} → {Source.RealNetwork ?? "?"}";

    public string Spoof => Source.SpoofChecked == 0 ? "—" : $"{Source.Spoofed}/{Source.SpoofChecked}";

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

        var choices = Resolvers.ItemsSource as IReadOnlyList<ResolverRow> ?? [];

        foreach (var row in last.Rows)
            Fill(choices, row);

        Survey.ItemsSource = last.Rows.Select(SurveyRow.From).ToList();
        SurveyHead.Visibility = Visibility.Visible;
        SurveyStatus.Text = $"{last.Status} Проверено в {last.At:HH:mm}.";
        Status.Text = $"Задержка — из обзора в {last.At:HH:mm}.";

        Redraw();
    }

    private CancellationTokenSource? _logos;

    /// <summary>
    /// Сайт, чей значок — логотип резолвера.
    /// </summary>
    /// <remarks>
    /// Значок берётся у самого сайта (SiteIcons), как у сервисов в «Маршрутах».
    /// Имя DoH для этого годится не всегда: у dns.quad9.net и
    /// common.dot.dns.yandex.net значка нет, он на главном сайте. Неизвестным
    /// и своим — по имени DoH без первой части; нет и его — буква.
    /// </remarks>
    private static string? LogoSite(DnsProvider provider)
    {
        var name = provider.Name;

        if (name.StartsWith("Google", StringComparison.OrdinalIgnoreCase)) return "dns.google";
        if (name.StartsWith("Cloudflare", StringComparison.OrdinalIgnoreCase)) return "one.one.one.one";
        if (name.StartsWith("Quad9", StringComparison.OrdinalIgnoreCase)) return "quad9.net";
        if (name.StartsWith("AdGuard", StringComparison.OrdinalIgnoreCase)) return "adguard-dns.io";
        if (name.StartsWith("Yandex", StringComparison.OrdinalIgnoreCase)) return "dns.yandex.ru";
        if (name.StartsWith("OpenDNS", StringComparison.OrdinalIgnoreCase)) return "opendns.com";
        if (name.StartsWith("CleanBrowsing", StringComparison.OrdinalIgnoreCase)) return "cleanbrowsing.org";
        if (name.StartsWith("Mullvad", StringComparison.OrdinalIgnoreCase)) return "mullvad.net";
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
        var rows = Resolvers.ItemsSource as IReadOnlyList<ResolverRow> ?? [];

        foreach (var row in rows)
            row.Icon = row.Site is { } site ? SiteIcons.Cached(site) : null;

        Redraw();

        _ = Task.Run(async () =>
        {
            foreach (var row in rows)
            {
                if (token.IsCancellationRequested)
                    return;

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

        // Те же провайдеры, что в обзоре, — все, кого sing-box может
        // спрашивать по DoH. Прежде список был своим, из шести, и мерился
        // своей кнопкой: две «Проверить» на одной вкладке, и чем они
        // отличаются, было не понять.
        //
        // Свои — и без DoH тоже: выбрать такой туннелю нельзя, но убрать
        // надо где-то уметь, а спрятанный он остался бы в файле навсегда.
        Resolvers.ItemsSource = DnsSurvey.All
            .Where(p => p.Choosable || p.Own)
            .Select(p => new ResolverRow
            {
                Name = p.Name,
                Address = p.TlsAddress ?? p.Udp[0],
                Note = p.Note ?? string.Empty,
                Chosen = p.Choosable && string.Equals(p.TlsAddress, settings.DnsServer, StringComparison.Ordinal),
                Own = p.Own,
                Choosable = p.Choosable,
                Udp = p.Udp.Count > 0 ? p.Udp[0] : string.Empty,
                Site = LogoSite(p),
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

        if (settings.DnsAuto)
            ChosenName.Text += " · автовыбор";

        AutoDnsSwitch.IsChecked = settings.DnsAuto;

        // Заполняем, не поднимая события выбора: иначе показ состояния
        // тут же записал бы его обратно в настройки и позвал уведомление
        // о перезапуске — при каждом заходе на вкладку.
        _filling = true;
        TunnelMode.SelectedIndex = settings.DnsThroughTunnel ? 1 : 0;
        _filling = false;

        // Обе стороны выбора имеют цену, и названа она честно: включённое
        // прячет запрос от оператора, но ставит разрешение имён в зависимость
        // от туннеля — пока тот не поднялся, не открывается ничего.
        // Без туннеля «через туннель» значит «через движок»: туннеля нет,
        // а запросы Windows ловит sing-box без выхода (AppSettings.NeedsDnsEngine).
        TunnelHint.Text = !settings.NeedsProxy && settings.NeedsDesync
            ? settings.DnsThroughTunnel
                ? "Туннель не поднимается, поэтому запросы Windows забирает движок без выхода "
                  + "и спрашивает выбранный резолвер по DoH напрямую. Так открытый DNS, который "
                  + "часть операторов подменяет, до программ не доходит. VPN при этом не работает."
                : "Туннель не поднимается, и имена разрешает сама Windows обычным DNS. У части "
                  + "операторов его подменяют — тогда YouTube и другие сайты не находятся вовсе, "
                  + "и десинку нечего чинить. «Через туннель» заведёт запросы в движок без VPN."
            : settings.DnsThroughTunnel
                ? "Запросы идут внутри туннеля: оператору они неотличимы от прочего трафика "
                  + "и заблокировать их отдельно нельзя. Цена — пока туннель не поднялся, имена "
                  + "не разрешаются вовсе, включая то, что работало на десинке."
                : "Запросы идут напрямую к выбранному резолверу. Если оператор перекроет DoH — "
                  + "а в августе 2026 это сделали несколько российских, — включайте «через туннель».";
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
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { DnsServer = address, DnsAuto = false };
            settings.Save(AppSettings.DefaultPath);

            ShowChosen(settings);

            if (Resolvers.ItemsSource is IEnumerable<ResolverRow> rows)
            {
                foreach (var row in rows)
                    row.Chosen = row.Address == address;

                Redraw();
            }

            Status.Text = $"Апстрим: {address}. Применится при следующем запуске движков.";
            this.Offer($"Резолвер сменён на {address}");
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
            var next = settings with { DnsAuto = !settings.DnsAuto };

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

        if (!settings.DnsAuto)
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
        if (!IsInitialized || _filling || sender is not ComboBox box || box.SelectedIndex < 0)
            return;

        try
        {
            bool through = box.SelectedIndex == 1;
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Выбор того же самого — не выбор.
            if (settings.DnsThroughTunnel == through)
                return;

            var next = settings with { DnsThroughTunnel = through };

            next.Save(AppSettings.DefaultPath);

            ShowChosen(next);

            Status.Text = next.NeedsDnsEngine
                ? "Имена будет разрешать движок без выхода, по DoH. Применится при следующем запуске движков."
                : next.DnsThroughTunnel
                    ? "Имена будут разрешаться внутри туннеля. Применится при следующем запуске движков."
                    : "Имена будут разрешаться напрямую. Применится при следующем запуске движков.";

            this.Offer(next.DnsThroughTunnel
                ? "DNS переведён внутрь туннеля"
                : "DNS переведён на прямой путь");
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

        var rows = new System.Collections.ObjectModel.ObservableCollection<SurveyRow>();
        var done = new List<DnsSurveyRow>();
        Survey.ItemsSource = rows;
        SurveyStatus.Text = "Проверяю… Запросы идут мимо туннеля, через адаптер.";

        var choices = Resolvers.ItemsSource as IReadOnlyList<ResolverRow> ?? [];

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
                rows.Add(SurveyRow.From(row));
                done.Add(row);
                Fill(choices, row);
                Redraw();
            });

            var all = await DnsSurvey.SurveyAllAsync(progress: progress, cancellationToken: _work.Token);

            // Порядок — как в списке провайдеров, а не как пришли ответы.
            Survey.ItemsSource = all.Select(SurveyRow.From).ToList();

            PickFastest(all, choices);

            var intercepted = all.Where(r => r.Spoofed > 0).Select(r => r.Provider.Name).ToList();

            SurveyStatus.Text = intercepted.Count == 0
                ? "Подмены по UDP не найдено."
                : $"По UDP подменяют ответы: {string.Join(", ", intercepted)}. "
                  + "Обычный DNS к ним перехвачен по дороге — пользуйтесь DoH или DoT.";

            _last = new LastSurvey(all, SurveyStatus.Text, DateTime.Now);
        }
        catch (OperationCanceledException)
        {
            SurveyStatus.Text = "Обзор прерван.";

            // Прерван уходом с вкладки — что успели, то и запомнили.
            if (done.Count > 0)
                _last = new LastSurvey(done.ToList(), $"Обзор прерван: проверено {done.Count} из {DnsSurvey.All.Count}.", DateTime.Now);
        }
        catch (Exception ex)
        {
            SurveyStatus.Text = "Обзор не удался: " + ex.GetBaseException().Message;
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

        PinsStatus.Text = "Проверяю: по одному соединению на каждый прибитый адрес…";

        try
        {
            var result = await Task.Run(() => PinRefresh.RunAsync(CancellationToken.None));

            if (result.Changes.Count > 0)
                Journal.Write("пин", "адрес умер, заменён живым: " + string.Join("; ", result.Changes)
                    + (result.Backup is null ? string.Empty : $"; копия hosts: {result.Backup}"));

            if (result.Dead.Count > 0)
                Journal.Write("пин", $"мёртвые без живой замены: {string.Join(", ", result.Dead.Take(8))}"
                    + (result.Dead.Count > 8 ? $" и ещё {result.Dead.Count - 8}" : string.Empty));

            if (result.Error is { } error)
                Journal.Write("пин", "проверка адресов: " + error);

            PinsStatus.Text = Describe(result)
                + (engines ? string.Empty : " Движки не подняты — часть посредников без десинка не отвечает, и мёртвыми могли показаться живые.");
        }
        catch (Exception ex)
        {
            PinsStatus.Text = "Проверка не удалась: " + ex.GetBaseException().Message;
            Journal.Write("пин", "проверка адресов не удалась: " + ex.GetBaseException().Message);
        }
        finally
        {
            PinsButton.IsEnabled = true;
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

        if (result.Dead.Count > 0)
            parts.Add($"Живой замены не нашлось: {string.Join(", ", result.Dead.Take(6))}"
                + (result.Dead.Count > 6 ? $" и ещё {result.Dead.Count - 6}." : "."));

        if (parts.Count == 0)
            parts.Add($"Проверено адресов: {result.Checked}, все отвечают — менять нечего.");

        return string.Join(" ", parts);
    }
}
