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
    bool Measurable,

    /// <summary>Кнопка «убрать» — только у отдельных ключей.</summary>
    Visibility RemoveShown = Visibility.Collapsed,

    /// <summary>Пункт меню строки: убрать из автоподбора или вернуть.</summary>
    string AutoPickLabel = "Не брать в автоподбор");

/// <summary>Папка одной подписки.</summary>
public sealed class SubRow
{
    public required SubscriptionEntry Entry { get; init; }

    /// <summary>Папка отдельных ключей, а не подписки (0.9.0).</summary>
    public bool IsKeys { get; init; }

    /// <summary>У папки ключей — какой ключ дал какой сервер: по нему ключ и убирают.</summary>
    public IReadOnlyList<KeyServer> KeyServers { get; set; } = [];

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

    /// <summary>Все серверы, как их дала подписка, — исходные теги.</summary>
    public IReadOnlyList<ProxyServer> Raw { get; set; } = [];

    /// <summary>
    /// Те же серверы с тегами пула (<see cref="SubscriptionPool.Tag"/>), если
    /// подписка в работе; иначе исходные. По ним выбирают, мерят и судят
    /// о живости — как и сборка конфига, иначе замер лёг бы не на тот сервер.
    /// </summary>
    public IReadOnlyList<ProxyServer> Pooled { get; set; } = [];

    /// <summary>Идёт ли замер именно этой подписки.</summary>
    public bool Checking { get; set; }

    /// <summary>Занят ли замер вообще — хоть этой подпиской, хоть соседней.</summary>
    public bool Busy { get; set; }

    public string CheckLabel => Checking ? "Тест пинга: замеряю…" : "Тест пинга: замерить серверы подписки";

    /// <summary>
    /// Пока идёт один замер, второй не начинают: каждый поднимает по пять
    /// движков разом, и два прогона вместе дают десять — это уже заметно
    /// машине и ничего не ускоряет.
    /// </summary>
    public bool CanCheck => !Busy && Servers.Count > 0;

    public Visibility ServersShown => Open && Servers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Поворот значка раскрытия.
    /// </summary>
    /// <remarks>
    /// Общий с маршрутами и хостами — см. <see cref="Chevrons"/>. Здесь до
    /// 0.6.3 стояла своя пара знаков, «►/▼» вместо «▸/▾», и подписки
    /// выбивались из остального окна.
    /// </remarks>
    public double ChevronAngle => Chevrons.Angle(Open);

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
/// квотой и сроком. С 0.9.0 движок собирается из пула — всех подписок
/// «в работе» (<see cref="SubscriptionPool"/>), — и автоподбор выбирает
/// из всех сразу. Сервер можно выбрать из любой; выбор из подписки вне работы
/// ставит её в работу, иначе конфиг собрался бы без этого сервера. Теги
/// серверов в работе — теги пула, те же, что у движка (<c>ApplyPool</c>).
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
    /// <summary>
    /// Замер серверов.
    /// </summary>
    /// <remarks>
    /// Отдельно от чтения подписок, и это не аккуратность ради аккуратности.
    /// Источник был один на оба действия, а раздел перечитывает подписки при
    /// каждом заходе — и отменял этим замер, начатый секундой раньше. Снаружи
    /// это выглядело как «кнопка „Замерить все“ работает через раз».
    /// </remarks>
    private CancellationTokenSource? _work;

    /// <summary>Чтение подписок.</summary>
    private CancellationTokenSource? _reading;

    /// <summary>Сортировать по задержке, а не по порядку подписки.</summary>
    private bool _byLatency;

    public VpnView()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            // Движок спрашивается сразу, не дожидаясь подписок. Прежде опрос
            // заводился после чтения всех подписок и первый раз срабатывал
            // ещё через 15 с — с лежащей «Основной» карточка оживала
            // через полминуты, и владелец (28.09) видел актуальный сервер
            // только при перезаходе на вкладку.
            var load = LoadAsync();
            _ = ShowExitAsync();
            _exitTimer.Start();
            await load;
        };

        Unloaded += (_, _) =>
        {
            _work?.Cancel();
            _reading?.Cancel();
            _exitTimer.Stop();
        };

        _exitTimer.Tick += async (_, _) =>
        {
            await ShowExitAsync();
            await RefreshMeasuresAsync();
        };
    }

    /// <summary>
    /// Раз в три секунды, пока вкладка открыта, — какой выход держит движок.
    /// </summary>
    /// <remarks>
    /// Автоподбор переключает выходы сам, по задержке и живости, и показанный
    /// однажды сервер к следующей минуте мог смениться. Опрос — к движку
    /// на localhost, он дешёвый; было 15 с, и смена выхода или запуск движков
    /// доходили до карточки с таким же опозданием.
    /// </remarks>
    private readonly System.Windows.Threading.DispatcherTimer _exitTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    /// <summary>Строка автоподбора без текущего выхода — к ней дописывается выход.</summary>
    private string _pickBase = string.Empty;

    /// <summary>
    /// Дописывает в строку автоподбора сервер, через который трафик идёт сейчас.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Просьба владельца 26.09. Строка говорила «через быстрейший из живых», но
    /// какой это сервер, было видно только в <c>nz status</c>: тот же вечер
    /// через «Польшу» висело каждое третье соединение, и понять, с каким
    /// выходом беда, из окна было нельзя.
    /// </para>
    /// <para>
    /// Спрашиваем сам движок, а не настройки: 23.09 в настройках стояло
    /// «авто», а движок держался WARP из своего кэша. Если движок держит не то,
    /// что сказано в настройках, строка это называет.
    /// </para>
    /// </remarks>
    /// <summary>Что движок ответил в последний раз: выход и выбран ли он сам.</summary>
    private (bool Running, string? Server, bool Automatic) _live;

    private bool _asking;

    private async Task ShowExitAsync()
    {
        if (!IsLoaded || _pickBase.Length == 0)
            return;

        if (!EnginesRunning)
        {
            PickLine.Text = _pickBase + " Движки не запущены — выхода сейчас нет.";
            _live = (false, null, false);
            ShowCurrent();
            return;
        }

        // Опрос чаще, чем движок иной раз отвечает: не копим вопросы в очередь.
        if (_asking)
            return;

        _asking = true;
        (string? server, bool automatic) answer;

        try
        {
            answer = await TunnelStatus.CurrentExitAsync(CancellationToken.None);
        }
        finally
        {
            _asking = false;
        }

        var (server, automatic) = answer;

        if (!IsLoaded)
            return;

        _live = (true, server, automatic);
        ShowCurrent();

        // Расхождение — только когда сервер выбран, а движок держит другой.
        // При автоподборе сервер, стоящий прямо в селекторе, — обычное дело:
        // его ставит сторож (прогрев и замена умершего, с 28.09), и писать
        // про него «держит закреплённый, перезапустите движки» было неправдой.
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        PickLine.Text = server is null
            ? _pickBase + " Движок не ответил, какой выход держит."
            : TunnelStatus.Standing(server, settings.PreferredServer) != ExitStanding.Other
                ? $"{_pickBase} Сейчас: {server}."
                : StandInRemark() is { } remark
                    ? $"{_pickBase} Сейчас: {server} — {remark}."
                    : $"{_pickBase} Но движок сейчас держит {server} — перезапустите движки, чтобы закрепление применилось.";
    }

    /// <summary>Примечание надзора о туннеле: «временная замена…»; <c>null</c> — нет.</summary>
    private static string? StandInRemark() =>
        SupervisorState.Load(SupervisorState.DefaultPath)?.Services
            .FirstOrDefault(s => s.Name == "sing-box")?.Remark;

    /// <summary>
    /// Карточка текущего сервера: флаг, имя, откуда он и в каком состоянии.
    /// </summary>
    /// <remarks>
    /// Имя — у движка, если он работает: показывать надо то, через что идёт
    /// трафик, а не то, что выбрано в настройках, — они расходятся до
    /// перезапуска. Движки стоят — показывается закреплённый сервер или
    /// обещание автоподбора. Подписка, протокол и задержка — из пула
    /// и замеров: по тегу пула сервер находится однозначно.
    /// </remarks>
    private void ShowCurrent()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var (running, live, automatic) = _live;

        bool pinned = !string.IsNullOrWhiteSpace(settings.PreferredServer);
        var tag = running ? live : settings.PreferredServer;

        // Имя группы вместо сервера: автоподбор ещё не выбрал — до первого
        // своего замера ему выбирать не из чего. Показывать «auto-latency»
        // как имя сервера значило бы выдать служебное слово за выход.
        bool choosing = running && tag is not null && tag.StartsWith("auto", StringComparison.OrdinalIgnoreCase);

        if (choosing)
            tag = null;

        CurrentCaption.Text = running
            ? "Сейчас трафик идёт через"
            : pinned ? "Закреплён — поднимется вместе с движками" : "Движки не запущены";

        // Где сервер лежит: подписка пула или выход WARP.
        var found = tag is null
            ? default
            : _rows.SelectMany(r => r.Pooled.Select(s => (Owner: r.Entry.Name, Server: s)))
                .Concat(Warp.Exits().Select(s => (Owner: "WARP", Server: s)))
                .FirstOrDefault(x => x.Server.Tag == tag);

        if (tag is null)
        {
            CurrentName.Text = choosing ? "Автоподбор выбирает…" : running ? "Выход не назван" : "Выберет автоподбор";
            CurrentDetail.Text = choosing
                ? "Движок меряет серверы пула и возьмёт быстрейший из живых — обычно это секунды."
                : running
                    ? "Движок не ответил, какой выход держит."
                    : "При запуске движков — быстрейший из живых серверов всех подписок в работе.";
            ShowFlag(string.Empty);
        }
        else
        {
            var (country, name) = CountryTag.Split(tag);
            CurrentName.Text = name.Length > 0 ? name : tag;
            ShowFlag(country);

            var parts = new List<string>();

            if (found.Server is { } server)
            {
                parts.Add(found.Owner == "WARP" ? "WARP" : $"из «{found.Owner}»");
                parts.Add(server.Protocol.ToString());
            }

            if (Seen(tag) is { Success: true, LatencyMs: { } ms })
                parts.Add($"{ms:0} мс");

            // Закреплён ли сервер, знают настройки, а не селектор: сторож ставит
            // серверы прямо в него и при автоподборе (TunnelStatus.Standing).
            // Замену выбранному серверу называет надзор: со стороны её
            // не отличить от выбора, который ещё не применился.
            parts.Add(!running
                ? "закреплён"
                : TunnelStatus.Standing(tag, settings.PreferredServer) switch
                {
                    ExitStanding.Pinned => "закреплён",
                    ExitStanding.Other => StandInRemark() ?? "выбор в настройках ещё не применён",
                    _ => "выбран автоподбором",
                });

            CurrentDetail.Text = string.Join(" · ", parts);
        }

        // Метка состояния: зелёная, пока движки работают.
        var key = running ? "Accent" : "Faint";
        CurrentDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        CurrentPill.SetResourceReference(Border.BorderBrushProperty, key);
        CurrentState.Text = running ? "в работе" : "движки остановлены";
    }

    /// <summary>Флаг картинкой, а нет картинки — буквами страны.</summary>
    private void ShowFlag(string country)
    {
        var flag = country.Length == 2 ? FlagImages.For(country) : null;

        if (flag is not null)
        {
            CurrentFlag.Background = new ImageBrush(flag) { Stretch = Stretch.UniformToFill };
            CurrentCountry.Text = string.Empty;
        }
        else
        {
            CurrentFlag.SetResourceReference(Border.BackgroundProperty, "Raised");
            CurrentCountry.Text = country.Length == 2 ? country : "•";
        }
    }

    private async Task LoadAsync(bool force = false)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        ShowPick(settings);

        _book = SubscriptionBook.Load();

        // Перенос старой строки WARP в выключатель мог поправить настройки —
        // перечитываем, иначе карточка покажет состояние до переноса.
        settings = AppSettings.Load(AppSettings.DefaultPath);
        ShowWarp(settings);

        // Запасы подписок, убранных раньше, — в том числе до того, как их
        // начали стирать при удалении (30.09). Пустая книга не в счёт: так же
        // выглядит испорченный файл, и чистка по ней стёрла бы всё.
        if (_book.Entries.Count > 0)
            KeepReserves(_book, settings);

        _rows = _book.Entries
            .Select(entry => new SubRow
            {
                Entry = entry,
                Open = entry.Open,
                Active = entry.Working,
            })
            .ToList();

        // Отдельные ключи — последней папкой, как и последним источником пула:
        // порядок тот же, что у сборки конфига, иначе метки пула разошлись бы.
        if (_book.Keys.Count > 0)
        {
            _rows.Add(new SubRow
            {
                Entry = new SubscriptionEntry { Name = KeyRing.Name, Url = string.Empty, InPool = _book.KeysInPool },
                IsKeys = true,
                Open = true,
                Active = _book.KeysInPool,
            });
        }

        Subscriptions.ItemsSource = _rows;

        if (_rows.Count == 0)
        {
            Status.Text = "Подписок нет. Без них серверов нет, а VPN недоступен — "
                + "десинк при этом работает.";

            return;
        }

        // Прежнее чтение прерываем. Оно могло висеть на мёртвой панели,
        // и без этого второе нажатие просто вставало за первым в очередь.
        //
        // Свой источник, а не общий с замером. Общий и был причиной того,
        // что «Замерить все» работало через раз: раздел перечитывает подписки
        // при каждом заходе, отменял этим общий токен — и замер, начатый
        // секундой раньше, обрывался на первом же сервере. Снаружи выглядело
        // как «кнопка срабатывает не всегда».
        _reading?.Cancel();
        _reading = new CancellationTokenSource();

        var token = _reading.Token;

        RefreshButton.IsEnabled = false;

        try
        {
            // По очереди, а не разом: панели подписок нередко одна и та же,
            // и три запроса в одну секунду с одного адреса ей не нравятся.
            //
            // Номер называется вслух: при зависшей панели «Читаю подписки…»
            // без номера не говорит даже того, которая из трёх не отвечает.
            bool everyRead = true;

            for (int i = 0; i < _rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                Status.Text = _rows.Count > 1
                    ? $"Читаю подписку {i + 1} из {_rows.Count}…"
                    : "Читаю подписку…";

                everyRead &= await FillAsync(_rows[i], settings, token, force);
            }

            // Теги пула — до чистки замеров: замеры подписок в работе лежат
            // под ними, и чистка по исходным тегам выбросила бы их.
            ApplyPool(settings);

            // Замеры исчезнувших серверов — вон, иначе файл копит их вечно,
            // а по ним судят о свежести всего списка. Звала это только
            // консоль; окно не звало ни разу (найдено 23.09).
            //
            // Только когда прочитались все подписки: не ответившая сейчас
            // лишилась бы замеров своих серверов, которые никуда не делись.
            // WARP в подписках не значится, и его выход сохраняется отдельно.
            if (everyRead && _rows.Count > 0)
            {
                _health.KeepOnly(_rows
                    .SelectMany(r => r.AsGiven)
                    .Select(s => s.Tag)
                    .Concat(Warp.Exits().Select(e => e.Tag)));
                _health.Save();
            }

            int servers = _rows.Sum(r => r.Servers.Count);

            Status.Text = servers == 0
                ? "Ни одна подписка не отдала серверов."
                : $"Подписок: {_rows.Count}, серверов: {servers}.";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "Чтение прервано.";
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            Redraw();
        }
    }

    /// <summary>Читает одну подписку и заполняет её папку.</summary>
    /// <returns>Прочиталась ли: ответила и разобралась.</returns>
    private async Task<bool> FillAsync(SubRow row, AppSettings settings, CancellationToken cancellationToken, bool force = false)
    {
        // Папка ключей: читать нечего, ключи уже здесь — только разобрать.
        if (row.IsKeys)
        {
            var (parsed, errors) = KeyRing.Parse(_book.Keys);

            row.KeyServers = parsed;
            row.Raw = parsed.Select(k => k.Server).ToList();
            row.Pooled = row.Raw;
            row.AsGiven = Rows(row.Raw.Where(s => s.IsUsableOutbound).ToList(), row.Entry.Name, settings);
            row.Servers = InChosenOrder(row.AsGiven);

            row.Detail = $"{Count(parsed.Count, "ключ", "ключа", "ключей")}"
                + (errors.Count > 0 ? $" · не разобрались: {errors.Count} — {errors[0]}" : string.Empty);

            Redraw();
            await Task.CompletedTask;
            return true;
        }

        try
        {
            // Чтение и разбор — в фоне. Без Task.Run разбор шёл продолжением
            // на главном потоке: замер 28.09 — паузы окна по 70–166 мс, пока
            // приходили подписки, прямо посреди появления раздела.
            //
            // Свежий запас (моложе получаса) берётся без сети — тем же
            // чтением, что у движка; к панели идут ⟳ и «Обновить».
            var read = await Task.Run(
                () => SubscriptionPool.ReadOneAsync(row.Entry.Url, force, cancellationToken), cancellationToken);

            if (read.Info is not { } info)
                throw new SubscriptionUnreadException(read.Error ?? "панель не ответила");

            var usable = info.Servers.Where(s => s.IsUsableOutbound).ToList();

            // Теги пула ставятся, когда прочитаны все (ApplyPool): метка
            // зависит от соседних подписок. До того — исходные.
            row.Raw = info.Servers;
            row.Pooled = info.Servers;
            row.AsGiven = Rows(usable, row.Entry.Name, settings);
            row.Servers = InChosenOrder(row.AsGiven);

            // Отброшенные называются числом, а не замалчиваются: человек,
            // видящий в подписке двадцать серверов и пятнадцать здесь,
            // вправе знать, куда делись пять.
            int skipped = info.Servers.Count - usable.Count;

            var at = (read.At ?? DateTimeOffset.Now).LocalDateTime;
            var when = at.Date == DateTime.Today ? $"{at:HH:mm}" : $"{at:dd.MM HH:mm}";

            // Из запаса — так и сказано, с причиной: иначе лежащая панель
            // выглядела бы живой подпиской с часовой давности временем.
            var parts = new List<string>
            {
                read.Source == SubscriptionReadSource.Reserve
                    ? $"панель подвела ({read.Error}) — серверы из запаса {when}"
                    : $"обновлена {when}",
                $"{usable.Count} серверов",
            };

            if (skipped > 0)
                parts.Add($"ещё {skipped} sing-box не поддерживает");

            parts.Add(info.RemainingBytes is { } left
                ? Size(left)
                : info.TotalBytes > 0 ? "квота кончилась" : "без ограничения");

            parts.Add(info.ExpiresAt is { } until
                ? $"{(until - DateTimeOffset.Now).Days} дн"
                : "без срока");

            // Пусто, а панель ответила — причина называется. Молча выходило
            // «0 серверов · 14 дн» (issue #3), и понять, что сломан разбор,
            // а не подписка, было нечем.
            if (usable.Count == 0 && info.Errors.Count > 0)
            {
                parts.Add("не разобралась: " + info.Errors[0]);
                Journal.Write("подписка", $"«{row.Entry.Name}» ответила, но серверов 0: "
                    + string.Join("; ", info.Errors.Take(3)));
            }

            row.Detail = string.Join(" · ", parts);
            Redraw();

            if (read.Source == SubscriptionReadSource.Reserve)
                Journal.Write("подписка", $"«{row.Entry.Name}» панель подвела ({read.Error}), взят запас {when}");

            // Ответила, но не разобралась — не прочиталась: её серверы
            // никуда не делись, и их замеры трогать нельзя. Запас — тоже
            // не прочтение: что у панели сейчас, неизвестно.
            return read.Source != SubscriptionReadSource.Reserve
                && !(usable.Count == 0 && info.Errors.Count > 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Чтение оборвали мы сами — повторным заходом в раздел. Это не
            // сбой подписки, и в её строке ему не место: скажет цикл выше.
            throw;
        }
        catch (Exception ex)
        {
            row.Raw = [];
            row.Pooled = [];
            row.AsGiven = [];
            row.Servers = [];

            // Срок HttpClient приходит той же отменой, что и наша, и прежде
            // читался как «The operation was canceled» — неотличимо от
            // прерванного чтения. Это медленная или мёртвая панель.
            row.Detail = ex is OperationCanceledException
                ? $"не прочиталась: панель не ответила за {SubscriptionClient.DefaultTimeout.TotalSeconds:0} с"
                : "не прочиталась: " + (ex is SubscriptionUnreadException ? ex.Message : ex.GetBaseException().Message);

            // В журнал — чтобы «периодически не читалась» можно было
            // разобрать задним числом. Без ссылки: она равносильна паролю.
            Journal.Write("подписка", $"«{row.Entry.Name}» {row.Detail}");
        }

        Redraw();
        return false;
    }

    /// <summary>
    /// Ставит серверам подписок в работе теги пула — те же, что у движка.
    /// </summary>
    /// <remarks>
    /// Тем же <see cref="SubscriptionPool.Tag"/>, что и сборка конфига, и в том
    /// же порядке подписок: разойдись теги окна и движка, «выбрать» закреплял
    /// бы сервер, которого в конфиге нет, а замер ложился бы не туда.
    /// Подписки вне работы остаются с исходными тегами — в пуле их нет.
    /// Не прочитавшаяся сейчас подписка в расчёт не входит, а движок взял бы
    /// её из запаса; метка у совпавшего имени тогда может разойтись до
    /// следующего чтения — это известное ограничение.
    /// </remarks>
    private void ApplyPool(AppSettings settings)
    {
        var working = _rows.Where(r => r.Active && r.Raw.Count > 0).ToList();
        var tags = SubscriptionPool.Tag(working.Select(r => (r.Entry.Name, r.Raw)).ToList());

        foreach (var row in _rows)
        {
            int index = working.IndexOf(row);

            row.Pooled = index < 0
                ? row.Raw
                : row.Raw.Select((server, j) => server with { Tag = tags[index][j] }).ToList();

            row.AsGiven = Rows(row.Pooled.Where(s => s.IsUsableOutbound).ToList(), row.Entry.Name, settings);

            // Показ — под своим именем из подписки, а тег — пула. Двойник
            // носит тег того, кого повторяет: у Trust «🇩🇪 Германия 🚀» —
            // тот же узел, что первый в «Авто-подборе локации», и без этого
            // в папке стояли бы две строки с одним и тем же чужим именем.
            var own = row.Raw.Where(s => s.IsUsableOutbound).ToList();

            if (own.Count == row.AsGiven.Count)
            {
                row.AsGiven = row.AsGiven
                    .Select((shown, j) =>
                    {
                        var (country, name) = CountryTag.Split(own[j].Tag);
                        var flag = country.Length == 2 ? FlagImages.For(country) : null;

                        return shown with
                        {
                            Name = name.Length > 0 ? name : own[j].Tag,
                            Country = country,
                            Flag = flag,
                            CountryShown = country.Length == 0 || flag is not null ? Visibility.Collapsed : Visibility.Visible,
                            FlagShown = flag is null ? Visibility.Collapsed : Visibility.Visible,
                        };
                    })
                    .ToList();
            }

            if (row.IsKeys)
                row.AsGiven = row.AsGiven.Select(s => s with { RemoveShown = Visibility.Visible }).ToList();

            row.Servers = InChosenOrder(row.AsGiven);
        }

        Redraw();
        ShowCurrent();
    }

    private IReadOnlyList<ServerRow> Rows(
        IReadOnlyList<ProxyServer> servers,
        string owner,
        AppSettings settings)
    {
        var rows = servers.Select(server =>
        {
            var known = Seen(server.Tag);
            bool chosen = server.Tag == settings.PreferredServer;

            // Незамеряемого пробником меряет сам движок — и тогда у него
            // обычный замер, как у всех. «Только в работе» остаётся лишь
            // на то время, пока движки стоят и спросить некого.
            var (latency, key) = !server.IsMeasurable && known is null
                ? ("только в работе", "Faint")
                : (Latency(server.Tag), Key(server.Tag));

            bool excluded = settings.AutoPickExcluded.Contains(server.Tag, StringComparer.Ordinal);

            if (excluded)
                latency += " · вне подбора";

            var detail = Detail(server.Protocol.ToString(), server.Host, server.Port, server.Tag);

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
                server.IsMeasurable,
                AutoPickLabel: excluded ? "Вернуть в автоподбор" : "Не брать в автоподбор");
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

    private int Rank(ServerRow row) => Seen(row.Tag) switch
    {
        { Success: true } => 0,
        null => 1,
        _ => 2,
    };

    private double Ms(ServerRow row) => Seen(row.Tag)?.LatencyMs ?? double.MaxValue;

    private void Redraw()
    {
        Subscriptions.ItemsSource = null;
        Subscriptions.ItemsSource = _rows;
    }

    /// <summary>Пересобирает строки серверов, не перечитывая подписки.</summary>
    private void Reshow()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        // Выходы WARP живут в карточке, а не в списке, и общий обход строк
        // их не касается — пересобираем отдельно, иначе замер по ним виден
        // только после перезахода на вкладку.
        ShowWarp(settings);
        
        foreach (var row in _rows)
        {
            row.Active = row.Entry.Working;

            // Раскладка считается от исходного порядка, а не от показанного:
            // иначе она накапливается сама на себе и вернуться некуда.
            row.Servers = InChosenOrder(row.AsGiven)
                .Select(server => server with
                {
                    Color = (Brush)FindResource(
                        server.Tag == settings.PreferredServer
                            ? "Accent"
                            : server.Measurable || Seen(server.Tag) is not null
                                ? Key(server.Tag)
                                : "Faint"),
                    ChooseLabel = server.Tag == settings.PreferredServer ? "выбран" : "выбрать",
                    CanChoose = server.Tag != settings.PreferredServer,

                    Latency = (server.Measurable || Seen(server.Tag) is not null
                        ? Latency(server.Tag)
                        : "только в работе")
                        + (settings.AutoPickExcluded.Contains(server.Tag, StringComparer.Ordinal) ? " · вне подбора" : string.Empty),

                    AutoPickLabel = settings.AutoPickExcluded.Contains(server.Tag, StringComparer.Ordinal)
                        ? "Вернуть в автоподбор"
                        : "Не брать в автоподбор",

                    // Возраст замера пересчитывается здесь же: иначе он
                    // оставался тем, каким был при чтении подписки, и «17 мин
                    // назад» висело даже на только что замеренном сервере.
                    Detail = Refresh(server.Detail, server.Tag),
                })
                .ToList();
        }

        Redraw();
        ShowCurrent();
    }

    /// <summary>
    /// Подпись под именем сервера: протокол, адрес и возраст замера.
    /// </summary>
    /// <remarks>
    /// Считается заново при каждой перерисовке, а не один раз при чтении
    /// подписки. Иначе «замер 17 мин назад» так и висел до перезахода
    /// на вкладку — в том числе на сервере, который только что замерили.
    /// </remarks>
    private string Detail(string protocol, string host, int port, string tag)
    {
        var detail = $"{protocol}, {host}:{port}";

        if (_measuring.Contains(tag))
            return detail + " · идёт замер";

        return Seen(tag) is { } known
            ? detail + $" · замер {Ago(known.CheckedAt)}"
            : detail;
    }

    /// <summary>
    /// Обновляет хвост подписи, оставив её начало нетронутым.
    /// </summary>
    /// <remarks>
    /// Протокол и адрес заново собирать не из чего — исходного сервера
    /// у строки уже нет, — зато всё до разделителя от замера не зависит.
    /// </remarks>
    private string Refresh(string detail, string tag)
    {
        int cut = detail.IndexOf(" · ", StringComparison.Ordinal);
        var head = cut < 0 ? detail : detail[..cut];

        if (_measuring.Contains(tag))
            return head + " · идёт замер";

        return Seen(tag) is { } known
            ? head + $" · замер {Ago(known.CheckedAt)}"
            : head;
    }

    /// <summary>Теги, которые замеряются прямо сейчас.</summary>
    private readonly HashSet<string> _measuring = new(StringComparer.Ordinal);

    /// <summary>Последние удачные замеры, как их помнит движок (ClashApi.HistoryAsync).</summary>
    private IReadOnlyDictionary<string, (double Ms, DateTimeOffset At)> _engine =
        new Dictionary<string, (double, DateTimeOffset)>();

    /// <summary>
    /// Замер сервера для показа — свежий из двух: файла и движка.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Владелец 30.09: «почему если настройка раз в минуту, последний замер
    /// 32 минуты назад». Вкладка показывала только файл, а в нём были лишь
    /// её собственные замеры. Теперь туда пишет и сторож, а удачные замеры
    /// автоподбора берутся прямо у движка — без новых соединений к продавцам.
    /// </para>
    /// <para>
    /// Замер движка только для показа и в файл не пишется: неудач движок
    /// не хранит, и история «мигающих» из одних удач врала бы в их пользу.
    /// Жёлтый цвет «мигающего» поэтому по-прежнему считается по файлу.
    /// </para>
    /// </remarks>
    private ServerHealth? Seen(string tag)
    {
        var known = _health.Find(tag);

        if (!_engine.TryGetValue(tag, out var engine) || (known is not null && known.CheckedAt >= engine.At))
            return known;

        return (known ?? new ServerHealth { Tag = tag, Success = true, CheckedAt = engine.At })
            with { Success = true, LatencyMs = engine.Ms, CheckedAt = engine.At };
    }

    /// <summary>Каждый какой тик таймера выхода (3 с) перечитывать замеры.</summary>
    private const int FreshEvery = 3;

    private int _freshTick;

    /// <summary>
    /// Перечитывает файл замеров и замеры движка; изменилось — перерисовывает.
    /// </summary>
    /// <remarks>
    /// Раз в девять секунд, пока вкладка открыта: сторож пишет раз в минуту
    /// по умолчанию, и чаще перечитывать незачем, а реже — «замер N мин назад»
    /// отставал бы от правды. Перерисовка только при изменении: полный
    /// перебор строк раз в несколько секунд дёргал окно (24.09).
    /// </remarks>
    private async Task RefreshMeasuresAsync()
    {
        if (_freshTick++ % FreshEvery != 0 || _measuring.Count > 0)
            return;

        var before = Stamp();

        _health.Reload();

        if (EnginesRunning)
        {
            using var api = new ClashApi();

            if (await api.HistoryAsync(CancellationToken.None) is { } history)
                _engine = history;
        }
        else
        {
            _engine = new Dictionary<string, (double, DateTimeOffset)>();
        }

        if (Stamp() != before)
            Reshow();
    }

    /// <summary>Отпечаток всех замеров — чтобы понять, есть ли что перерисовывать.</summary>
    private string Stamp() => string.Join("|",
        _health.Entries.Values.Select(e => $"{e.Tag}:{e.CheckedAt.Ticks}:{e.Success}")
            .Concat(_engine.Select(e => $"{e.Key}:{e.Value.At.Ticks}"))
            .Order(StringComparer.Ordinal));

    private string Key(string tag) => Seen(tag) switch
    {
        // Отвечает через раз — жёлтым (владелец 29.09): ОБС у SecureWay
        // отвечали 2–3 раза из 5, и зелёный на таком врал бы.
        { Flaky: true } => "Warn",
        { Success: true } => "Accent",
        { Success: false } => "Danger",
        _ => "Faint",
    };

    private string Latency(string tag) => Seen(tag) switch
    {
        { Flaky: true, LatencyMs: { } flaky } => $"{flaky:0} мс · через раз",
        { Flaky: true } => "через раз",
        { Success: true, LatencyMs: { } ms } => $"{ms:0} мс",
        { Success: true } => "отвечает",
        { Success: false } => "не отвечает",
        _ => "не замерян",
    };

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

        bool auto = string.IsNullOrWhiteSpace(pinned);

        _pickBase = auto
            ? "Включён: трафик идёт через быстрейший из живых серверов."
            : $"Выключен: закреплён {pinned}. Нажмите, чтобы вернуть автоподбор.";

        PickLine.Text = _pickBase;
        AutoSwitch.IsChecked = auto;

        // Выход — следом, не дожидаясь таймера: иначе первые четверть минуты
        // на вкладке строка была бы без него.
        _ = ShowExitAsync();
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

        // Выключенный — не показываем вовсе (владелец, 28.09): включается он
        // в «Настройках», и карточка «выключен» только занимала место.
        WarpCard.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // Выключатель уехал в настройки, и строка теперь говорит не только
        // состояние, но и где его менять: иначе выключенный WARP выглядит
        // как показанное без всякой причины.
        WarpLine.Text = on
            ? "Добавлен к серверам подписок в работе. Учётную запись движок "
              + "заводит себе сам — от вас не требуется ничего."
            : "Выключен. Включается в «Настройках»: ни почты, ни оплаты, ни ключей.";

        WarpExits.ItemsSource = on ? Rows(Warp.Exits(), WarpOwner, settings) : null;
        WarpExits.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // Замер есть, пока есть у кого спросить. Отдельным пробником MASQUE
        // не берётся — его меряет сам движок, и значит при остановленных
        // движках мерить нечем. Прежде здесь стояло условие «есть хоть один
        // замеряемый пробником», и с уходом выхода по WireGuard кнопка
        // пропала совсем.
        WarpCheckButton.Visibility = on && EnginesRunning
            ? Visibility.Visible
            : Visibility.Collapsed;

        // «Попробовать» есть только при работающих движках: оно переключает
        // живой селектор, а выключенному движку переключать нечего.
        WarpTryButton.Visibility = on && EnginesRunning
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Открывает настройки туннеля.
    /// </summary>
    /// <remarks>
    /// Перечитываем вкладку по закрытии, и только если там что-то меняли:
    /// выключенный WARP убирает список выходов, смена режима меняет подпись
    /// состояния. Без этого окно закрывалось бы, а вкладка показывала
    /// прежнее — ровно тот разлад, из-за которого не верят показаниям.
    /// </remarks>
    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var window = new TunnelSettingsWindow { Owner = Window.GetWindow(this) };

        window.ShowDialog();

        if (window.Changed)
            await LoadAsync();
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
    /// Включает и выключает WARP.
    /// </summary>
    /// <remarks>
    /// Одна запись в настройках, и всё. Заводить ключи и регистрироваться
    /// больше не нужно: выход остался один — MASQUE, а его учётную запись
    /// движок делает себе сам и хранит в своём кэше.
    /// </remarks>
    private void OnWarp(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var next = settings with { WarpEnabled = !settings.WarpEnabled };

            next.Save(AppSettings.DefaultPath);
            ShowWarp(next);

            Status.Text = next.WarpEnabled
                ? "WARP добавлен к серверам подписок в работе. Пока те живы, "
                  + "автоподбор берёт их. Применится при следующем запуске движков."
                : "WARP выключен. Применится при следующем запуске движков.";

            this.Offer("WARP переключён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переключить: " + ex.GetBaseException().Message;
        }
    }

    private void OnAuto(object sender, RoutedEventArgs e)
    {
        // Выключить тумблером нечем: чтобы закрепить сервер, надо знать
        // какой. Говорим, где это делается, а не делаем вид, что выключили.
        if (string.IsNullOrWhiteSpace(AppSettings.Load(AppSettings.DefaultPath).PreferredServer))
        {
            Status.Text = "Автоподбор выключается выбором сервера: нажмите «выбрать» у нужного в списке ниже.";
            return;
        }

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

        // Ключ, а не ссылка подписки — в папку отдельных ключей (0.9.0).
        if (KeyRing.IsKey(url))
        {
            AddKeys(url);
            return;
        }

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

            book.Entries.Add(new SubscriptionEntry { Name = name, Url = url, InPool = true });
            book.Save();

            // Новая встаёт в работу сразу: подписку добавляют, чтобы ею
            // пользоваться, и увидеть её серверы, которых туннель не берёт,
            // значило бы гадать почему. Указатель для консоли — следом.
            SubscriptionBook.SetWorking(url, true);
            this.Offer($"Подписка «{name}» в работе");

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

        if (sender is not FrameworkElement { Tag: string name })
            return;

        var row = _rows.FirstOrDefault(r => r.Entry.Name == name);

        if (row is null)
            return;

        bool working = !row.Active;

        try
        {
            if (row.IsKeys)
                SubscriptionBook.SaveKeys(_book.Keys, working);
            else
                SubscriptionBook.SetWorking(row.Entry.Url, working);

            _book = SubscriptionBook.Load();

            foreach (var r in _rows)
            {
                r.Entry.InPool = r.IsKeys
                    ? _book.KeysInPool
                    : _book.Entries.FirstOrDefault(e => e.Url == r.Entry.Url)?.InPool;
                r.Active = r.Entry.Working;
            }

            // Метки пула зависят от того, кто в работе: вторая подписка
            // с тем же именем сервера добавляет метку и первой.
            ApplyPool(AppSettings.Load(AppSettings.DefaultPath));
            Reshow();

            int inPool = _rows.Count(r => r.Active);

            Status.Text = working
                ? $"«{name}» в работе: её серверы идут в общий автоподбор. В работе подписок: {inPool}. "
                  + "Применится при следующем запуске движков."
                : inPool == 0
                    ? $"«{name}» выведена из работы, и в работе не осталось ни одной — VPN будет "
                      + (AppSettings.Load(AppSettings.DefaultPath).WarpEnabled ? "только через WARP." : "недоступен.")
                    : $"«{name}» выведена из работы. В работе подписок: {inPool}. "
                      + "Применится при следующем запуске движков.";

            this.Offer(working ? $"Подписка «{name}» в работе" : $"Подписка «{name}» выведена из работы");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переключить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>«1 ключ, 2 ключа, 5 ключей» — как в «Маршрутах».</summary>
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

    /// <summary>Корзина у ключа: убрать его одного.</summary>
    private void OnRemoveKey(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag })
            return;

        var keysRow = _rows.FirstOrDefault(r => r.IsKeys);

        if (keysRow is null)
            return;

        // Ключ находится по месту сервера в папке: тег — пула, и у двойника
        // с сервером подписки он тот же, так что ищем именно здесь.
        int index = keysRow.Pooled.ToList().FindIndex(s => s.Tag == tag);

        if (index < 0 || index >= keysRow.KeyServers.Count)
            return;

        var key = keysRow.KeyServers[index].Key;

        try
        {
            var keys = _book.Keys.Where(k => k != key).ToList();
            SubscriptionBook.SaveKeys(keys, _book.KeysInPool);

            _ = LoadAsync();
            Status.Text = $"Ключ «{tag}» убран.";

            if (keysRow.Active)
                this.Offer($"Ключ «{tag}» убран");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось убрать ключ: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Ключи из поля «Добавить» — в папку «Отдельные ключи».</summary>
    private void AddKeys(string input)
    {
        var fresh = KeyRing.Split(input);

        if (fresh.Count == 0)
        {
            Status.Text = "Ключей не нашлось: нужны строки вида vless://, hysteria2://, trojan://, ss://, vmess://.";
            return;
        }

        var (parsed, errors) = KeyRing.Parse(fresh);

        if (parsed.Count == 0)
        {
            Status.Text = "Ключ не разбирается: " + errors[0];
            return;
        }

        try
        {
            var book = SubscriptionBook.Load();
            var added = parsed.Select(k => k.Key).Where(k => !book.Keys.Contains(k)).ToList();

            // Новые ключи — сразу в работе: добавляют, чтобы пользоваться.
            SubscriptionBook.SaveKeys([.. book.Keys, .. added], inPool: true);

            NewName.Clear();
            NewUrl.Clear();

            _ = LoadAsync();

            Status.Text = added.Count == 0
                ? "Эти ключи уже есть."
                : $"Добавлено: {Count(added.Count, "ключ", "ключа", "ключей")} — в папке «{KeyRing.Name}»."
                  + (errors.Count > 0 ? $" Не разобрались: {errors.Count}." : string.Empty);

            if (added.Count > 0)
                this.Offer($"Добавлено: {Count(added.Count, "ключ", "ключа", "ключей")}");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось добавить ключи: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>⟳ у подписки: перечитать только её.</summary>
    private async void OnRefreshOne(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { Tag: string name })
            await RefreshOneAsync(name);
    }

    private async Task RefreshOneAsync(string name)
    {
        var row = _rows.FirstOrDefault(r => r.Entry.Name == name);

        if (row is null)
            return;

        row.Detail = "читаю…";
        Redraw();

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        await FillAsync(row, settings, CancellationToken.None, force: true);

        // Метки пула зависят от соседей — пересчитываем для всех.
        ApplyPool(settings);
        Status.Text = $"«{name}» перечитана.";
    }

    /// <summary>
    /// ⋯ у подписки: всё остальное — как в Happ (владелец, 28.09).
    /// </summary>
    /// <remarks>
    /// Из пунктов Happ здесь нет «Маршрутизации» и «Настроек» подписки:
    /// маршруты у нас общие для всех подписок (вкладка «Маршруты»), а имя
    /// клиента для панели программа подбирает сама — заглушку для нелюбимого
    /// клиента она узнаёт и переспрашивает под Happ.
    /// </remarks>
    private void OnMore(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: string name } anchor)
            return;

        var row = _rows.FirstOrDefault(r => r.Entry.Name == name);

        if (row is null)
            return;

        bool first = _rows.Count > 0 && ReferenceEquals(_rows[0], row);

        // У папки ключей — своё меню: ни ссылки, ни места в списке у неё нет.
        if (row.IsKeys)
        {
            var keysMenu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            keysMenu.Items.Add(Item("\uE72C", "Обновить", async () => await RefreshOneAsync(name)));
            keysMenu.Items.Add(Item("\uEC4A", "Тест пинга", async () => await MeasureAsync([row]), row.CanCheck));
            keysMenu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
            keysMenu.Items.Add(Item("\uE74D", "Удалить все ключи…", ConfirmRemoveKeys));
            keysMenu.IsOpen = true;
            return;
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        menu.Items.Add(Item("", "Обновить", async () => await RefreshOneAsync(name)));
        menu.Items.Add(Item("", "Тест пинга", async () => await MeasureAsync([row]), row.CanCheck));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(Item("", first ? "Уже первая" : "Закрепить наверху", () => MoveToTop(name), !first));
        menu.Items.Add(Item("", "Копировать ссылку", () => CopyUrl(row)));
        menu.Items.Add(Item("", "Изменить…", () => Edit(row)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(Item("", "Удалить…", () => ConfirmRemove(name)));

        menu.IsOpen = true;
    }

    private static MenuItem Item(string glyph, string text, Action act, bool enabled = true)
    {
        var icon = new TextBlock { Text = glyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

        var item = new MenuItem { Header = text, Icon = icon, IsEnabled = enabled };
        item.Click += (_, _) => act();

        return item;
    }

    /// <summary>
    /// Первой в списке — и первой в пуле: её сервер побеждает при совпадении
    /// узла у двух продавцов, и на неё смотрит консоль.
    /// </summary>
    private void MoveToTop(string name)
    {
        try
        {
            var book = SubscriptionBook.Load();
            var entry = book.Entries.FirstOrDefault(x => x.Name == name);

            if (entry is null)
                return;

            book.Entries.Remove(entry);
            book.Entries.Insert(0, entry);
            book.Save();

            // Указатель для консоли — первая в работе.
            SubscriptionBook.SetWorking(entry.Url, entry.Working);

            _ = LoadAsync();
            Status.Text = $"«{name}» закреплена наверху.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переставить: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// В буфер обмена, не на экран.
    /// </summary>
    /// <remarks>
    /// Ссылка равносильна паролю и на экран не выводится нигде — но это ссылка
    /// самого человека, и перенести её в другой клиент он вправе. Копируется
    /// только по его нажатию, и подпись напоминает, чем она является.
    /// </remarks>
    private void CopyUrl(SubRow row)
    {
        try
        {
            Clipboard.SetText(row.Entry.Url);
            Status.Text = $"Ссылка «{row.Entry.Name}» скопирована. Это пароль к вашим серверам — "
                + "вставляйте её только в свой клиент.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось скопировать: " + ex.GetBaseException().Message;
        }
    }

    private void Edit(SubRow row)
    {
        var taken = _rows.Where(r => !ReferenceEquals(r, row)).Select(r => r.Entry.Name).ToList();
        var window = new SubscriptionEditWindow(row.Entry.Name, taken) { Owner = Window.GetWindow(this) };

        if (window.ShowDialog() != true || window.ChosenName is not { } name)
            return;

        try
        {
            var book = SubscriptionBook.Load();
            var entry = book.Entries.FirstOrDefault(x => x.Url == row.Entry.Url);

            if (entry is null)
                return;

            var oldUrl = entry.Url;
            entry.Name = name;

            if (window.ChosenUrl is { } url)
                entry.Url = url;

            book.Save();

            // Ссылка сменилась — указатель консоли мог смотреть на прежнюю.
            if (window.ChosenUrl is not null)
                SubscriptionBook.SetWorking(entry.Url, entry.Working);

            _ = LoadAsync();

            Status.Text = window.ChosenUrl is null
                ? $"Подписка переименована: «{name}»."
                : $"«{name}»: ссылка заменена. Применится при следующем запуске движков.";

            if (window.ChosenUrl is not null && entry.Working)
                this.Offer($"У подписки «{name}» новая ссылка");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось сохранить: " + ex.GetBaseException().Message;
        }
    }

    private void ConfirmRemoveKeys()
    {
        var answer = MessageBox.Show(
            $"Удалить все отдельные ключи ({_book.Keys.Count})? Их серверы уйдут из пула, а ключи придётся вставлять заново.",
            "Удалить ключи",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK)
            return;

        SubscriptionBook.SaveKeys([], true);
        _ = LoadAsync();
        Status.Text = "Отдельные ключи удалены.";
        this.Offer("Отдельные ключи удалены");
    }

    private void ConfirmRemove(string name)
    {
        var answer = MessageBox.Show(
            $"Удалить подписку «{name}»? Её серверы уйдут из пула, а ссылку придётся вставлять заново.",
            "Удалить подписку",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.OK)
            Remove(name);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { Tag: string name })
            Remove(name);
    }

    private void Remove(string name)
    {
        try
        {
            var book = SubscriptionBook.Load();
            var removed = book.Entries.FirstOrDefault(entry => entry.Name == name);

            book.Entries.RemoveAll(entry => entry.Name == name);
            book.Save();

            // Указатель для консоли — на первую из оставшихся в работе;
            // ссылка на убранную подписку жила бы в настройках молча.
            if (removed is not null)
                SubscriptionBook.SetWorking(removed.Url, false);

            // В работе не осталось никого — выбранный сервер тоже ушёл вместе
            // с подпиской, и закрепление указывало бы в никуда.
            if (SubscriptionBook.Load().Pool.Count == 0)
            {
                (AppSettings.Load(AppSettings.DefaultPath) with { PreferredServer = null })
                    .Save(AppSettings.DefaultPath);
            }

            // Запас убранной — вон вместе с ней: в нём ключи серверов (30.09).
            // Только если она и правда нашлась и убрана: иначе книга могла
            // прочитаться пустой из-за порчи, и стёрлись бы запасы всех.
            if (removed is not null)
                KeepReserves(SubscriptionBook.Load(), AppSettings.Load(AppSettings.DefaultPath));

            _ = LoadAsync();
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось убрать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Оставляет запасы только нынешних подписок и ссылки консоли.
    /// </summary>
    /// <remarks>
    /// Ссылка консоли (<c>SubscriptionUrl</c>) — отдельно: nz читает её
    /// и сам, и её запас нужен ему, даже когда в книге её почему-то нет.
    /// </remarks>
    private static void KeepReserves(SubscriptionBook book, AppSettings settings)
    {
        try
        {
            SubscriptionPool.KeepReserves(book.Entries.Select(e => e.Url).Append(settings.SubscriptionUrl));
        }
        catch (Exception)
        {
            // Не стёрлось — сотрётся при следующем входе на вкладку.
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

    private async void OnRefresh(object sender, RoutedEventArgs e) => await LoadAsync(force: true);

    /// <summary>
    /// Гоняет пробу по всем серверам всех подписок.
    /// </summary>
    /// <remarks>
    /// Через локальный вход, без TUN: работающий обход при этом не прерывается,
    /// и права администратора не нужны — хотя у окна они и так есть.
    /// </remarks>
    /// <summary>Замеряет серверы одной подписки.</summary>
    private async void OnCheckOne(object sender, RoutedEventArgs e)
    {
        // Кнопка внутри кнопки-папки: без этого нажатие дойдёт до неё,
        // и папка захлопнется на ровном месте.
        e.Handled = true;

        if (sender is not Button { Tag: string name })
            return;

        var row = _rows.FirstOrDefault(r => r.Entry.Name == name);

        if (row is not null)
            await MeasureAsync([row]);
    }

    private async void OnMeasure(object sender, RoutedEventArgs e) => await MeasureAsync(_rows);

    private async Task MeasureAsync(IReadOnlyList<SubRow> which)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        // Уже прочитанные серверы, с тегами пула, а не свежее чтение панели:
        // замер ложится под тот тег, под которым сервер знает движок, —
        // иначе у совпавших имён из двух подписок замер попадал бы под
        // исходное имя, и автоподбор его не видел. Двойник из соседней
        // подписки — тот же узел под тем же тегом, мерить его дважды незачем.
        // Незамеряемые отсеиваются здесь, а не внутри пробника: иначе
        // счётчик «измерено N из M» считал бы и тех, кого не трогали.
        var servers = which
            .SelectMany(row => row.Pooled)
            .Where(s => s.IsUsableOutbound && s.IsMeasurable)
            .DistinctBy(s => s.Tag)
            .ToList();

        // Выходы WARP замеряются вместе со всеми: в конфиге они лежат рядом
        // с серверами подписки, и знать про них надо то же самое.
        if (settings.WarpEnabled)
        {
            servers.AddRange(Warp.Exits().Where(s => s.IsMeasurable));

            // А незамеряемые пробником — руками самого движка, если он работает.
            // Это не хуже пробника, а лучше: меряется тот выход, через который
            // пойдёт трафик, а не его копия в отдельном процессе.
            await MeasureThroughEngineAsync(Warp.Exits().Where(s => !s.IsMeasurable));
        }

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

        // Полоса и подписи у папок: замер идёт десятками секунд и до этого
        // выглядел зависанием. Строки оживали по одной, а понять, идёт ли
        // ещё что-то или всё кончилось, было не по чему.
        Progress.Maximum = servers.Count;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;

        _measuring.Clear();

        foreach (var server in servers)
            _measuring.Add(server.Tag);

        ShowBusy(which);
        Reshow();

        int done = 0;

        try
        {
            // Движки подняты — всё, что лежит в конфиге, меряет сам движок:
            // без процесса на сервер, как у пробника (владелец 28.09: «ускорь
            // проверку ключей»). Но не залпом — на один вход строго по одному,
            // разные входы параллельно, не больше восьми всего: залп в 26 проверок на один вход Trust с одним
            // ключом, 28.09, владелец увидел как «положил все сервера в trust».
            // Пробнику остаётся то, чего в движке нет.
            var rest = servers;
            var token = _work.Token;

            if (EnginesRunning)
            {
                using var api = new ClashApi();

                if (await api.MembersAsync(SelectorGroup, token) is { } members)
                {
                    var inEngine = members.ToHashSet(StringComparer.Ordinal);
                    var mine = servers.Where(s => inEngine.Contains(s.Tag)).ToList();

                    rest = servers.Where(s => !inEngine.Contains(s.Tag)).ToList();

                    await api.MeasureGentlyAsync(
                        mine.Select(s => (s.Tag, $"{s.Host}:{s.Port}")).ToList(),
                        "http://cp.cloudflare.com/generate_204",
                        TimeSpan.FromSeconds(5),
                        (tag, delay) =>
                        {
                            // Прерванный замер — не «не отвечает».
                            if (token.IsCancellationRequested)
                                return;

                            _health.Set(new ServerHealth
                            {
                                Tag = tag,
                                Success = delay is not null,
                                LatencyMs = delay?.TotalMilliseconds,
                                CheckedAt = DateTimeOffset.Now,
                            });

                            Dispatcher.Invoke(() =>
                            {
                                done++;
                                Progress.Value = done;
                                _measuring.Remove(tag);
                                MeasureButton.Content = $"{done} из {servers.Count}…";
                                Status.Text = $"Измерено {done} из {servers.Count}…";
                                Reshow();
                            });
                        },
                        token);
                }
            }

            await new ProxyProbe(singBox).RunManyAsync(
                rest,
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
                        Progress.Value = done;
                        _measuring.Remove(result.ServerTag);

                        // Счёт и на кнопке тоже. Полоса и строка состояния
                        // стоят вверху раздела, а смотрят во время замера
                        // на список серверов — и оттуда их попросту не видно.
                        MeasureButton.Content = $"{done} из {servers.Count}…";

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
            // Называем причину: замер останавливается при уходе с вкладки,
            // и это единственное объяснение тому, почему у соседних серверов
            // одной подписки замеры разного возраста. Молчаливое «прервано»
            // оставляло человека гадать, что сломалось.
            Status.Text = $"Замер прерван на {done} из {servers.Count}. Он останавливается, "
                + "когда уходишь с вкладки: иначе движки проверки остались бы работать "
                + "без окна. Остальные серверы сохранили прежние замеры.";
        }
        catch (Exception ex)
        {
            Status.Text = "Замер не удался: " + ex.GetBaseException().Message;
        }
        finally
        {
            MeasureButton.IsEnabled = true;
            MeasureButton.Content = "Замерить все";

            Progress.Visibility = Visibility.Collapsed;

            // Незамеренные остались бы с подписью «идёт замер» навсегда,
            // если прогон прервали на середине.
            _measuring.Clear();

            ShowBusy([]);
            Reshow();
        }
    }

    /// <summary>
    /// Замеряет выходы руками работающего движка.
    /// </summary>
    /// <remarks>
    /// Для тех, кого не берёт пробник: у MASQUE учётная запись лежит в кэше
    /// движка, а файл занят им же, и отдельный экземпляр обязан
    /// регистрироваться заново — дозвониться ему для этого не через что.
    /// Движок же меряет свой выход сам, и меряет именно тот, через который
    /// пойдёт трафик.
    /// </remarks>
    private async Task MeasureThroughEngineAsync(IEnumerable<ProxyServer> servers)
    {
        var list = servers.ToList();

        if (list.Count == 0 || !EnginesRunning)
            return;

        using var api = new ClashApi();

        if (!await api.AliveAsync(CancellationToken.None))
            return;

        foreach (var server in list)
        {
            var delay = await api.MeasureAsync(
                server.Tag,
                "http://cp.cloudflare.com/generate_204",
                TimeSpan.FromSeconds(15),
                CancellationToken.None);

            _health.Set(new ServerHealth
            {
                Tag = server.Tag,
                Success = delay is not null,
                LatencyMs = delay?.TotalMilliseconds,
                CheckedAt = DateTimeOffset.Now,
            });
        }

        _health.Save();
        Reshow();
    }

    /// <summary>Работают ли движки прямо сейчас.</summary>
    private static bool EnginesRunning =>
        SupervisorState.Load(SupervisorState.DefaultPath) is { } state && state.IsSupervisorAlive();

    /// <summary>Тег группы, которой движок выбирает выход.</summary>
    private const string SelectorGroup = "auto";

    /// <summary>
    /// Пробует WARP на живом движке и возвращает всё обратно.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Через Clash API, а не через настройки. Закрепление сервера правит
    /// <c>PreferredServer</c>, а оно применяется только при следующем запуске
    /// движков — то есть «попробовать» стоило бы обрыва всего трафика
    /// на полминуты, дважды: туда и обратно. Переключение группы в работающем
    /// движке происходит мгновенно и ничего не роняет.
    /// </para>
    /// <para>
    /// Возврат делается всегда, в том числе когда проба не удалась. Оставить
    /// человека на неработающем выходе, потому что мы не смогли его проверить,
    /// — худшее, чем может кончиться кнопка с надписью «попробовать».
    /// </para>
    /// </remarks>
    private async void OnTryWarp(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        using var api = new ClashApi();

        WarpTryButton.IsEnabled = false;
        WarpTryButton.Content = "пробую…";

        var previous = await api.SelectedAsync(SelectorGroup, CancellationToken.None);

        try
        {
            var exit = Warp.Exits().FirstOrDefault();

            if (exit is null || !await api.SelectAsync(SelectorGroup, exit.Tag, CancellationToken.None))
            {
                Status.Text = "Движок не отозвался. Проверьте, что он работает — «Главная».";
                return;
            }

            Status.Text = $"Весь трафик временно идёт через «{exit.Tag}». Проверяю…";

            // Через сам движок, а не мимо него: вопрос ровно в том, дойдёт ли
            // трафик тем путём, которым пойдёт всё остальное.
            var delay = await api.MeasureAsync(
                exit.Tag,
                "http://cp.cloudflare.com/generate_204",
                TimeSpan.FromSeconds(20),
                CancellationToken.None);

            Status.Text = delay is { } ms
                ? $"{exit.Tag} работает: {ms.TotalMilliseconds:0} мс. Выход вернулся на прежний — "
                  + "чтобы оставить WARP насовсем, выберите его в списке."
                : $"{exit.Tag} не отозвался за двадцать секунд. Туннель MASQUE поднимается "
                  + "не мгновенно: если движки только что запущены, повторите через полминуты.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось попробовать: " + ex.GetBaseException().Message;
        }
        finally
        {
            // Возврат в любом случае, и без оглядки на исход пробы.
            if (!string.IsNullOrEmpty(previous))
                await api.SelectAsync(SelectorGroup, previous, CancellationToken.None);

            WarpTryButton.IsEnabled = true;
            WarpTryButton.Content = "попробовать сейчас";

            Reshow();
        }
    }

    /// <summary>Замеряет выходы WARP.</summary>
    /// <remarks>
    /// Отдельной кнопкой, потому что они живут в карточке, а не в папке
    /// подписки: общая «Замерить все» их берёт, а вот проверить их одних
    /// было нечем.
    /// </remarks>
    /// <summary>
    /// Замеряет выходы WARP руками работающего движка.
    /// </summary>
    /// <remarks>
    /// Через движок, а не пробником: учётная запись MASQUE лежит в его кэше,
    /// а файл занят им же — отдельный экземпляр обязан регистрироваться
    /// заново, и дозвониться ему для этого не через что.
    /// </remarks>
    private async void OnCheckWarp(object sender, RoutedEventArgs e)
    {
        WarpCheckButton.IsEnabled = false;
        WarpCheckButton.Content = "меряю…";
        Status.Text = "Спрашиваю движок о задержке WARP…";

        try
        {
            await MeasureThroughEngineAsync(Warp.Exits());

            var known = Warp.Exits()
                .Select(s => (s.Tag, Health: _health.Find(s.Tag)))
                .ToList();

            Status.Text = known.All(p => p.Health is { Success: true })
                ? string.Join(", ", known.Select(p => $"{p.Tag}: {p.Health!.LatencyMs:0} мс"))
                : "WARP не отозвался. Туннель поднимается не мгновенно — если движки "
                  + "только что запущены, повторите через полминуты.";
        }
        finally
        {
            WarpCheckButton.IsEnabled = true;
            WarpCheckButton.Content = "проверить";
        }
    }

    /// <summary>Отмечает, какие папки сейчас замеряются.</summary>
    private void ShowBusy(IReadOnlyList<SubRow> checking)
    {
        foreach (var row in _rows)
        {
            row.Checking = checking.Contains(row);

            // Занятыми помечаются все, а не только замеряемая: пока идёт один
            // прогон, второй не начинают. Каждый поднимает по пять движков
            // разом, и два вместе дают десять — машине заметно, а быстрее
            // не становится.
            row.Busy = checking.Count > 0;
        }

        Redraw();
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

            // Выходы WARP подписок не касаются: они и так в пуле.
            var owner = row.Owner == WarpOwner
                ? null
                : _rows.FirstOrDefault(r => r.Entry.Name == row.Owner);

            var tag = row.Tag;
            bool switched = owner is not null && !owner.Active;

            // Сервер из подписки вне работы — ставим её в работу, иначе конфиг
            // собрался бы без него. Метки пула от этого могут поменяться
            // (совпадёт имя с сервером соседней), так что тег выбранного
            // берём заново — по месту сервера в его подписке.
            if (switched)
            {
                int index = owner!.Pooled.ToList().FindIndex(s => s.Tag == row.Tag);

                if (owner.IsKeys)
                    SubscriptionBook.SaveKeys(_book.Keys, true);
                else
                    SubscriptionBook.SetWorking(owner.Entry.Url, true);

                _book = SubscriptionBook.Load();
                owner.Entry.InPool = true;
                owner.Active = true;

                ApplyPool(settings);

                if (index >= 0)
                    tag = owner.Pooled[index].Tag;
            }

            (AppSettings.Load(AppSettings.DefaultPath) with { PreferredServer = tag })
                .Save(AppSettings.DefaultPath);

            // Раньше перерисовки, а не после. Выбор уже записан, и движки уже
            // работают со старой настройкой — сказать об этом надо независимо
            // от того, как пройдёт перерисовка. Стояло последним, и любая
            // её заминка съедала уведомление целиком: настройка менялась
            // молча, а человек узнавал об этом в следующий раз.
            this.Offer($"Выбран {tag}");

            Reshow();

            Status.Text = switched
                ? $"Выбран {tag}, и подписка «{row.Owner}» поставлена в работу — иначе "
                  + "его не было бы в конфиге. Применится при следующем запуске движков."
                : $"Выбран {tag}. Применится при следующем запуске движков.";
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

    /// <summary>
    /// Убирает сервер из автоподбора или возвращает (правый щелчок по строке).
    /// </summary>
    /// <remarks>
    /// Владелец 29.09: ОБС у SecureWay отвечают через раз на домашней сети
    /// и сбивают автоподбор. Убранный остаётся в списке — выбрать его руками
    /// можно; меняется с перезапуском движков, как всё, что уходит в конфиг.
    /// </remarks>
    private void OnAutoPickToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag })
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var list = settings.AutoPickExcluded.ToList();
            bool remove = list.Remove(tag);

            if (!remove)
                list.Add(tag);

            settings = settings with { AutoPickExcluded = list };
            settings.Save(AppSettings.DefaultPath);

            Reshow();

            Status.Text = remove
                ? $"«{tag}» снова в автоподборе. Применится при следующем запуске движков."
                : $"«{tag}» убран из автоподбора — выбрать его руками по-прежнему можно. Применится при следующем запуске движков.";

            this.Offer("Автоподбор изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось записать: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Панель подвела, а запаса нет — причина уже словами.</summary>
    private sealed class SubscriptionUnreadException(string message) : Exception(message);
}
