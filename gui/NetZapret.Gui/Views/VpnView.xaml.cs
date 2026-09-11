using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>Строка одного сервера подписки.</summary>
public sealed record ServerRow(
    string Tag,
    string Owner,
    string Name,
    string Country,
    Visibility CountryShown,
    BitmapImage? Flag,
    Visibility FlagShown,
    string Detail,
    string Latency,
    Brush Color,
    string ChooseLabel,
    bool CanChoose,

    /// <summary>Можно ли замерить его отдельным пробником.</summary>
    bool Measurable);

/// <summary>Папка одной подписки.</summary>
public sealed class SubRow
{
    public required SubscriptionEntry Entry { get; init; }

    public string Name => Entry.Name;

    public bool Open { get; set; }

    public bool Active { get; set; }

    public string Detail { get; set; } = "читаю…";

    public IReadOnlyList<ServerRow> Servers { get; set; } = [];

    /// <summary>Серверы в том порядке, в каком их дала подписка.</summary>
    /// <remarks>
    /// Хранится отдельно затем, что сортировка по задержке прежде писалась
    /// поверх исходного списка. Порядок подписки терялся после первого же
    /// нажатия, и «По порядку» возвращало тот же отсортированный список —
    /// кнопка работала ровно один раз.
    /// </remarks>
    public IReadOnlyList<ServerRow> AsGiven { get; set; } = [];

    public Visibility ServersShown => Open && Servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public string Chevron => Open ? "▼" : "►";

    public Brush Edge => (Brush)Application.Current.FindResource(Active ? "Accent" : "Border");

    public Visibility ActiveShown => Active ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ActivateShown => Active ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>
/// Вытаскивает код страны из названия сервера.
/// </summary>
/// <remarks>
/// <para>
/// Панели подписок ставят в начало тега флаг эмодзи — пару региональных
/// букв вроде U+1F1E9 U+1F1EA для Германии. Windows их не рисует: в Segoe UI
/// Emoji флагов стран нет вовсе, и на экране получается «de», влипшее
/// в название. Это не наша оплошность и шрифтом не лечится — Microsoft
/// не поставляет флаги намеренно.
/// </para>
/// <para>
/// Раз нарисовать нельзя, обходимся тем, что есть: пара превращается
/// в обычные заглавные буквы и выносится в значок рядом. Опознавательный
/// знак вместо опечатки.
/// </para>
/// </remarks>
public static class CountryTag
{
    /// <summary>Первая и последняя региональные буквы.</summary>
    private const int FirstIndicator = 0x1F1E6;
    private const int LastIndicator = 0x1F1FF;

    /// <summary>Делит тег на код страны и остальное имя.</summary>
    public static (string Country, string Name) Split(string tag)
    {
        var letters = new List<char>();
        int i = 0;

        while (i < tag.Length)
        {
            if (!char.IsHighSurrogate(tag[i]) || i + 1 >= tag.Length)
                break;

            int code = char.ConvertToUtf32(tag[i], tag[i + 1]);

            if (code is < FirstIndicator or > LastIndicator)
                break;

            letters.Add((char)('A' + code - FirstIndicator));
            i += 2;
        }

        // Пара, а не одна буква: одиночная региональная буква кодом страны
        // не является, и показывать её значком означало бы выдумать страну.
        return letters.Count == 2
            ? (new string(letters.ToArray()), tag[i..].Trim())
            : (string.Empty, tag.Trim());
    }
}

/// <summary>
/// Подписки, их серверы и выключатель туннеля.
/// </summary>
/// <remarks>
/// <para>
/// Подписок может быть несколько, и каждая — своя папка со своими серверами,
/// квотой и сроком. Но действует одна: конфиг sing-box собирается по одной
/// ссылке из <see cref="AppSettings.SubscriptionUrl"/>, и это не наше
/// ограничение показа, а устройство движка. Поэтому выбор сервера из соседней
/// подписки делает действующей её — иначе конфиг собрался бы без этого
/// сервера, а окно уверяло бы, что он выбран.
/// </para>
/// <para>
/// Замеры хранятся в том же <see cref="ServerHealthCache"/>, который читает
/// консоль, — окно и меню показывают одни цифры.
/// </para>
/// <para>
/// Ссылки не показываются и не пишутся в журнал: они равнозначны паролю.
/// </para>
/// </remarks>
public partial class VpnView : UserControl
{
    private readonly ServerHealthCache _health = ServerHealthCache.Load();

    private SubscriptionBook _book = new();
    private List<SubRow> _rows = [];
    private CancellationTokenSource? _work;

    /// <summary>Сортировать по задержке, а не по порядку подписки.</summary>
    private bool _byLatency;

    public VpnView()
    {
        InitializeComponent();

        Loaded += async (_, _) => await LoadAsync();
        Unloaded += (_, _) => _work?.Cancel();
    }

    private async Task LoadAsync()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowPower(settings);

        _book = SubscriptionBook.Load();
        var active = _book.Active(settings);

        // Перенос старой строки WARP в выключатель мог поправить настройки —
        // перечитываем, иначе карточка покажет состояние до переноса.
        settings = AppSettings.Load(AppSettings.DefaultPath);
        ShowWarp(settings);

        _rows = _book.Entries
            .Select(entry => new SubRow
            {
                Entry = entry,
                Open = entry.Open,
                Active = active is not null && ReferenceEquals(entry, active),
            })
            .ToList();

        Subscriptions.ItemsSource = _rows;

        if (_rows.Count == 0)
        {
            Status.Text = "Подписок нет. Без них серверов нет, а VPN недоступен — "
                + "десинк при этом работает.";

            return;
        }

        Status.Text = "Читаю подписки…";
        RefreshButton.IsEnabled = false;

        try
        {
            // По очереди, а не разом: панели подписок нередко одна и та же,
            // и три запроса в одну секунду с одного адреса ей не нравятся.
            foreach (var row in _rows)
                await FillAsync(row, settings);

            int servers = _rows.Sum(r => r.Servers.Count);

            Status.Text = servers == 0
                ? "Ни одна подписка не отдала серверов."
                : $"Подписок: {_rows.Count}, серверов: {servers}.";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            Redraw();
        }
    }

    /// <summary>Читает одну подписку и заполняет её папку.</summary>
    private async Task FillAsync(SubRow row, AppSettings settings)
    {
        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(row.Entry.Url), CancellationToken.None);

            var usable = info.Servers.Where(s => s.IsSupportedBySingBox).ToList();

            row.AsGiven = Rows(usable, row.Entry.Name, settings);
            row.Servers = InChosenOrder(row.AsGiven);

            // Отброшенные называются числом, а не замалчиваются: человек,
            // видящий в подписке двадцать серверов и пятнадцать здесь,
            // вправе знать, куда делись пять.
            int skipped = info.Servers.Count - usable.Count;

            var parts = new List<string> { $"{usable.Count} серверов" };

            if (skipped > 0)
                parts.Add($"ещё {skipped} sing-box не поддерживает");

            parts.Add(info.RemainingBytes is { } left
                ? Size(left)
                : info.TotalBytes > 0 ? "квота кончилась" : "без ограничения");

            parts.Add(info.ExpiresAt is { } until
                ? $"{(until - DateTimeOffset.Now).Days} дн"
                : "без срока");

            row.Detail = string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            row.AsGiven = [];
            row.Servers = [];
            row.Detail = "не прочиталась: " + ex.GetBaseException().Message;
        }

        Redraw();
    }

    private IReadOnlyList<ServerRow> Rows(
        IReadOnlyList<ProxyServer> servers,
        string owner,
        AppSettings settings)
    {
        var rows = servers.Select(server =>
        {
            var known = _health.Find(server.Tag);
            bool chosen = server.Tag == settings.PreferredServer;

            // Незамеряемые выходы не притворяются замеренными. Пробник поднимает
            // свой движок, а учётная запись MASQUE лежит в кэше работающего,
            // и файл занят им же — пробник обязан регистрироваться заново,
            // а через что, ему взять негде. Подписать такое «не отвечает»
            // значило бы выдать особенность замера за свойство сервера.
            var (latency, key) = !server.IsMeasurable
                ? ("только в работе", "Faint")
                : known switch
                {
                    { Success: true, LatencyMs: { } ms } => ($"{ms:0} мс", "Accent"),
                    { Success: true } => ("отвечает", "Accent"),
                    { Success: false } => ("не отвечает", "Danger"),
                    _ => ("не замерян", "Faint"),
                };

            var detail = $"{server.Protocol}, {server.Host}:{server.Port}";

            if (known is not null)
                detail += $" · замер {Ago(known.CheckedAt)}";

            var (country, name) = CountryTag.Split(server.Tag);
            var flag = country.Length == 2 ? FlagImages.For(country) : null;

            return new ServerRow(
                server.Tag,
                owner,
                name,
                country,

                // Ровно одно из двух: картинка либо буквы. Показать оба
                // значило бы сказать одно и то же дважды в одной строке.
                country.Length == 0 || flag is not null ? Visibility.Collapsed : Visibility.Visible,
                flag,
                flag is null ? Visibility.Collapsed : Visibility.Visible,
                detail,
                latency,
                (Brush)FindResource(chosen ? "Accent" : key),
                chosen ? "выбран" : "выбрать",
                !chosen,
                server.IsMeasurable);
        });

        // Сортировка здесь не применяется: список отдаётся в порядке подписки,
        // а порядок показа выбирается в Reshow. Иначе исходный порядок негде
        // было бы взять обратно.
        return rows.ToList();
    }

    /// <summary>
    /// Раскладывает серверы в том порядке, который выбран кнопкой.
    /// </summary>
    /// <remarks>
    /// Незамеренные идут после отвечающих, но раньше молчащих: про них мы
    /// ничего не знаем, и ставить их в конец, к заведомо мёртвым, значило бы
    /// приписать им приговор, которого не выносили.
    /// </remarks>
    private IReadOnlyList<ServerRow> InChosenOrder(IReadOnlyList<ServerRow> rows) =>
        _byLatency ? rows.OrderBy(Rank).ThenBy(Ms).ToList() : rows;

    private int Rank(ServerRow row) => _health.Find(row.Tag) switch
    {
        { Success: true } => 0,
        null => 1,
        _ => 2,
    };

    private double Ms(ServerRow row) => _health.Find(row.Tag)?.LatencyMs ?? double.MaxValue;

    private void Redraw()
    {
        Subscriptions.ItemsSource = null;
        Subscriptions.ItemsSource = _rows;
    }

    /// <summary>Пересобирает строки серверов, не перечитывая подписки.</summary>
    private void Reshow()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var active = _book.Active(settings);

        // Выходы WARP живут в карточке, а не в списке, и общий обход строк
        // их не касается — пересобираем отдельно, иначе замер по ним виден
        // только после перезахода на вкладку.
        ShowWarp(settings);

        foreach (var row in _rows)
        {
            row.Active = active is not null && ReferenceEquals(row.Entry, active);

            // Раскладка считается от исходного порядка, а не от показанного:
            // иначе она накапливается сама на себе и вернуться некуда.
            row.Servers = InChosenOrder(row.AsGiven)
                .Select(server => server with
                {
                    Color = (Brush)FindResource(
                        server.Tag == settings.PreferredServer
                            ? "Accent"
                            : server.Measurable ? Key(server.Tag) : "Faint"),
                    ChooseLabel = server.Tag == settings.PreferredServer ? "выбран" : "выбрать",
                    CanChoose = server.Tag != settings.PreferredServer,
                    Latency = server.Measurable ? Latency(server.Tag) : "только в работе",
                })
                .ToList();
        }

        Redraw();
    }

    private string Key(string tag) => _health.Find(tag) switch
    {
        { Success: true } => "Accent",
        { Success: false } => "Danger",
        _ => "Faint",
    };

    private string Latency(string tag) => _health.Find(tag) switch
    {
        { Success: true, LatencyMs: { } ms } => $"{ms:0} мс",
        { Success: true } => "отвечает",
        { Success: false } => "не отвечает",
        _ => "не замерян",
    };

    /// <summary>
    /// Показывает, поднимается ли туннель в нынешнем режиме.
    /// </summary>
    /// <remarks>
    /// Отдельного признака «VPN включён» в настройках нет — он выводится
    /// из режима, и заводить второй источник правды ради кнопки нельзя:
    /// они разошлись бы в первый же день.
    /// </remarks>
    private void ShowPower(AppSettings settings)
    {
        bool on = settings.NeedsProxy;

        Dot.Fill = (Brush)FindResource(on ? "Accent" : "Faint");
        PowerLine.Text = on ? "VPN включён" : "VPN выключен";
        PowerButton.Content = on ? "Выключить VPN" : "Включить VPN";

        PowerHint.Text = on
            ? $"Режим «{settings.DescribeMode()}»: туннель поднимается."
            : settings.Mode == OperatingMode.Off
                ? "Режим «выключено»: не поднимается ничего, включая десинк."
                : "Режим «только десинк»: туннель не поднимается, пакеты правятся на лету.";

        ShowPick(settings);
    }

    /// <summary>
    /// Показывает, как выбирается выход, и обе ручки к этому выбору.
    /// </summary>
    /// <remarks>
    /// Возврат к автоподбору был невозможен вовсе: закрепить сервер кнопка
    /// «выбрать» умела, а снять закрепление — ничто, кроме удаления подписки
    /// целиком. Настройка про отбор кандидатов при этом жила в «Ещё», среди
    /// выключателей журнала и обновлений, где её находил только тот, кто знал,
    /// что она есть.
    /// </remarks>
    private void ShowPick(AppSettings settings)
    {
        var pinned = settings.PreferredServer;

        PickLine.Text = string.IsNullOrWhiteSpace(pinned)
            ? "Автоподбор: движок сам опрашивает серверы и берёт быстрейший из живых. "
              + "Мёртвый выход не выбирается — этим автоподбор и отличается от «первого по списку»."
            : $"Закреплён вручную: {pinned}. Автоподбор не применяется, "
              + "даже если этот сервер перестанет отвечать.";

        AutoButton.Visibility = string.IsNullOrWhiteSpace(pinned)
            ? Visibility.Collapsed
            : Visibility.Visible;

        ForeignButton.Content = settings.ForeignExitsOnly ? "включено" : "выключено";

        ForeignButton.Foreground = (Brush)FindResource(
            settings.ForeignExitsOnly ? "Accent" : "Muted");
    }

    /// <summary>
    /// Показывает состояние выключателя WARP и его выходы.
    /// </summary>
    /// <remarks>
    /// Выключатель, а не строка списка: выходы WARP подмешиваются к серверам
    /// действующей подписки. Отдельной подпиской он занимал её место — выбор
    /// его выхода делал действующим его и отключал рабочий VPN целиком.
    /// </remarks>
    private void ShowWarp(AppSettings settings)
    {
        bool on = settings.WarpEnabled;
        var account = WarpAccount.Load();

        WarpButton.Content = on ? "включён" : "выключен";
        WarpButton.Foreground = (Brush)FindResource(on ? "Accent" : "Muted");

        WarpLine.Text = on
            ? account is null
                ? "Добавлен к подписке. Ключей WireGuard нет — работает только MASQUE."
                : $"Добавлен к подписке. Ключи заведены {account.RegisteredAt:d MMMM yyyy}, "
                  + $"адрес внутри сети {account.AddressV4}."
            : account is null
                ? "Выключен. При включении ключи заводятся на месте — ни почты, ни оплаты."
                : $"Выключен. Ключи заведены {account.RegisteredAt:d MMMM yyyy} и сохранены.";

        WarpExits.ItemsSource = on
            ? Rows(WarpAccount.Exits(), WarpOwner, settings)
            : null;

        WarpExits.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Чем подписаны выходы WARP в <see cref="ServerRow.Owner"/>.
    /// </summary>
    /// <remarks>
    /// Подписки с таким именем нет и быть не должно — по нему <see cref="OnChoose"/>
    /// и узнаёт, что действующую менять не надо: выход и так уже в конфиге.
    /// </remarks>
    private const string WarpOwner = "\0warp";

    /// <summary>
    /// Заводит учётную запись WARP и добавляет её в список подписок.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Порядок именно такой: сперва ключи и регистрация, и только при успехе —
    /// строка в списке. Иначе в списке появлялась бы подписка, которая ничего
    /// не отдаёт, и убирать её пришлось бы руками.
    /// </para>
    /// <para>
    /// Повторное нажатие заводит новую запись поверх старой. Это не откат
    /// и не починка: у WARP нет способа «обновить» запись, а выходной адрес
    /// у него и так меняется. Прежняя при этом остаётся у Cloudflare
    /// висеть — удалять её нечем, кроме как её же ключом доступа, и на
    /// бесплатном тарифе это никого не стесняет.
    /// </para>
    /// </remarks>
    /// <summary>
    /// Включает и выключает WARP.
    /// </summary>
    /// <remarks>
    /// Ключи заводятся один раз, при первом включении. Выключение их не трогает:
    /// у Cloudflare нет способа «обновить» запись, и заводить новую при каждом
    /// щелчке значило бы плодить их на ровном месте.
    /// </remarks>
    private async void OnWarp(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        // Выключение и повторное включение с готовыми ключами — просто запись
        // в настройках. Сеть здесь не нужна вовсе.
        if (settings.WarpEnabled || WarpAccount.Load() is not null)
        {
            var next = settings with { WarpEnabled = !settings.WarpEnabled };
            next.Save(AppSettings.DefaultPath);

            ShowWarp(next);

            Status.Text = next.WarpEnabled
                ? "WARP добавлен к серверам действующей подписки. "
                  + "Применится при следующем запуске движков."
                : "WARP выключен. Ключи сохранены — включить обратно можно без регистрации.";

            this.Offer("WARP переключён");
            return;
        }

        var singBox = FindSingBox();

        if (singBox is null)
        {
            Status.Text = "Движок sing-box не найден рядом с программой — ключи заводить нечем.";
            return;
        }

        WarpButton.IsEnabled = false;
        WarpButton.Content = "включаю…";
        Status.Text = "Завожу ключи и регистрирую их в Cloudflare…";

        try
        {
            var keys = await Task.Run(() => WireGuardKeys.Generate(singBox));

            using var client = new WarpClient();
            var account = await client.RegisterAsync(keys, CancellationToken.None);

            account.Save();

            (AppSettings.Load(AppSettings.DefaultPath) with { WarpEnabled = true })
                .Save(AppSettings.DefaultPath);

            Status.Text = $"WARP включён: адрес внутри сети {account.AddressV4}. "
                + "Его выходы добавлены к серверам действующей подписки, "
                + "применится при следующем запуске движков.";

            await LoadAsync();
        }
        catch (Exception ex)
        {
            // Отказ почти всегда один и тот же, и звучит он непонятно:
            // «время ожидания истекло». Называем причину, раз она известна.
            var reason = ex.GetBaseException();

            Status.Text = reason is TaskCanceledException or HttpRequestException or IOException
                ? "Cloudflare не ответил. Его домен закрыт российскими операторами по имени "
                  + "в TLS, поэтому запрос идёт через туннель — запустите движки с работающей "
                  + "подпиской и повторите."
                : "Не удалось подключить WARP: " + reason.Message;
        }
        finally
        {
            WarpButton.IsEnabled = true;
            ShowWarp(AppSettings.Load(AppSettings.DefaultPath));
        }
    }

    private void OnAuto(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath) with { PreferredServer = null };
            settings.Save(AppSettings.DefaultPath);

            Reshow();
            ShowPick(settings);

            Status.Text = "Сервер больше не закреплён — выбирается автоподбором по задержке.";
            this.Offer("Выбор сервера изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось: " + ex.GetBaseException().Message;
        }
    }

    private void OnForeign(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings = settings with { ForeignExitsOnly = !settings.ForeignExitsOnly };
            settings.Save(AppSettings.DefaultPath);

            ShowPick(settings);

            Status.Text = settings.ForeignExitsOnly
                ? "Автоподбор берёт только зарубежные выходы."
                : "Автоподбор берёт любые выходы, включая отечественные.";

            this.Offer("Отбор серверов изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Включает и выключает туннель.
    /// </summary>
    /// <remarks>
    /// Переключает режим между «выборочно» и «только десинк» — и, если движки
    /// работают, перезапускает их. Здесь это уместно, в отличие от выбора
    /// в списке: кнопка называется «выключить VPN», и оставить её без действия
    /// до следующего запуска значило бы соврать надписью.
    /// </remarks>
    private void OnPower(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            var next = settings with
            {
                Mode = settings.NeedsProxy ? OperatingMode.DesyncOnly : OperatingMode.Selective,
            };

            next.Save(AppSettings.DefaultPath);
            ShowPower(next);

            var state = SupervisorState.Load(SupervisorState.DefaultPath);

            if (state is null || !state.IsSupervisorAlive())
            {
                Status.Text = next.NeedsProxy
                    ? "VPN включён. Поднимется вместе с движками на «Главной»."
                    : "VPN выключен. Десинк остаётся.";

                return;
            }

            Restart(next);
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переключить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Останавливает движки и поднимает их заново.</summary>
    /// <remarks>
    /// Конфиг при этом пересобирается: смена сервера ради того и делается,
    /// чтобы туда пошёл трафик, а не только чтобы поменялась подпись.
    /// </remarks>
    private async void Restart(AppSettings settings)
    {
        PowerButton.IsEnabled = false;
        Status.Text = "Перезапускаю движки…";

        var outcome = await EngineControl.RestartAsync(CancellationToken.None);

        PowerButton.IsEnabled = true;

        if (!outcome.Ok)
        {
            Status.Text = outcome.Message;
            return;
        }

        Status.Text = settings.NeedsProxy
            ? "VPN включён, движки перезапущены."
            : "VPN выключен, движки перезапущены. Десинк работает.";
    }

    private void OnFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SubRow row })
            return;

        row.Open = !row.Open;
        row.Entry.Open = row.Open;

        try
        {
            _book.Save();
        }
        catch (Exception)
        {
            // Состояние показа не стоит того, чтобы из-за него жаловаться.
        }

        Redraw();
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        var url = NewUrl.Password.Trim();

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            // Ссылку не повторяем даже в жалобе: она уже в поле, и вынести её
            // в подпись значило бы показать ровно то, что мы прячем.
            Status.Text = "Это не похоже на ссылку.";
            return;
        }

        // Обёртки клиентов разворачиваются до проверки, а не после.
        // Библиотека их и так понимает — happ://add/, clash://install-config,
        // sn://subscription, — но проверка стояла раньше неё и отбивала
        // ссылку, которую программа умеет читать. Поставщики раздают именно
        // такие: у них одна кнопка «добавить в клиент».
        parsed = SubscriptionClient.Unwrap(parsed);
        url = parsed.ToString();

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            Status.Text = "Это не похоже на ссылку подписки: нужна http, https "
                + "либо обёртка happ, clash или sn.";

            return;
        }

        try
        {
            var book = SubscriptionBook.Load();
            var name = NewName.Text.Trim();

            if (name.Length == 0)
                name = book.FreeName();

            if (book.Entries.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Status.Text = $"Подписка с именем «{name}» уже есть. Дайте другое.";
                return;
            }

            book.Entries.Add(new SubscriptionEntry { Name = name, Url = url });
            book.Save();

            // Первая становится действующей сама: иначе человек добавил бы
            // подписку, увидел её серверы и не понял, почему туннель их
            // не берёт.
            if (book.Entries.Count == 1)
                SubscriptionBook.MakeActive(book.Entries[0]);

            NewName.Clear();
            NewUrl.Clear();

            _ = LoadAsync();
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось добавить: " + ex.GetBaseException().Message;
        }
    }

    private void OnActivate(object sender, RoutedEventArgs e)
    {
        // Кнопка внутри кнопки-папки: без этого нажатие дойдёт до неё,
        // и папка захлопнется на ровном месте.
        e.Handled = true;

        if (sender is not Button { Tag: string name })
            return;

        var entry = _book.Entries.FirstOrDefault(x => x.Name == name);

        if (entry is null)
            return;

        try
        {
            SubscriptionBook.MakeActive(entry);
            Reshow();

            Status.Text = $"Действует «{name}». Прежний выбор сервера снят — в этой подписке "
                + "такого тега может не быть. Применится при следующем запуске движков.";

            this.Offer($"Действует подписка «{name}»");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переключить: " + ex.GetBaseException().Message;
        }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not Button { Tag: string name })
            return;

        try
        {
            var book = SubscriptionBook.Load();
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            bool wasActive = book.Active(settings)?.Name == name;

            book.Entries.RemoveAll(entry => entry.Name == name);
            book.Save();

            if (wasActive)
            {
                // Убрали действующую — туннелю не на что опереться. Берём
                // первую из оставшихся, а если их нет, честно очищаем ссылку:
                // ссылка на убранную подписку жила бы в настройках молча.
                if (book.Entries.Count > 0)
                {
                    SubscriptionBook.MakeActive(book.Entries[0]);
                }
                else
                {
                    (settings with { SubscriptionUrl = null, PreferredServer = null })
                        .Save(AppSettings.DefaultPath);
                }
            }

            _ = LoadAsync();
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось убрать: " + ex.GetBaseException().Message;
        }
    }

    private void OnSort(object sender, RoutedEventArgs e)
    {
        _byLatency = !_byLatency;
        SortButton.Content = _byLatency ? "По порядку" : "По задержке";

        Reshow();
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} ТБ",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} МБ",
        _ => $"{bytes / 1024.0:0.#} КБ",
    };

    private static string Ago(DateTimeOffset when)
    {
        var passed = DateTimeOffset.Now - when;

        return passed switch
        {
            { TotalMinutes: < 1 } => "только что",
            { TotalHours: < 1 } => $"{(int)passed.TotalMinutes} мин назад",
            { TotalDays: < 1 } => $"{(int)passed.TotalHours} ч назад",
            _ => $"{(int)passed.TotalDays} дн назад",
        };
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync();

    /// <summary>
    /// Гоняет пробу по всем серверам всех подписок.
    /// </summary>
    /// <remarks>
    /// Через локальный вход, без TUN: работающий обход при этом не прерывается,
    /// и права администратора не нужны — хотя у окна они и так есть.
    /// </remarks>
    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var servers = new List<ProxyServer>();

        foreach (var row in _rows)
        {
            try
            {
                using var client = new SubscriptionClient();
                var info = await client.FetchAsync(new Uri(row.Entry.Url), CancellationToken.None);

                // Незамеряемые отсеиваются здесь, а не внутри пробника: иначе
                // счётчик «измерено N из M» считал бы и тех, кого не трогали.
                servers.AddRange(info.Servers.Where(s => s.IsSupportedBySingBox && s.IsMeasurable));
            }
            catch (Exception)
            {
                // Непрочитанная подписка просто не участвует в замере;
                // почему она не прочиталась, уже написано в её папке.
            }
        }

        // Выходы WARP замеряются вместе со всеми: в конфиге они лежат рядом
        // с серверами подписки, и знать про них надо то же самое.
        if (settings.WarpEnabled)
            servers.AddRange(WarpAccount.Exits().Where(s => s.IsMeasurable));

        if (servers.Count == 0)
        {
            Status.Text = "Замерять нечего: серверов нет.";
            return;
        }

        var singBox = FindSingBox();

        if (singBox is null)
        {
            Status.Text = "Движок sing-box не найден рядом с программой — замерять нечем.";
            return;
        }

        _work?.Cancel();
        _work = new CancellationTokenSource();

        MeasureButton.IsEnabled = false;
        MeasureButton.Content = "Измеряю…";

        int done = 0;

        try
        {
            await new ProxyProbe(singBox).RunManyAsync(
                servers,
                new ProbeOptions
                {
                    // Внешний адрес здесь не показывают, а его поиск стоит
                    // секунд на каждом сервере.
                    LookupExternalIp = false,
                    LogLevel = "warn",
                },
                result =>
                {
                    _health.Set(new ServerHealth
                    {
                        Tag = result.ServerTag,
                        Success = result.Success,
                        LatencyMs = result.Latency?.TotalMilliseconds,
                        CheckedAt = DateTimeOffset.Now,
                    });

                    // Обновляем на каждом ответе: проверка идёт полминуты,
                    // и таблица, оживающая на глазах, куда честнее полосы
                    // загрузки, которая ничего не измеряет.
                    Dispatcher.Invoke(() =>
                    {
                        done++;
                        Status.Text = $"Измерено {done} из {servers.Count}…";
                        Reshow();
                    });
                },
                _work.Token);

            _health.Save();

            int alive = servers.Count(s => _health.Find(s.Tag) is { Success: true });
            Status.Text = $"Отвечают {alive} из {servers.Count}. Замер сохранён — меню увидит те же цифры.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Замер прерван.";
        }
        catch (Exception ex)
        {
            Status.Text = "Замер не удался: " + ex.GetBaseException().Message;
        }
        finally
        {
            MeasureButton.IsEnabled = true;
            MeasureButton.Content = "Замерить все";
        }
    }

    /// <summary>
    /// Записывает выбор сервера.
    /// </summary>
    /// <remarks>
    /// Не перезапускаем движки: они несут весь трафик машины, и уронить их
    /// в ответ на клик по строке в списке — не та цена, на которую человек
    /// соглашался, выбирая сервер. Кнопка включения VPN — другое дело:
    /// там перезапуск и есть то, о чём просят.
    /// </remarks>
    private void OnChoose(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ServerRow row })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            // Выходы WARP действующую подписку не меняют: они добавлены
            // к ней, а не вместо неё, и уже лежат в том же конфиге. Попытка
            // «сделать действующим» WARP как раз и отключала рабочий VPN.
            var owner = row.Owner == WarpOwner
                ? null
                : _book.Entries.FirstOrDefault(entry => entry.Name == row.Owner);

            bool switched = owner is not null && _book.Active(settings)?.Name != owner.Name;

            if (switched)
                SubscriptionBook.MakeActive(owner!);

            (AppSettings.Load(AppSettings.DefaultPath) with { PreferredServer = row.Tag })
                .Save(AppSettings.DefaultPath);

            // Раньше перерисовки, а не после. Выбор уже записан, и движки уже
            // работают со старой настройкой — сказать об этом надо независимо
            // от того, как пройдёт перерисовка. Стояло последним, и любая
            // её заминка съедала уведомление целиком: настройка менялась
            // молча, а человек узнавал об этом в следующий раз.
            this.Offer($"Выбран {row.Tag}");

            Reshow();

            Status.Text = switched
                ? $"Выбран {row.Tag}, действующей стала «{row.Owner}»: конфиг движка собирается "
                  + "по одной ссылке. Применится при следующем запуске движков."
                : $"Выбран {row.Tag}. Применится при следующем запуске движков.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать выбор: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Где лежит движок; <c>null</c> — не нашли.</summary>
    private static string? FindSingBox()
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box", "sing-box.exe");

        return File.Exists(beside) ? beside : null;
    }
}
