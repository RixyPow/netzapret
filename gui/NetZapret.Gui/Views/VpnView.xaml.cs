using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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

    /// <summary>Протокол словом: «Vless».</summary>
    string Protocol,

    /// <summary>Адрес и порт сервера.</summary>
    string Address,
    string Latency,
    Brush Color,
    string ChooseLabel,
    bool CanChoose,

    /// <summary>Можно ли замерить его отдельным пробником.</summary>
    bool Measurable,

    /// <summary>Кнопка «убрать» — только у отдельных ключей.</summary>
    Visibility RemoveShown = Visibility.Collapsed,

    /// <summary>Пункт меню строки: убрать из автоподбора или вернуть.</summary>
    string AutoPickLabel = "Не брать в автоподбор",

    /// <summary>Надёжность справа: «90 %»; пусто — проверок мало (<see cref="ServerHealth.Reliability"/>).</summary>
    string Reliability = "",

    /// <summary>Подсказка к проценту; null — без подсказки, иначе WPF показал бы пустую.</summary>
    string? ReliabilityTip = null,

    Brush? ReliabilityColor = null,

    /// <summary>Хвост подписи: « · замер 3 мин назад», « · идёт замер» или пусто.</summary>
    string Measured = "",

    /// <summary>Плашка протокола из самого сервера: «TLS XHTTP», «Reality TCP».</summary>
    string Chip = "",

    /// <summary>Через этот сервер трафик идёт сейчас — по движку.</summary>
    Visibility NowShown = Visibility.Collapsed,

    /// <summary>Рамка карточки: у выбранного — цветом кнопки «Выбран».</summary>
    Brush? Edge = null,

    /// <summary>Полоски надёжности, от низкой к высокой: залитая — цветом надёжности, пустая — цветом рамки.</summary>
    Brush? Bar1 = null,
    Brush? Bar2 = null,
    Brush? Bar3 = null,
    Brush? Bar4 = null,
    Brush? Bar5 = null,

    /// <summary>Прозрачность: при WARP подписки на паузе — бледнее.</summary>
    double Fade = 1,

    /// <summary>Метка «вне подбора» у имени: в колонке задержки она не помещалась.</summary>
    Visibility ExcludedShown = Visibility.Collapsed)
{
    /// <summary>Подпись под именем на узком окне: «Vless, адрес · замер …».</summary>
    public string Detail => $"{Protocol}, {Address}{Measured}";

    /// <summary>Вторая строка колонки адреса на широком окне: «Vless · замер …».</summary>
    public string Sub => $"{Protocol}{Measured}";

    public Visibility ChipShown => Chip.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ChosenShown => CanChoose ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ChooseShown => CanChoose ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Строка в папке пула (<see cref="ServerPool.Id"/>); <c>null</c> — в папке подписки.
    /// </summary>
    /// <remarks>
    /// В пуле «Выбрать» закрепляет сервер в этом пуле, а не в пуле по умолчанию.
    /// </remarks>
    public string? PoolId { get; init; }
}

/// <summary>Плитка быстрой смены сервера (макет владельца 01.10); тег <c>null</c> — «Авто».</summary>
public sealed record QuickTile(
    string? Tag,
    string Title,
    string Latency,
    Brush Color,
    BitmapImage? Flag,
    Visibility FlagShown,
    string Mark,
    FontFamily MarkFont,
    Visibility MarkShown,
    Brush Edge,
    Brush Fill,
    string Tip);

/// <summary>Строка «Недавних серверов»: флаг, а нет его — знак (буквы страны, у WARP — облачко).</summary>
public sealed record RecentRow(
    string Name,
    string Mark,
    FontFamily MarkFont,
    double MarkSize,
    Brush MarkBrush,
    Visibility MarkShown,
    BitmapImage? Flag,
    Visibility FlagShown,
    string Latency,
    Brush Color,
    FontWeight Weight);

/// <summary>Папка одной подписки.</summary>
public sealed class SubRow : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Перечитать все привязки папки.</summary>
    /// <remarks>
    /// Папка — один и тот же объект между пересборками, а поля у неё меняются
    /// на месте. Замена в списке тем же объектом привязки не трогает (опыт
    /// 13.10): после 0.15.3 папка так и показывала «читаю…» над прочитанной
    /// подпиской. Поля по одному не извещают — пересборка всё равно меняет
    /// их пачкой.
    /// </remarks>
    public void Refresh() =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));

    public required SubscriptionEntry Entry { get; init; }

    /// <summary>Папка отдельных ключей, а не подписки (0.9.0).</summary>
    public bool IsKeys { get; init; }

    /// <summary>У папки ключей — какой ключ дал какой сервер: по нему ключ и убирают.</summary>
    public IReadOnlyList<KeyServer> KeyServers { get; set; } = [];

    public string Name => Entry.Name;

    public bool Open { get; set; }

    public bool Active { get; set; }

    public string Detail { get; set; } = "читаю…";

    /// <summary>
    /// Что поставщик шлёт заголовками сверх квоты и срока (15.10, как в Happ):
    /// поддержка, страница подписки, объявление.
    /// </summary>
    public string? SupportUrl { get; set; }

    public string? WebPageUrl { get; set; }

    public string? Announce { get; set; }

    /// <summary>Срок кончается или кончился — строкой цвета предупреждения.</summary>
    public string? Warning { get; set; }

    public Visibility SupportShown => SupportUrl is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WebPageShown => WebPageUrl is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AnnounceShown => Announce is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility WarningShown => Warning is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Полоска расхода, как в Happ (владелец 19.10), и восклицательный знак
    /// при слишком быстром темпе (<see cref="SubscriptionUsage"/>); <c>null</c> —
    /// подписка не прочитана или это папка ключей: у них квоты нет.
    /// </summary>
    public UsageForecast? Usage { get; set; }

    public Visibility UsageShown => Usage is null ? Visibility.Collapsed : Visibility.Visible;

    public string UsageLabel => Usage?.Label ?? string.Empty;

    public string UsageExpires => Usage?.Expires ?? string.Empty;

    /// <summary>Израсходованная часть полоски; без лимита — пустая, как у Happ.</summary>
    public GridLength UsedStar => new(Usage?.Fraction ?? 0, GridUnitType.Star);

    public GridLength LeftStar => new(1 - (Usage?.Fraction ?? 0), GridUnitType.Star);

    public Brush UsageFill => (Brush)Application.Current.FindResource(Usage?.Warning is null ? "AccentFill" : "WarnFill");

    public string? PaceWarning => Usage?.Warning;

    public Visibility PaceShown => Usage?.Warning is null ? Visibility.Collapsed : Visibility.Visible;

    public string UsageTip => Usage is not { } usage
        ? string.Empty
        : $"Израсходовано: {usage.Label}"
            + (usage.PerDay is { } perDay ? $"\nЗа последнюю неделю — {SubscriptionUsage.Size(perDay)} в сутки" : "\nТемп появится, когда накопится история хотя бы за полсуток")
            + (usage.Warning is { } warning ? "\n\n" + warning : string.Empty);

    public string SupportTip => SupportUrl is { } url && url.Contains("t.me/", StringComparison.OrdinalIgnoreCase)
        ? "Поддержка поставщика в Telegram"
        : "Поддержка поставщика";

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

    /// <summary>Прозрачность папки: при WARP подписки на паузе — бледнее.</summary>
    public double Fade { get; set; } = 1;

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

        // Верх вкладки и подпись внизу размечены в Holder, а живут первым
        // и последним элементом виртуального списка (Page): иначе их
        // пришлось бы описывать шаблонами, и имена элементов пропали бы.
        Holder.Children.Clear();

        // Заголовок пулов — с первого показа, а не с первой перерисовки:
        // иначе он вставал бы над подписками скачком.
        _poolsOn = AppSettings.Load(AppSettings.DefaultPath).SelectiveRouting == true;
        ShowPage();

        SizeChanged += (_, e) => ShowWidth(e.NewSize.Width);

        Loaded += async (_, _) =>
        {
            // Фокус — на вкладку, иначе Ctrl+V («Из буфера») не дошёл бы до неё:
            // после перехода из меню фокус остаётся на пункте меню.
            Focus();

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
            await ProbeExitAsync();
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
            // «Движки остановлены» говорит карточка выхода — здесь не повторяем.
            ShowPickLine(_pickBase);
            bool wasRunning = _live.Running;
            _live = (false, null, false);

            if (wasRunning)
                Reshow();
            else
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

        bool moved = _live.Server != server || !_live.Running;
        _live = (true, server, automatic);

        // «Сейчас» у строки выхода — переезжает вместе с ним.
        if (moved)
            Reshow();
        else
            ShowCurrent();

        // Сам выход и расхождение с выбором — в карточке выше (ShowCurrent):
        // здесь одна короткая строка, чтобы карточка ленты не прыгала по высоте
        // (владелец, 01.10).
        ShowPickLine(_pickBase);
    }

    /// <summary>Живая проверка текущего выхода: тег, ответил ли, задержка и когда.</summary>
    private (string Tag, bool Ok, double? Ms, DateTimeOffset At)? _probe;

    private bool _probing;
    private int _probeTick;

    /// <summary>Каждый какой тик таймера выхода (3 с) проверять выход: раз в 15 с.</summary>
    private const int ProbeEvery = 5;

    /// <summary>
    /// Пока вкладка открыта — один запрос через движок к текущему выходу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Владелец 08.10: карточка выхода «должна отображать сервер и его статус
    /// в реальном времени». Имя движок называл и прежде (раз в 3 с), а «В работе»
    /// значило только «движки запущены», задержка же была последним замером —
    /// иной раз получасовой давности. Сторож, который проверяет выход сам,
    /// у владельца выключен («Проверка сервера выключена»).
    /// </para>
    /// <para>
    /// Один запрос к одному выходу раз в 15 с и сразу при смене выхода — не залп
    /// (CLAUDE.md, «Продавцов не бить залпом»): сторож делает так же раз в 30 с,
    /// Happ — 2–4 сервера в минуту. Пока идёт «Замерить все», не проверяем.
    /// Удачный замер движок помнит и сам; неудачный — нет, поэтому ответ
    /// держим здесь и накладываем поверх (<see cref="Seen"/>).
    /// </para>
    /// </remarks>
    private async Task ProbeExitAsync()
    {
        var (running, tag, _) = _live;

        if (!running || tag is null || _probing || _measuring.Count > 0
            || tag.StartsWith("auto", StringComparison.OrdinalIgnoreCase) || tag == TunnelBypass.DirectTag)
        {
            return;
        }

        // Новый выход — сразу; тот же — раз в ProbeEvery тиков.
        if (_probe?.Tag == tag && ++_probeTick % ProbeEvery != 0)
            return;

        _probeTick = 0;
        _probing = true;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);

            using var api = new ClashApi();
            var delay = await api.MeasureAsync(tag, Ping.UrlOf(settings), Ping.TimeoutOf(settings), CancellationToken.None);

            if (!IsLoaded)
                return;

            _probe = (tag, delay is not null, delay?.TotalMilliseconds, DateTimeOffset.Now);
            ShowCurrent();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            // Движок не ответил на сам вопрос — о выходе это ничего не говорит.
        }
        finally
        {
            _probing = false;
        }
    }

    /// <summary>Сколько назад, с секундами: «8 с», «2 мин» — проверка идёт раз в 15 с.</summary>
    private static string SecondsAgo(DateTimeOffset at)
    {
        var passed = DateTimeOffset.Now - at;

        return passed.TotalSeconds < 60 ? $"{Math.Max(0, passed.TotalSeconds):0} с" : $"{passed.TotalMinutes:0} мин";
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

        // При включённом WARP закреплён он (Warp.PreferredExit).
        var preferred = Warp.PreferredExit(settings);
        bool pinned = !string.IsNullOrWhiteSpace(preferred);
        var tag = running ? live : preferred;

        // Имя группы вместо сервера: автоподбор ещё не выбрал — до первого
        // своего замера ему выбирать не из чего. Показывать «auto-latency»
        // как имя сервера значило бы выдать служебное слово за выход.
        bool choosing = running && tag is not null && tag.StartsWith("auto", StringComparison.OrdinalIgnoreCase);

        if (choosing)
            tag = null;

        bool desyncOnly = !running && DesyncOnly;

        CurrentCaption.Text = running
            ? "Сейчас трафик идёт через"
            : desyncOnly
                ? "Режим «Десинк» — туннель не поднимается"
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
            ShowStats(null, null, null);
        }
        else
        {
            var (country, name) = CountryTag.Split(tag);
            CurrentName.Text = name.Length > 0 ? name : tag;

            if (tag == Warp.MasqueTag)
                ShowCloud();
            else
                ShowFlag(country);

            // Только как выход выбран: подписка, протокол и задержка — цифрами
            // ниже (макет 01.10), повторять их строкой незачем.
            //
            // Закреплён ли сервер, знают настройки, а не селектор: сторож ставит
            // серверы прямо в него и при автоподборе (TunnelStatus.Standing).
            // Замену выбранному серверу называет надзор: со стороны её
            // не отличить от выбора, который ещё не применился.
            CurrentDetail.Text = !running
                ? string.Empty
                : TunnelStatus.Standing(tag, preferred) switch
                {
                    ExitStanding.Pinned => tag == Warp.MasqueTag ? "Включён WARP." : "Закреплён вами.",
                    ExitStanding.Other => StandInRemark() is { } remark
                        ? char.ToUpper(remark[0]) + remark[1..] + "."
                        : "Выбор в настройках ещё не применён — перезапустите движки.",
                    _ => "Выбран автоподбором.",
                };
        }

        // Живая проверка этого выхода (ProbeExitAsync) — если есть и свежая.
        var probe = running && tag is not null && _probe is { } p && p.Tag == tag
            && DateTimeOffset.Now - p.At < TimeSpan.FromMinutes(1)
            ? p
            : ((string Tag, bool Ok, double? Ms, DateTimeOffset At)?)null;

        if (probe is { } checkedNow && CurrentDetail.Text.Length > 0)
            CurrentDetail.Text += $" Проверен {SecondsAgo(checkedNow.At)} назад.";

        // Метка состояния: зелёная, пока выход отвечает; красная — не ответил
        // на последнюю проверку; без проверки — по тому, работают ли движки.
        var key = probe is { Ok: false } ? "Danger" : running ? "Accent" : "Faint";
        CurrentDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, key);
        CurrentState.SetResourceReference(TextBlock.ForegroundProperty, key);
        CurrentState.Text = probe switch
        {
            { Ok: true, Ms: { } ms } => $"Отвечает · {ms:0} мс",
            { Ok: true } => "Отвечает",
            { Ok: false } => "Не отвечает",
            _ => running ? "В работе" : desyncOnly ? "Туннель выключен" : "Движки остановлены",
        };

        ShowStats(tag, found.Owner, found.Server);
        ShowRecent(running ? live : null);
    }

    /// <summary>
    /// Цифры выхода под его именем: задержка, протокол, реализация, источник (макет 01.10).
    /// </summary>
    private void ShowStats(string? tag, string? owner, ProxyServer? server)
    {
        StatLatency.Text = tag is not null && Seen(tag) is { Success: true, LatencyMs: { } ms } ? $"{ms:0} мс" : "—";
        StatProtocol.Text = server is null ? "—" : Transport(server);
        StatKind.Text = server?.ProtocolName ?? "—";

        bool warp = owner == "WARP";
        var source = owner is null ? null : warp ? "WARP" : owner;
        StatSource.Text = source ?? "—";

        // У WARP значок повторил бы имя.
        CurrentSource.Text = source ?? string.Empty;
        CurrentSourcePill.Visibility = source is null || warp ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Защита и транспорт словами, как их пишут продавцы: «TLS XHTTP», «Reality TCP».</summary>
    private static string Transport(ProxyServer server)
    {
        var security = server.Security?.ToLowerInvariant() switch
        {
            "tls" => "TLS",
            "reality" => "Reality",
            null or "" or "none" => null,
            var other => other,
        };

        var transport = string.IsNullOrWhiteSpace(server.Transport) ? null : server.Transport.ToUpperInvariant();

        // У протоколов на QUIC, у MASQUE и WireGuard транспорт свой и словом
        // из ссылки не называется.
        transport = server.Protocol switch
        {
            ProxyProtocol.Masque => "HTTP/2",
            ProxyProtocol.Hysteria2 or ProxyProtocol.Hysteria or ProxyProtocol.Tuic => "QUIC",
            ProxyProtocol.Wireguard => "UDP",
            _ => transport,
        };

        var parts = new[] { security, transport }.Where(p => !string.IsNullOrEmpty(p));
        var text = string.Join(" ", parts);

        return text.Length > 0 ? text : "—";
    }

    /// <summary>Цвет облачка Cloudflare — его знак.</summary>
    private static readonly Brush Cloud = Frozen(new SolidColorBrush(Color.FromRgb(0xF3, 0x80, 0x20)));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>У WARP вместо флага облачко (владелец, 01.10: на месте флага стояла точка).</summary>
    private void ShowCloud()
    {
        CurrentFlag.SetResourceReference(Border.BackgroundProperty, "Raised");
        CurrentCountry.Text = string.Empty;
        CurrentCloud.Visibility = Visibility.Visible;
    }

    /// <summary>Флаг картинкой, а нет картинки — буквами страны.</summary>
    private void ShowFlag(string country)
    {
        CurrentCloud.Visibility = Visibility.Collapsed;

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

        ShowPage();

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
            // Разные панели — разом, одна панель — по очереди (SubscriptionPool.ByPanel):
            // три запроса в одну секунду с одного адреса панели не нравятся, а
            // по одной на всех молчащая панель держала соседей. 22.10 Secureway
            // не отвечала 12 с, и TRUST (0,4–0,7 с) ждала её — обе на «читаю…».
            //
            // Ждущие называются по имени: при зависшей панели «Читаю подписки…»
            // не говорит даже того, которая не отвечает.
            bool everyRead = true;
            var waiting = _rows.Where(r => !r.IsKeys).Select(r => r.Entry.Name).ToList();

            void ShowWaiting() => Status.Text = waiting.Count switch
            {
                0 => string.Empty,
                1 => $"Читаю подписку «{waiting[0]}»…",
                _ => $"Читаю подписки: «{string.Join("», «", waiting)}»…",
            };

            ShowWaiting();

            // Продолжения FillAsync — в потоке окна: строки, флаг и список
            // ждущих меняются по одному, хоть панели и отвечают вперемешку.
            await SubscriptionPool.ByPanel(_rows, r => r.IsKeys ? string.Empty : r.Entry.Url, async row =>
            {
                token.ThrowIfCancellationRequested();

                everyRead &= await FillAsync(row, settings, token, force);

                waiting.Remove(row.Entry.Name);
                ShowWaiting();
            });

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

            // Числа — в сводке у заголовка; здесь только беда.
            Status.Text = servers == 0 && _rows.Count > 0
                ? "Ни одна подписка не отдала серверов."
                : string.Empty;
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
                    ? $"{read.Error ?? "панель подвела"} — серверы из запаса {when}"
                    : $"обновлена {when}",
                $"{usable.Count} серверов",
            };

            if (skipped > 0)
                parts.Add($"ещё {skipped} sing-box не поддерживает");

            // Квота и срок — полоской под именем (19.10, как в Happ), а не словами
            // в этой строке. Темп — по истории расхода, которую пишет каждое
            // чтение панели, и окном, и надзором.
            row.Usage = SubscriptionUsage.Forecast(info, SubscriptionUsage.Samples(row.Entry.Url), DateTimeOffset.Now);

            // Ключи (KeyRing) заголовков не шлют: у них этих полей нет и не будет.
            row.SupportUrl = info.SupportUrl;
            row.WebPageUrl = info.WebPageUrl;
            row.Announce = info.Announce;
            row.Warning = info.ExpiryWarningText(DateTimeOffset.Now);

            // Поставщик перенёс подписку — ссылка в книге новая, и в этой
            // книге тоже: вкладка держит свою и записала бы старую обратно.
            if (read.Source != SubscriptionReadSource.Reserve)
            {
                SubscriptionBook.Follow(_book, row.Entry.Url, info.NewUrl);
                SubscriptionNotices.RemindExpiry(row.Entry.Name, row.Entry.Url, info);
            }

            // Основная панель молчала, а запасной адрес поставщика ответил.
            if (read.Source == SubscriptionReadSource.Panel && read.Error is { } rescued)
                parts.Add(rescued);

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
                Journal.Write("подписка", $"«{row.Entry.Name}»: {read.Error ?? "панель подвела"}, взят запас {when}");

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
            row.Usage = null;

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
                            Name = WithoutChip(name.Length > 0 ? name : own[j].Tag, shown.Chip),
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
            var (country, name) = CountryTag.Split(server.Tag);

            // WARP выпускает в ближайшем городе Cloudflare, из России — в Москве
            // (замер 01.10: loc=RU, colo=DME). Флага в имени нет, а имя менять
            // нельзя — к нему привязана учётная запись в кэше движка.
            if (server.IsSelfRegistering && country.Length == 0)
                country = "RU";

            var flag = country.Length == 2 ? FlagImages.For(country) : null;
            var chip = Transport(server);
            chip = chip == "—" ? string.Empty : chip;

            var row = new ServerRow(
                server.Tag,
                owner,
                WithoutChip(name, chip),
                country,

                // Ровно одно из двух: картинка либо буквы. Показать оба
                // значило бы сказать одно и то же дважды в одной строке.
                country.Length == 0 || flag is not null ? Visibility.Collapsed : Visibility.Visible,
                flag,
                flag is null ? Visibility.Collapsed : Visibility.Visible,
                server.ProtocolName,
                $"{server.Host}:{server.Port}",
                string.Empty,
                Brushes.Transparent,
                "выбрать",
                true,
                server.IsMeasurable,
                Chip: chip);

            return Decorate(row, settings);
        });

        // Сортировка здесь не применяется: список отдаётся в порядке подписки,
        // а порядок показа выбирается в Reshow. Иначе исходный порядок негде
        // было бы взять обратно.
        return rows.ToList();
    }

    /// <summary>
    /// Всё в строке сервера, что зависит от замеров, выбора и движка, — заново
    /// при каждой перерисовке.
    /// </summary>
    /// <remarks>
    /// Одно место на чтение подписки и на перерисовку: прежде эти два пути
    /// красили строку каждый по-своему. Возраст замера считается здесь же —
    /// иначе «замер 17 мин назад» висел бы и на только что замеренном.
    /// </remarks>
    private ServerRow Decorate(ServerRow server, AppSettings settings)
    {
        bool chosen = server.Tag == settings.PreferredServer;
        bool excluded = settings.AutoPickExcluded.Contains(server.Tag, StringComparer.Ordinal);
        bool known = server.Measurable || Seen(server.Tag) is not null;

        // Незамеряемого пробником меряет сам движок — и тогда у него обычный
        // замер, как у всех. «Только в работе» остаётся лишь на то время,
        // пока движки стоят и спросить некого.
        var latency = known ? Latency(server.Tag) : "только в работе";

        var (reliability, tip, color, bars) = ReliabilityOf(server.Tag);
        var empty = (Brush)FindResource("Border");
        Brush Bar(int n) => n <= bars ? color : empty;

        var (running, live, _) = _live;

        return server with
        {
            Color = (Brush)FindResource(known ? Key(server.Tag) : "Faint"),
            Latency = latency,
            ChooseLabel = chosen ? "выбран" : "выбрать",
            CanChoose = !chosen,
            AutoPickLabel = excluded ? "Вернуть в автоподбор" : "Не брать в автоподбор",
            Measured = Measured(server.Tag),
            Reliability = reliability,
            ReliabilityTip = tip,
            ReliabilityColor = color,
            Bar1 = Bar(1),
            Bar2 = Bar(2),
            Bar3 = Bar(3),
            Bar4 = Bar(4),
            Bar5 = Bar(5),
            NowShown = running && live == server.Tag ? Visibility.Visible : Visibility.Collapsed,
            Edge = (Brush)FindResource(chosen ? "AccentFill" : "Border"),
            Fade = _paused ? 0.55 : 1,
            ExcludedShown = excluded ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    /// <summary>
    /// Имя без хвоста, повторяющего плашку протокола: «Германия — TLS XHTTP» → «Германия».
    /// </summary>
    /// <remarks>
    /// Плашка берётся из самого сервера, а не из имени (12.10): так она есть
    /// и у подписок, не пишущих протокол в имени. Хвост срезается, только
    /// если он с плашкой совпадает; иначе — «Польша — XDNS» — имя остаётся
    /// целиком: что там написал продавец, мы не знаем.
    /// </remarks>
    internal static string WithoutChip(string name, string chip)
    {
        if (chip.Length == 0 || !name.EndsWith(chip, StringComparison.OrdinalIgnoreCase))
            return name;

        var head = name[..^chip.Length].TrimEnd();
        var trimmed = head.TrimEnd('—', '–', '-', '|', '·', ':', ',').TrimEnd();

        // Срезаем только хвост за разделителем: «VLESS TLS XHTTP» без
        // разделителя — само имя, а не подпись к нему.
        return trimmed.Length > 0 && trimmed.Length < head.Length ? trimmed : name;
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

    /// <summary>
    /// Ранг для сортировки по задержке: живые, затем — при «Сначала стабильные» —
    /// живые, но мигающие, затем незамеренные и мёртвые.
    /// </summary>
    private int Rank(ServerRow row) => Seen(row.Tag) switch
    {
        { Success: true, Flaky: true } when _stableFirst => 1,
        { Success: true } => 0,
        null => 2,
        _ => 3,
    };

    /// <summary>«Сначала стабильные» — из настроек, обновляется при каждой пересборке строк.</summary>
    private bool _stableFirst = true;

    private double Ms(ServerRow row) => Seen(row.Tag)?.LatencyMs ?? double.MaxValue;

    /// <summary>
    /// Плоский список вкладки: верх, папки, серверы раскрытых папок, подпись.
    /// </summary>
    /// <remarks>
    /// Список рисует только видимые строки (12.10: подписка на 403 сервера
    /// вешала вкладку на минуты, а 455 карточек разом стоили 0,8 с на каждую
    /// пересборку). Контейнеры не переиспользуются (VirtualizationMode
    /// Standard): папки — те же объекты между пересборками, и
    /// переиспользованный контейнер с тем же Content не перечитал бы их привязки.
    /// </remarks>
    private void ShowPage()
    {
        var items = new List<object> { TopBlock };

        if (_poolsOn)
            items.Add(PoolsBlock);

        foreach (var pool in _poolRows)
        {
            pool.Fade = _paused ? 0.55 : 1;
            items.Add(pool);

            if (pool.Open)
                items.AddRange(pool.Servers);
        }

        items.Add(SubsBlock);

        foreach (var row in _rows)
        {
            row.Fade = _paused ? 0.55 : 1;
            items.Add(row);

            if (row.Open)
                items.AddRange(row.Servers);
        }

        items.Add(Footer);

        // По месту, а не новым списком: замена ItemsSource сбрасывала
        // прокрутку наверх (снимок 12.10: 633 → 15 после каждой пересборки),
        // а пересборка идёт и раз в девять секунд, и при смене выхода.
        // Строки серверов — новые записи, их заменяем, и список заново рисует
        // только видимые из них. Папки — те же объекты с новыми полями:
        // замена тем же объектом их не перерисовывает, папка извещает сама
        // (SubRow.Refresh). Верх и подпись — одни и те же элементы, не трогаем.
        if (Page.ItemsSource != _items)
            Page.ItemsSource = _items;

        for (int i = 0; i < items.Count; i++)
        {
            // Элемент окна (плашки пулов и подписок, подпись) в списке может
            // стоять лишь в одном месте: два места — одно дитя у двух
            // родителей, и список падает (19.10: свернул папку пула — плашка
            // подписок встала выше, ещё не уйдя снизу). Сперва вынимаем его
            // со старого места — оно всегда дальше: всё до i уже совпало.
            if (items[i] is UIElement element
                && (i >= _items.Count || !ReferenceEquals(_items[i], element))
                && _items.IndexOf(element) is var old and >= 0)
            {
                _items.RemoveAt(old);
            }

            if (i >= _items.Count)
                _items.Add(items[i]);
            else if (!ReferenceEquals(_items[i], items[i]))
                _items[i] = items[i];
            else if (items[i] is SubRow folder)
                folder.Refresh();
            else if (items[i] is PoolRow pool)
                pool.Refresh();
        }

        while (_items.Count > items.Count)
            _items.RemoveAt(_items.Count - 1);
    }

    /// <summary>Содержимое списка вкладки — постоянное, меняется по месту (ShowPage).</summary>
    private readonly System.Collections.ObjectModel.ObservableCollection<object> _items = [];

    /// <summary>Подписки на паузе — туннель идёт через WARP.</summary>
    private bool _paused;

    /// <summary>Плашка и адрес колонками — на широком окне; на узком подпись уходит под имя.</summary>
    public static readonly DependencyProperty WideShownProperty = DependencyProperty.Register(
        nameof(WideShown), typeof(Visibility), typeof(VpnView), new PropertyMetadata(Visibility.Visible));

    public Visibility WideShown
    {
        get => (Visibility)GetValue(WideShownProperty);
        set => SetValue(WideShownProperty, value);
    }

    public static readonly DependencyProperty NarrowShownProperty = DependencyProperty.Register(
        nameof(NarrowShown), typeof(Visibility), typeof(VpnView), new PropertyMetadata(Visibility.Collapsed));

    public Visibility NarrowShown
    {
        get => (Visibility)GetValue(NarrowShownProperty);
        set => SetValue(NarrowShownProperty, value);
    }

    /// <summary>
    /// Ширина вкладки, с которой плашка протокола и адрес встают колонками.
    /// </summary>
    /// <remarks>
    /// У владельца окно 1536 и меню слева — вкладка ~1240. Колонки строки
    /// сервера вместе с отступами — ~800 точек, и имени нужно ещё ~250.
    /// </remarks>
    private const double WideFrom = 1100;

    private void ShowWidth(double width)
    {
        bool wide = width >= WideFrom;
        WideShown = wide ? Visibility.Visible : Visibility.Collapsed;
        NarrowShown = wide ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Redraw()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);

        // Пулы — из тех же строк подписок, уже раскрашенных.
        ShowPools(settings);
        ShowPage();

        // Лента и сводка строятся из тех же строк — вместе с ними.
        ShowQuick(settings);
        ShowSummary();

        // Надпись — по тому, что раскрыто, а не по прошлому нажатию: вкладка
        // пересоздаётся при каждом заходе, а раскрытые папки хранятся в книге
        // (владелец, 01.10: «багается при переходе между страницами»).
        ShowAllButton.Content = _rows.Count > 0 && _rows.All(r => r.Open) ? "Скрыть все" : "Показать все";
    }

    private DispatcherTimer? _reshowTimer;
    private DateTime _reshowAllowedAt;

    /// <summary>
    /// Пересборка на каждом ответе замера — но не чаще, чем окно её тянет.
    /// </summary>
    /// <remarks>
    /// 12.10 у владельца подписка на 403 сервера: «Замерить все» пересобирал
    /// весь список (455 карточек заново) на каждый ответ, и поток окна был
    /// занят 59 с подряд — окно «зависло полностью». Теперь пересборка —
    /// не чаще раза в секунду и не раньше, чем через четыре своих
    /// длительности: окно занято ею не больше пятой части времени.
    /// Последний ответ замера показывается пересборкой после него.
    /// </remarks>
    private void ReshowSoon()
    {
        var now = DateTime.UtcNow;

        if (now >= _reshowAllowedAt)
        {
            ReshowTimed();
            return;
        }

        if (_reshowTimer is { IsEnabled: true })
            return;

        _reshowTimer ??= new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
        _reshowTimer.Interval = _reshowAllowedAt - now;
        _reshowTimer.Tick -= OnReshowTimer;
        _reshowTimer.Tick += OnReshowTimer;
        _reshowTimer.Start();
    }

    private void OnReshowTimer(object? sender, EventArgs e)
    {
        _reshowTimer?.Stop();
        ReshowTimed();
    }

    private void ReshowTimed()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Reshow();

        var pause = TimeSpan.FromTicks(Math.Max(TimeSpan.FromSeconds(1).Ticks, clock.Elapsed.Ticks * 4));
        _reshowAllowedAt = DateTime.UtcNow + pause;
    }

    /// <summary>Пересобирает строки серверов, не перечитывая подписки.</summary>
    private void Reshow()
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        _stableFirst = settings.StableFirst;

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
                .Select(server => Decorate(server, settings))
                .ToList();
        }

        Redraw();
        ShowCurrent();
    }

    /// <summary>Хвост подписи сервера: возраст замера или «идёт замер».</summary>
    /// <remarks>
    /// Считается заново при каждой перерисовке, а не один раз при чтении
    /// подписки. Иначе «замер 17 мин назад» так и висел до перезахода
    /// на вкладку — в том числе на сервере, который только что замерили.
    /// </remarks>
    private string Measured(string tag)
    {
        if (_measuring.Contains(tag))
            return " · идёт замер";

        return Seen(tag) is { } known ? $" · замер {Ago(known.CheckedAt)}" : string.Empty;
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

        if (_engine.TryGetValue(tag, out var engine) && (known is null || known.CheckedAt < engine.At))
        {
            known = (known ?? new ServerHealth { Tag = tag, Success = true, CheckedAt = engine.At })
                with { Success = true, LatencyMs = engine.Ms, CheckedAt = engine.At };
        }

        // Живая проверка текущего выхода свежее всего прочего — и неудачная
        // тоже: её движок не помнит (ProbeExitAsync).
        if (_probe is { } live && live.Tag == tag && (known is null || known.CheckedAt < live.At))
        {
            known = (known ?? new ServerHealth { Tag = tag, Success = live.Ok, CheckedAt = live.At })
                with { Success = live.Ok, LatencyMs = live.Ms, CheckedAt = live.At };
        }

        return known;
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

    /// <summary>
    /// Надёжность сервера справа в строке (владелец 12.10) — текст, подсказка, цвет.
    /// </summary>
    /// <remarks>
    /// По файлу замеров, а не по <see cref="Seen"/>: движок помнит одни удачи,
    /// и с его историей процент врал бы в пользу сервера. Ниже 80 % — жёлтым,
    /// как «через раз» (<see cref="ServerHealth.Flaky"/>), ниже 50 — красным.
    /// </remarks>
    private (string Text, string? Tip, Brush Color, int Bars) ReliabilityOf(string tag)
    {
        if (_health.Find(tag) is not { Reliability: { } percent } history)
            return (string.Empty, "Надёжность: проверок пока меньше трёх", (Brush)FindResource("Muted"), 0);

        var tip = $"Надёжность {percent} %: ответил в {history.Recent.Count(ok => ok)} из {history.Recent.Count} "
            + "последних проверок — замеров этой вкладки и сторожа";

        // Пять полосок по 20 %; хоть один ответ — хоть одна полоска.
        int bars = percent == 0 ? 0 : Math.Max(1, (int)Math.Round(percent / 20.0));
        var color = (Brush)FindResource(percent >= 80 ? "Accent" : percent >= 50 ? "Warn" : "Danger");

        return ($"{percent} %", tip, color, bars);
    }

    private string Key(string tag) => Seen(tag) switch
    {
        // Отвечает через раз — жёлтым (владелец 29.09): ОБС у SecureWay
        // отвечали 2–3 раза из 5, и зелёный на таком врал бы.
        { Flaky: true } => "Warn",
        { Success: true } => "Accent",

        // Обычно отвечает, промахнулась последняя проверка — жёлтым, а не
        // красным: красный рядом с «80 %» читался как «мёртв» (12.10).
        { Success: false, Reliability: >= 80 } => "Warn",
        { Success: false } => "Danger",
        _ => "Faint",
    };

    private string Latency(string tag) => Seen(tag) switch
    {
        { Flaky: true, LatencyMs: { } flaky } => $"{flaky:0} мс · через раз",
        { Flaky: true } => "через раз",
        { Success: true, LatencyMs: { } ms } => $"{ms:0} мс",
        { Success: true } => "отвечает",
        { Success: false, Reliability: >= 80 } => "промах последней",
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

        // Коротко, одной строкой (владелец, 01.10: длинная подпись меняла
        // высоту карточки). Имя закреплённого — без флага-букв из тега.
        _pickBase = settings.WarpEnabled
            ? "На паузе: туннель идёт через WARP."
            : auto
                ? "Автоподбор: быстрейший из живых."
                : $"Закреплён: {CountryTag.Split(pinned!).Name}. «Авто» вернёт автоподбор.";

        // Сторож выключен настройкой — сказать здесь, а не только в окне настроек.
        // 30.09 у владельца стояло «не проверять», заминки сервера никто
        // не замечал, а вкладка обещала «быстрейший из живых».
        if (settings.ExitCheckSeconds <= 0 && !settings.WarpEnabled)
            _pickBase += " Проверка сервера выключена.";

        ShowPickLine(_pickBase);
        ForeignSwitch.IsChecked = settings.ForeignExitsOnly;
        StableSwitch.IsChecked = settings.StableFirst;

        // Выход — следом, не дожидаясь таймера: иначе первые четверть минуты
        // на вкладке строка была бы без него.
        _ = ShowExitAsync();
    }

    /// <summary>
    /// Карточка WARP: выключатель пути и что он сейчас значит.
    /// </summary>
    /// <remarks>
    /// С 01.10 WARP — выбор пути, а не запасной выход (Warp.TunnelExits):
    /// включён — подписки на паузе, лента серверов и список тускнеют.
    /// </remarks>
    private void ShowWarp(AppSettings settings)
    {
        bool on = settings.WarpEnabled;

        WarpSwitch.IsChecked = on;
        WarpWord.Text = on ? "Включён" : "Выключен";
        WarpDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, on ? "Accent" : "Faint");

        var exit = Warp.Exits()[0];
        var measured = Seen(exit.Tag) is { Success: true, LatencyMs: { } ms } ? $" · {ms:0} мс" : string.Empty;

        WarpLine.Text = on
            ? $"Туннель идёт только через WARP, подписки на паузе. Выход в Москве{measured}."
            : "Бесплатный выход через сеть Cloudflare. Включите — и туннель пойдёт через него вместо подписок.";

        // Замер — руками движка, а он знает WARP, только когда тот в конфиге.
        // Кнопка на месте всегда: пропадая, она не говорила, почему её нет.
        WarpCheckButton.IsEnabled = on && EnginesRunning;
        WarpCheckButton.ToolTip = !on
            ? "Замер пинга WARP — когда он включён и движки работают"
            : EnginesRunning
                ? "Замерить пинг WARP"
                : "Замер пинга WARP — когда движки работают";

        // Подписки и лента на паузе — видно сразу, а не только в подписи.
        PausedLine.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _paused = on;
        QuickCard.Opacity = on ? 0.55 : 1;
        QuickServers.IsEnabled = !on;
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
                ? "WARP включён: туннель пойдёт только через него, подписки на паузе. "
                  + "Применится при следующем запуске движков."
                : "WARP выключен: туннель вернётся к подпискам. Применится при следующем запуске движков.";

            ShowPick(next);
            ShowQuick(next);

            this.Offer("WARP переключён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось переключить: " + ex.GetBaseException().Message;
        }
    }

    private void ChooseAuto()
    {
        // Выключить автоподбор плиткой «Авто» нечем: чтобы закрепить сервер,
        // надо знать какой. Говорим, где это делается.
        if (string.IsNullOrWhiteSpace(AppSettings.Load(AppSettings.DefaultPath).PreferredServer))
        {
            Status.Text = "Автоподбор уже включён. Закрепить сервер — плиткой этого сервера или «выбрать» в списке.";
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
            settings = settings with { ForeignExitsOnly = ForeignSwitch.IsChecked == true };
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


    /// <summary>
    /// «Добавить подписку» — окно добавления (владелец 01.10: «как в Happ»).
    /// </summary>
    private void OnAddToggle(object sender, RoutedEventArgs e) => OpenAdd(pasteOnOpen: false);

    /// <param name="pasteOnOpen">Ctrl+V на вкладке: окно сразу берёт из буфера.</param>
    private void OpenAdd(bool pasteOnOpen)
    {
        var taken = _book.Entries.Select(entry => entry.Name).ToList();
        var window = new SubscriptionAddWindow(taken, pasteOnOpen) { Owner = Window.GetWindow(this) };

        if (window.ShowDialog() != true || window.Request is not { } request)
            return;

        if (request.Kind == AddKind.Keys)
            AddKeys(request.Input, request.InPool);
        else
            AddSubscription(request.Input, request.Name, request.InPool);
    }

    /// <summary>Ссылку подписки — в книгу; обёртки клиентов уже развёрнуты окном.</summary>
    /// <param name="name">Имя; <c>null</c> — первое свободное.</param>
    /// <param name="inPool">Сразу в работу.</param>
    private void AddSubscription(string url, string? name, bool inPool)
    {
        try
        {
            var book = SubscriptionBook.Load();
            name ??= book.FreeName();

            if (book.Entries.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Status.Text = $"Подписка с именем «{name}» уже есть. Дайте другое.";
                return;
            }

            book.Entries.Add(new SubscriptionEntry { Name = name, Url = url, InPool = inPool });
            book.Save();

            // По умолчанию новая встаёт в работу сразу: подписку добавляют,
            // чтобы ею пользоваться, и увидеть её серверы, которых туннель
            // не берёт, значило бы гадать почему. Указатель для консоли — следом.
            // С 01.10 это выключатель в окне: можно добавить и на паузе.
            if (inPool)
            {
                SubscriptionBook.SetWorking(url, true);
                this.Offer($"Подписка «{name}» в работе");
            }
            else
            {
                Status.Text = $"Подписка «{name}» добавлена на паузе — включите её, когда понадобится.";
            }

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

    /// <summary>Ключи из окна добавления — в папку «Отдельные ключи».</summary>
    /// <param name="inPool">
    /// Сразу в работу. «В работе» — у папки целиком, не у ключа: если папка
    /// уже в работе, новые ключи войдут в неё и при выключенном выключателе.
    /// </param>
    private void AddKeys(string input, bool inPool)
    {
        var fresh = KeyRing.Split(input);

        if (fresh.Count == 0)
        {
            Status.Text = "Ключей не нашлось: нужны строки вида vless://, trojan://, hysteria2://, tuic://, "
                + "wireguard:// и подобные.";
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

            // Новые ключи по умолчанию сразу в работе: добавляют, чтобы
            // пользоваться. Папку, уже стоящую в работе, выключатель окна
            // не выводит — он про новое, а не про прежние ключи.
            bool folderInPool = book.Keys.Count == 0 ? inPool : book.KeysInPool || inPool;
            SubscriptionBook.SaveKeys([.. book.Keys, .. added], inPool: folderInPool);

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

    /// <summary>
    /// Ctrl+V на вкладке — окно добавления, сразу взявшее из буфера.
    /// </summary>
    /// <remarks>
    /// Если курсор в поле ввода, Ctrl+V остаётся обычной вставкой: человек
    /// вставляет в поле — значит, хочет видеть вставленное там.
    /// </remarks>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.V || Keyboard.Modifiers != ModifierKeys.Control)
            return;

        if (Keyboard.FocusedElement is TextBox or PasswordBox)
            return;

        e.Handled = true;
        OpenAdd(pasteOnOpen: true);
    }

    /// <summary>⟳ у подписки: перечитать только её.</summary>
    /// <summary>Кнопка «Поддержка» у подписки — ссылка из её же заголовка (проверена: только http/https).</summary>
    private void OnSupport(object sender, RoutedEventArgs e) =>
        OpenProviderLink(sender, e, row => row.SupportUrl);

    /// <summary>Кнопка «Сайт подписки»: продлить, сменить тариф.</summary>
    private void OnWebPage(object sender, RoutedEventArgs e) =>
        OpenProviderLink(sender, e, row => row.WebPageUrl);

    private void OpenProviderLink(object sender, RoutedEventArgs e, Func<SubRow, string?> pick)
    {
        // Кнопка внутри кнопки папки — без этого щелчок раскрыл бы и папку.
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: string name }
            || _rows.FirstOrDefault(r => r.Entry.Name == name) is not { } row
            || pick(row) is not { } url)
            return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Status.Text = "Ссылку открыть не вышло: " + ex.GetBaseException().Message;
        }
    }

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
        bool fresh = await FillAsync(row, settings, CancellationToken.None, force: true);

        // Метки пула зависят от соседей — пересчитываем для всех.
        ApplyPool(settings);

        // «Перечитана» — только если панель и правда ответила (01.10: при 502
        // писалось «перечитана», а под ней — «серверы из запаса»).
        Status.Text = fresh
            ? $"«{name}» перечитана."
            : $"«{name}» не перечиталась — оставлен прежний список, причина в строке подписки.";
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
            keysMenu.Items.Add(Item("", "Очистить память замеров", () => ForgetMeasures(row)));
            keysMenu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
            keysMenu.Items.Add(Item("\uE74D", "Удалить все ключи…", ConfirmRemoveKeys));
            keysMenu.IsOpen = true;
            return;
        }

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        menu.Items.Add(Item("", "Обновить", async () => await RefreshOneAsync(name)));
        menu.Items.Add(Item("", "Тест пинга", async () => await MeasureAsync([row]), row.CanCheck));
        menu.Items.Add(Item("", "Очистить память замеров", () => ForgetMeasures(row)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(Item("", first ? "Уже первая" : "Закрепить наверху", () => MoveToTop(name), !first));
        menu.Items.Add(Item("", "Копировать ссылку", () => CopyUrl(row)));
        menu.Items.Add(Item("", "Изменить…", () => Edit(row)));
        menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });
        menu.Items.Add(Item("", "Удалить…", () => ConfirmRemove(name)));

        menu.IsOpen = true;
    }

    /// <summary>
    /// Забывает замеры серверов одной подписки — задержки, «через раз», «не отвечает».
    /// </summary>
    /// <remarks>
    /// Владелец 07.10: кнопка в «Настройках туннеля» стирает память всех
    /// подписок, а нужна и для одной. Теги — и пула, и исходные: замеры
    /// подписки в работе лежат под первыми, выключенной — под вторыми.
    /// </remarks>
    private void ForgetMeasures(SubRow row)
    {
        if (_sweeping)
        {
            Status.Text = "Идёт замер — дождитесь его или остановите.";
            return;
        }

        try
        {
            int forgotten = ServerHealthCache.Forget(row.Pooled.Concat(row.Raw).Select(s => s.Tag));

            _health.Reload();
            Reshow();

            Status.Text = forgotten > 0
                ? $"Память замеров «{row.Entry.Name}» очищена: серверов {forgotten}. Задержки появятся со следующим замером, "
                  + "нестабильные вернутся в автоподбор при следующем запуске движков."
                : $"У «{row.Entry.Name}» в памяти замеров ничего нет.";
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось очистить: " + ex.GetBaseException().Message;
        }
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

    /// <summary>Порядок серверов — выпадающим списком (макет владельца 12.10).</summary>
    private void OnSort(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SortButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        menu.Items.Add(Item(_byLatency ? "\uE73E" : string.Empty, "По задержке", () => Sort(byLatency: true)));
        menu.Items.Add(Item(_byLatency ? string.Empty : "\uE73E", "По порядку подписки", () => Sort(byLatency: false)));

        menu.IsOpen = true;
    }

    private void Sort(bool byLatency)
    {
        _byLatency = byLatency;
        SortButton.Content = byLatency ? "По задержке" : "По порядку подписки";

        Reshow();
    }

    /// <summary>
    /// «⋮» строки сервера: автоподбор, замер этого сервера, адрес, у ключа — убрать.
    /// </summary>
    /// <remarks>
    /// Прежде «Не брать в автоподбор» жило в правом щелчке, о котором не знал
    /// никто, а кнопка «убрать ключ» пропала из строки при одной из прошлых
    /// переделок — обработчик остался без кнопки.
    /// </remarks>
    private void OnServerMenu(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: string tag } anchor)
            return;

        var owner = _rows.FirstOrDefault(r => r.Servers.Any(s => s.Tag == tag));
        var server = owner?.Servers.First(s => s.Tag == tag);

        if (owner is null || server is null)
            return;

        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };

        var autoPick = Item("\uE8D0", server.AutoPickLabel, () => { });
        autoPick.Tag = tag;
        autoPick.Click += OnAutoPickToggle;
        menu.Items.Add(autoPick);

        menu.Items.Add(Item("\uEC4A", "Замерить этот", async () => await MeasureAsync([owner], onlyTag: tag),
            server.Measurable && !_sweeping));

        menu.Items.Add(Item("\uE8C8", "Скопировать адрес", () =>
        {
            try
            {
                Clipboard.SetText(server.Address);
                Status.Text = $"Адрес скопирован: {server.Address}.";
            }
            catch (Exception ex)
            {
                Status.Text = "Не скопировалось: " + ex.GetBaseException().Message;
            }
        }));

        AddPoolItems(menu, anchor.DataContext as ServerRow ?? server);

        if (server.RemoveShown == Visibility.Visible)
        {
            menu.Items.Add(new Separator { Style = (Style)FindResource("MenuLine") });

            var remove = Item("\uE74D", "Убрать ключ", () => { });
            remove.Tag = tag;
            remove.Click += OnRemoveKey;
            menu.Items.Add(remove);
        }

        menu.IsOpen = true;
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

    /// <summary>Идёт замер — кнопка «Замерить все» его останавливает.</summary>
    private bool _sweeping;

    /// <summary>Замер остановлен кнопкой, а не уходом с вкладки.</summary>
    private bool _stoppedByHand;

    /// <remarks>
    /// Пока идёт замер, кнопка — «Остановить» (владелец 07.10: «добавь
    /// возможность остановить замер»). Прежде замер останавливал только уход
    /// с вкладки, а при двадцати неработающих серверах он шёл минуты.
    /// </remarks>
    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (_sweeping)
        {
            _stoppedByHand = true;
            _work?.Cancel();

            MeasureButton.IsEnabled = false;
            MeasureButton.Content = "Останавливаю…";
            return;
        }

        await MeasureAsync(_rows);
    }

    /// <param name="onlyTag">Только этот сервер — пункт «Замерить этот» в «⋮» строки.</param>
    private async Task MeasureAsync(IReadOnlyList<SubRow> which, string? onlyTag = null)
    {
        // Тест пинга одной подписки посреди общего замера оборвал бы его.
        if (_sweeping)
        {
            Status.Text = "Идёт замер — дождитесь его или остановите.";
            return;
        }

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
            .Where(s => onlyTag is null || s.Tag == onlyTag)
            .ToList();

        // Выходы WARP замеряются вместе со всеми: в конфиге они лежат рядом
        // с серверами подписки, и знать про них надо то же самое.
        if (settings.WarpEnabled && onlyTag is null)
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

        _sweeping = true;
        _stoppedByHand = false;

        MeasureButton.Content = "Остановить";

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
            // Замер — в библиотеке (ServerSweep): его же зовёт запуск программы,
            // когда в «Настройках туннеля» включён «Замер всех серверов при запуске».
            // Движок меряет свои выходы сам, на вход не больше двух разом
            // (ServerSweep.PerEntry); пробник — до восьми разом.
            int alive = await ServerSweep.RunAsync(
                servers,
                singBox,
                EnginesRunning,
                _health,
                tag =>
                {
                    // Обновляем на каждом ответе: проверка идёт полминуты,
                    // и таблица, оживающая на глазах, куда честнее полосы
                    // загрузки, которая ничего не измеряет.
                    Dispatcher.Invoke(() =>
                    {
                        done++;
                        Progress.Value = done;
                        _measuring.Remove(tag);

                        // Счёт и на кнопке тоже. Полоса и строка состояния
                        // стоят вверху раздела, а смотрят во время замера
                        // на список серверов — и оттуда их попросту не видно.
                        if (!_stoppedByHand)
                            MeasureButton.Content = $"Остановить · {done} из {servers.Count}";

                        Status.Text = $"Измерено {done} из {servers.Count}…";
                        ReshowSoon();
                    });
                },
                _work.Token,
                settings,
                SelectorGroup);

            Status.Text = $"Отвечают {alive} из {servers.Count}. Замер сохранён — меню увидит те же цифры.";
        }
        catch (OperationCanceledException) when (_stoppedByHand)
        {
            // Остановлен кнопкой — замеренное до того не пропадает.
            _health.Save();

            Status.Text = $"Замер остановлен на {done} из {servers.Count}: замеренное сохранено, "
                + "у остальных серверов — прежние замеры.";
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
            _sweeping = false;
            _stoppedByHand = false;

            MeasureButton.IsEnabled = true;
            MeasureButton.Content = "Замерить все";

            Progress.Visibility = Visibility.Collapsed;

            // Незамеренные остались бы с подписью «идёт замер» навсегда,
            // если прогон прервали на середине.
            _measuring.Clear();

            ShowBusy([]);
            _reshowTimer?.Stop();
            Reshow();
        }
    }

    /// <summary>
    /// Замеряет выходы руками работающего движка — тех, кого не берёт пробник (WARP).
    /// </summary>
    private async Task MeasureThroughEngineAsync(IEnumerable<ProxyServer> servers)
    {
        if (!EnginesRunning)
            return;

        await ServerSweep.ThroughEngineAsync(servers, _health, CancellationToken.None, AppSettings.Load(AppSettings.DefaultPath));
        Reshow();
    }

    /// <summary>Работают ли движки прямо сейчас.</summary>
    /// <summary>
    /// Работает ли движок туннеля: у вкладки всё — выход, замеры, WARP — спрашивается у него.
    /// </summary>
    /// <remarks>
    /// Не «жив ли надзор»: при одном десинке надзор жив, а sing-box нет, и вкладка
    /// писала «В работе · Выход не назван · Движок не ответил» (владелец, 01.10:
    /// «это что за прикол»).
    /// </remarks>
    private static bool EnginesRunning =>
        SupervisorState.Load(SupervisorState.DefaultPath) is { } state
        && state.IsSupervisorAlive()
        && state.Services.Any(s => s.Name == "sing-box");

    /// <summary>Надзор жив, а туннеля среди служб нет — работает один десинк.</summary>
    private static bool DesyncOnly =>
        SupervisorState.Load(SupervisorState.DefaultPath) is { } state
        && state.IsSupervisorAlive()
        && state.Services.All(s => s.Name != "sing-box");

    /// <summary>Тег группы, которой движок выбирает выход.</summary>
    private const string SelectorGroup = "auto";

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
            ShowWarp(AppSettings.Load(AppSettings.DefaultPath));
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
        if (sender is Button { DataContext: ServerRow row })
            Choose(row);
    }

    /// <summary>
    /// Тег сервера у движка; подписку вне работы при этом ставит в работу.
    /// </summary>
    /// <remarks>
    /// Сервер из подписки вне работы — ставим её в работу, иначе конфиг
    /// собрался бы без него. Метки пула от этого могут поменяться
    /// (совпадёт имя с сервером соседней), так что тег берём заново —
    /// по месту сервера в его подписке. Нужно и выбору, и пулу серверов.
    /// </remarks>
    private string IntoWork(ServerRow row, AppSettings settings, out bool switched)
    {
        // Выходы WARP подписок не касаются: они и так в пуле.
        var owner = row.Owner == WarpOwner
            ? null
            : _rows.FirstOrDefault(r => r.Entry.Name == row.Owner);

        var tag = row.Tag;
        switched = owner is not null && !owner.Active;

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

        return tag;
    }

    private void Choose(ServerRow row)
    {
        if (row.PoolId is { } pool)
        {
            PinInPool(pool, row);
            return;
        }

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var tag = IntoWork(row, settings, out bool switched);

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

    /// <summary>
    /// Лента быстрой смены: «Авто» и серверы подписок в работе, быстрые первыми.
    /// </summary>
    private void ShowQuick(AppSettings settings)
    {
        bool auto = string.IsNullOrWhiteSpace(settings.PreferredServer);
        var icons = (FontFamily)FindResource("IconFont");
        var text = (FontFamily)FindResource("UiFont");

        var tiles = new List<QuickTile>
        {
            new(null, "Авто", "быстрейший", (Brush)FindResource("Muted"),
                null, Visibility.Collapsed, "", icons, Visibility.Visible,
                Edge(auto), Fill(auto), "Автоподбор: быстрейший из живых"),
        };

        // Мёртвые и незамеренные — в конце: лента для быстрого выбора, а не перечень.
        var servers = _rows
            .Where(r => r.Active)
            .SelectMany(r => r.Servers)
            .DistinctBy(s => s.Tag)
            .OrderBy(s => settings.StableFirst && Seen(s.Tag) is { Flaky: true } ? 1 : 0)
            .ThenBy(s => Seen(s.Tag) is { Success: true, LatencyMs: { } ms } ? ms : double.MaxValue)
            .Take(24);

        foreach (var server in servers)
        {
            var (country, name) = CountryTag.Split(server.Tag);
            var flag = country.Length == 2 ? FlagImages.For(country) : null;
            bool chosen = server.Tag == settings.PreferredServer;

            tiles.Add(new QuickTile(
                server.Tag,
                Short(name.Length > 0 ? name : server.Tag),
                Seen(server.Tag) is { Success: true, LatencyMs: { } ms } ? $"{ms:0} мс" : "—",
                (Brush)FindResource(Key(server.Tag)),
                flag,
                flag is null ? Visibility.Collapsed : Visibility.Visible,
                flag is null ? (country.Length == 2 ? country : "•") : string.Empty,
                text,
                flag is null ? Visibility.Visible : Visibility.Collapsed,
                Edge(chosen),
                Fill(chosen),
                server.Tag));
        }

        QuickServers.ItemsSource = tiles;

        Brush Edge(bool on) => (Brush)FindResource(on ? "Accent" : "Border");
        Brush Fill(bool on) => (Brush)FindResource(on ? "AccentFill" : "Surface");
    }

    /// <summary>Короткое имя для плитки: «Эстония — TLS XHTTP» → «Эстония».</summary>
    private static string Short(string name)
    {
        foreach (var cut in new[] { " — ", " | ", " - ", " · " })
        {
            int at = name.IndexOf(cut, StringComparison.Ordinal);

            if (at > 0)
                return name[..at].Trim();
        }

        return name;
    }

    private void OnQuick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        if (button.Tag is not string tag)
        {
            ChooseAuto();
            return;
        }

        var row = _rows.SelectMany(r => r.Servers).FirstOrDefault(s => s.Tag == tag);

        if (row is not null)
            Choose(row);
    }

    /// <summary>Сдвиг ленты на три плитки.</summary>
    private void OnQuickLeft(object sender, RoutedEventArgs e) =>
        QuickScroll.ScrollToHorizontalOffset(Math.Max(0, QuickScroll.HorizontalOffset - 3 * 124));

    private void OnQuickRight(object sender, RoutedEventArgs e) =>
        QuickScroll.ScrollToHorizontalOffset(QuickScroll.HorizontalOffset + 3 * 124);

    /// <summary>
    /// Колесо над лентой листает страницу, а не ленту; с Shift — ленту.
    /// </summary>
    /// <remarks>
    /// Вложенная прокрутка забирает колесо себе, даже когда ей листать
    /// по вертикали нечего, — и страница вставала, стоило мыши оказаться над лентой.
    /// </remarks>
    private void OnQuickWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            QuickScroll.ScrollToHorizontalOffset(QuickScroll.HorizontalOffset - e.Delta);
            return;
        }

        if (QuickScroll.Parent is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = sender,
            });
        }
    }

    /// <summary>«Показать все» — раскрыть папки подписок и прокрутить к ним; раскрыты — «Скрыть все».</summary>
    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        bool open = !_rows.All(r => r.Open);

        foreach (var row in _rows)
        {
            row.Open = open;
            row.Entry.Open = open;
        }

        try
        {
            _book.Save();
        }
        catch (Exception)
        {
            // Состояние показа не стоит жалобы.
        }

        Redraw();

        if (open)
            SubsHeader.BringIntoView();
    }

    private string _recentShown = string.Empty;

    /// <summary>«Недавние серверы»: последние разные выходы из журнала, свежие первыми.</summary>
    private void ShowRecent(string? current)
    {
        var recent = ExitHistory.Recent(5);

        // Перерисовка — только когда что-то поменялось: карточка выхода
        // обновляется раз в три секунды, а список — редко.
        var shown = string.Join("|", recent.Select(r => r.Tag + "·" + Latency(r.Tag) + "·" + (r.Tag == current)));

        if (shown == _recentShown)
            return;

        _recentShown = shown;

        var icons = (FontFamily)FindResource("IconFont");
        var text = (FontFamily)FindResource("UiFont");
        var muted = (Brush)FindResource("Muted");

        RecentList.ItemsSource = recent.Select(seen =>
        {
            var (country, name) = CountryTag.Split(seen.Tag);

            // У WARP флага нет — облачко (владелец, 01.10).
            bool warp = seen.Tag == Warp.MasqueTag;
            var flag = !warp && country.Length == 2 ? FlagImages.For(country) : null;

            return new RecentRow(
                Short(name.Length > 0 ? name : seen.Tag),
                warp ? "\uE753" : country.Length == 2 ? country : "•",
                warp ? icons : text,
                warp ? 15 : 11,
                warp ? Cloud : muted,
                flag is null ? Visibility.Visible : Visibility.Collapsed,
                flag,
                flag is null ? Visibility.Collapsed : Visibility.Visible,
                Seen(seen.Tag) is { Success: true, LatencyMs: { } ms } ? $"{ms:0} мс" : "—",
                (Brush)FindResource(Key(seen.Tag)),
                seen.Tag == current ? FontWeights.SemiBold : FontWeights.Normal);
        }).ToList();

        RecentEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Сводка у заголовка подписок: сколько в работе и серверов.</summary>
    private void ShowSummary()
    {
        int working = _rows.Count(r => r.Active);
        int servers = _rows.Sum(r => r.Servers.Count);

        SubsSummary.Text = _rows.Count == 0
            ? "нет подписок"
            : $"{working} в работе · {servers} {Plural(servers, "сервер", "сервера", "серверов")}";
    }

    private static string Plural(int n, string one, string few, string many) =>
        (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch
        {
            1 => one,
            >= 2 and <= 4 => few,
            _ => many,
        };

    private void OnStable(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            settings = settings with { StableFirst = StableSwitch.IsChecked == true };
            settings.Save(AppSettings.DefaultPath);

            Reshow();

            Status.Text = settings.StableFirst
                ? "Сначала стабильные: мигающие серверы — мимо автоподбора и после стабильных в ленте."
                : "Только по задержке: мигающие серверы — наравне со всеми.";

            this.Offer("Отбор серверов изменён");
        }
        catch (Exception ex)
        {
            Status.Text = "Не удалось: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>Подпись ленты: одна строка; не влезла — целиком в подсказке.</summary>
    private void ShowPickLine(string text)
    {
        PickLine.Text = text;
        PickLine.ToolTip = text;
    }
}
