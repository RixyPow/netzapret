using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Что сделало обновление пинов.</summary>
/// <param name="Changes">По строке на замену мёртвого: «87.228.47.204 → 87.228.47.201 (23 имени)».</param>
/// <param name="Dead">Имена с мёртвым первым адресом, замены которым не нашлось.</param>
public sealed record PinRefreshResult(
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Dead,
    string? Backup,
    string? Error,
    int Checked = 0)
{
    /// <summary>Посредники, не ответившие по DoH ни по одному адресу.</summary>
    public IReadOnlyList<string> Silent { get; init; } = [];

    /// <summary>Посредник переехал при живом старом: «Comss DNS: a → b (3 имени)».</summary>
    public IReadOnlyList<string> Moved { get; init; } = [];

    /// <summary>Первый адрес молчит, живой из записанных встал первым.</summary>
    public IReadOnlyList<string> Reordered { get; init; } = [];

    /// <summary>Первых адресов, не ответивших сейчас: меняться будут, если промолчат и в следующий раз.</summary>
    public int Suspects { get; init; }

    /// <summary>Скольким старым пинам записан профиль посредника.</summary>
    public int Assigned { get; init; }
}

/// <summary>Пины одного посредника под присмотром автозамены.</summary>
/// <param name="Intermediary">Имя посредника; <c>null</c> — пины без профиля.</param>
public sealed record PinWatchGroup(string? Intermediary, int Count);

/// <summary>Состояние автозамены для показа: см. <see cref="PinRefresh.Watch"/>.</summary>
/// <param name="Last">Последняя проверка; <c>null</c> — не было.</param>
/// <param name="Suspects">Первые адреса, промолчавшие в прошлой проверке.</param>
/// <param name="Groups">Пины под присмотром по посредникам.</param>
public sealed record PinWatch(
    DateTimeOffset? Last,
    IReadOnlyList<string> Suspects,
    IReadOnlyList<PinWatchGroup> Groups)
{
    public int Watched => Groups.Sum(g => g.Count);
}

/// <summary>
/// Сеть автозамены: проба адреса, опрос сервера посредника, контроль связи.
/// Отдельно — чтобы ход проверки покрывался тестами без сети.
/// </summary>
internal sealed class PinNet
{
    /// <summary>Проба адреса именем: TLS с проверкой сертификата и ответ сайта.</summary>
    public required Func<string, string, CancellationToken, Task<PinVerdict>> Probe { get; init; }

    /// <summary>Ответ сервера посредника на имена; <c>null</c> — не ответил.</summary>
    public required Func<Intermediary, IReadOnlyCollection<string>, CancellationToken, Task<ServerAnswers?>> Ask { get; init; }

    /// <summary>Есть ли связь вообще: соединение с контрольными адресами.</summary>
    public required Func<CancellationToken, Task<bool>> Online { get; init; }

    /// <summary>
    /// Контрольные адреса — Яндекс DNS и ya.ru, как у Zapret GUI (relay_check.py):
    /// российские, их не режут, и молчат оба только без сети.
    /// </summary>
    internal static readonly string[] Controls = ["77.88.8.8", "87.250.250.242"];

    public static PinNet Real { get; } = new()
    {
        Probe = async (address, name, ct) =>
            (await PinPicker.ProbeAsync(new PinCandidate(address, PinSource.Pool, "пин"), name, ct)).Verdict,
        Ask = IntermediaryDns.AskServerAsync,
        Online = async ct =>
        {
            var tries = Controls.Select(async address =>
            {
                try
                {
                    using var client = new System.Net.Sockets.TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));

                    await client.ConnectAsync(System.Net.IPAddress.Parse(address), 443, timeout.Token);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            });

            return (await Task.WhenAll(tries)).Any(ok => ok);
        },
    };
}

/// <summary>
/// Сам меняет умерший адрес пина на живой, который посредник отдаёт сейчас.
/// </summary>
/// <remarks>
/// <para>
/// Просьба владельца 28.09: «если они меняются, то можно сделать чтоб пины
/// автоматически менялись». В тот день у XBOX DNS умер 87.228.47.204, и Claude
/// с ChatGPT легли до тех пор, пока владелец не нашёл новый адрес руками.
/// </para>
/// <para>
/// Осторожно, по трём правилам. Работающий пин не трогается вовсе. Живость
/// проверяется по одному имени на адрес, а не по каждому: 28.09 подбор пина
/// бил по посредникам сотнями соединений, и XBOX, возможно, закрыл за это
/// адрес владельца. Замена пишется, только если новый адрес сам проверен
/// тем же именем — иначе мёртвое сменилось бы мёртвым.
/// </para>
/// <para>
/// Проверять стоит при поднятых движках: часть посредников напрямую отвечает
/// только с десинком (.195 28.09). Без движков живой адрес выглядел бы мёртвым.
/// </para>
/// <para>
/// С 30.09 по 03.11 — только кнопкой на вкладке DNS (владелец: «даже не всегда
/// при запуске нужен»). С 03.11 ещё и сама, раз в сутки при поднятых движках
/// (владелец: «их банят только так… стоит внедрить автозамену», «каждый час —
/// перебор, лучше раз в сутки», «добавь возможность вкл выкл автозамену»):
/// выключатель <see cref="AppSettings.AutoRepin"/>, срок — <see cref="IsDue"/>.
/// В тот день у владельца 25 имён Google AI стояли на мёртвом GeoHide
/// 193.233.112.67 — с ноября его прокси не отвечают.
/// </para>
/// <para>
/// С 05.11 — по образцу Zapret GUI 21.1.7.228 (address_engine.py), решением
/// владельца. Срок — 6 часов, как <c>HOSTS_ADDRESSES</c> у них. У пина есть
/// профиль — посредник, который его отдал (<see cref="HostsEditor.ViaMark"/>),
/// и каждая проверка спрашивает сервер профиля о его именах: отдаёт он других
/// посредников, а первого адреса пина среди них нет — посредник переехал, и пин
/// переходит на новых, проверенных той же пробой, не дожидаясь смерти старого.
/// Живость — только первого адреса имени, одной пробой; не ответил — подозреваемый
/// (<see cref="SuspectsPath"/>), меняется со второго промаха подряд или сразу,
/// если проверка запущена кнопкой. Перед заменой мёртвого — контроль сети
/// (<see cref="PinNet.Controls"/>): молчат оба — не меняется ничего.
/// </para>
/// <para>
/// Живой адрес ставится первым. По замеру Zapret GUI 13.10 Chrome из нескольких
/// строк hosts одного имени берёт только первую — у нас это не проверено.
/// Прежде здесь стояло «жив хоть один из двух — Windows дойдёт до второго»,
/// и имя с мёртвым первым адресом не трогалось вовсе: если Chrome и правда
/// берёт одну строку, такой пин в браузере был мёртв.
/// </para>
/// </remarks>
public static class PinRefresh
{
    /// <summary>Как часто проверять самой.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromHours(6);

    /// <summary>Когда проверка прошла в последний раз — кнопкой или сама.</summary>
    public static string StampPath => Path.Combine("runtime", "pin-refresh.txt");

    /// <summary>Пора ли проверять самой: с прошлой проверки прошёл срок <see cref="Every"/> или её не было.</summary>
    public static bool IsDue(DateTimeOffset now, string? stampPath = null)
    {
        try
        {
            var text = File.ReadAllText(stampPath ?? StampPath).Trim();

            return !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeUniversal, out var last)
                   || now - last >= Every
                   || last > now;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Когда была последняя проверка; <c>null</c> — не было или отметка не читается.</summary>
    /// <remarks>
    /// Только для показа: вкладка DNS называет время проверки и следующей. Срок
    /// решает по-прежнему <see cref="IsDue"/>, чтобы чтение не меняло ход автозамены.
    /// </remarks>
    public static DateTimeOffset? LastChecked(string? stampPath = null)
    {
        try
        {
            var text = File.ReadAllText(stampPath ?? StampPath).Trim();

            return DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var last)
                ? last
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Что видно об автозамене без проверки: время прошлой, подозреваемые адреса
    /// и пины под присмотром по посредникам.
    /// </summary>
    /// <remarks>
    /// Под присмотром — те же имена, что берёт проверка: наш блок без пинов на адрес
    /// самого сервиса (<see cref="HostsEditor.DesyncMark"/>). Незнакомый ключ профиля
    /// проверка считает пином без посредника — так и здесь, чтобы счёт не расходился.
    /// </remarks>
    public static PinWatch Watch(string? hostsPath = null, string? stampPath = null, string? suspectsPath = null)
    {
        var groups = new List<PinWatchGroup>();

        try
        {
            var own = HostsEditor.DesyncPins(hostsPath);
            var via = HostsEditor.PinsVia(hostsPath);

            var names = HostsEditor.PinsAll(hostsPath)
                .Where(p => !own.Contains(p.Key) && p.Value.Count > 0)
                .Select(p => p.Key)
                .ToList();

            // Порядок посредников — как в списке доверия, без посредника — последним.
            foreach (var server in IntermediaryDns.Servers)
            {
                int count = names.Count(n => via.TryGetValue(n, out var key)
                    && string.Equals(IntermediaryDns.ByKey(key)?.Key, server.Key, StringComparison.OrdinalIgnoreCase));

                if (count > 0)
                    groups.Add(new(server.Name, count));
            }

            int bare = names.Count(n => !via.TryGetValue(n, out var key) || IntermediaryDns.ByKey(key) is null);

            if (bare > 0)
                groups.Add(new(null, bare));
        }
        catch (Exception)
        {
            // hosts не прочитался — присмотр не показывается; сама проверка скажет об этом своим итогом.
        }

        return new PinWatch(
            LastChecked(stampPath),
            LoadSuspects(suspectsPath ?? SuspectsPath).Order(StringComparer.Ordinal).ToList(),
            groups);
    }

    /// <summary>Отметить проверку: следующая сама — через <see cref="Every"/>.</summary>
    public static void Mark(DateTimeOffset now, string? stampPath = null)
    {
        var target = stampPath ?? StampPath;

        if (Path.GetDirectoryName(target) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        File.WriteAllText(target, now.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Кнопка и плановая проверка не идут разом: обе пишут hosts.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Адреса, не ответившие в прошлой проверке: второй промах подряд — уже смерть.</summary>
    public static string SuspectsPath => Path.Combine("runtime", "pin-suspects.json");

    /// <summary>Проверка и замена пинов.</summary>
    /// <param name="byHand">
    /// Кнопкой: человек ждёт итога, и мёртвый первый адрес меняется сразу, а не со
    /// второго промаха подряд.
    /// </param>
    /// <remarks>
    /// На настоящем hosts ставит отметку срока (<see cref="Mark"/>) под тем же замком,
    /// что и сама проверка: плановая, ждавшая кнопку на замке, увидит отметку и не
    /// пойдёт следом вторым залпом соединений, засчитав только что записанных
    /// подозреваемых вторым промахом за секунды, а не за 6 часов.
    /// </remarks>
    public static async Task<PinRefreshResult> RunAsync(CancellationToken cancellationToken, string? path = null, bool byHand = false)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            return await CheckAsync(PinNet.Real, path, path is null ? SuspectsPath : path + ".nz-suspects.json", byHand, cancellationToken);
        }
        finally
        {
            if (path is null)
                TryMark();

            Gate.Release();
        }
    }

    /// <summary>
    /// Плановая проверка: срок смотрится уже под замком; <c>null</c> — не пора
    /// (кнопка успела проверить, пока проверка ждала замка).
    /// </summary>
    public static async Task<PinRefreshResult?> RunIfDueAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            if (!IsDue(DateTimeOffset.Now))
                return null;

            try
            {
                return await CheckAsync(PinNet.Real, null, SuspectsPath, byHand: false, cancellationToken);
            }
            finally
            {
                // Отметка и после неудачи: иначе молчащая сеть повторяла бы проверку каждые пять минут.
                TryMark();
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void TryMark()
    {
        try
        {
            Mark(DateTimeOffset.Now);
        }
        catch (IOException)
        {
            // Не записалась — следующий круг сторожа проверит снова; это не поломка.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Ответ сервера посредника и его посредники: все и без правила трёх имён.</summary>
    private sealed record Heard(ServerAnswers Answers, IReadOnlySet<string> Relays, IReadOnlySet<string> Strong);

    private static async Task<ServerAnswers?> AskAsync(
        PinNet net, Intermediary server, IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        try
        {
            return await net.Ask(server, names, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Весь ход проверки; сеть — через <paramref name="net"/>, чтобы алгоритм
    /// проверялся тестами без сети.
    /// </summary>
    internal static async Task<PinRefreshResult> CheckAsync(
        PinNet net,
        string? path,
        string suspectsPath,
        bool byHand,
        CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> pins;
        IReadOnlyDictionary<string, string> profiles;

        try
        {
            // Пины на адреса самого сервиса (пометка nz:desync, решения
            // «Напрямую») не обновляются: посредники отдали бы им свой прокси,
            // а живость такого адреса без десинка не видна — 06.10 у владельца
            // TCP к адресам Meta из каталога без десинка не соединялся вовсе.
            var own = HostsEditor.DesyncPins(path);

            pins = HostsEditor.PinsAll(path)
                .Where(p => !own.Contains(p.Key) && p.Value.Count > 0)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

            // Незнакомый ключ (посредника убрали из списка) — как без профиля.
            profiles = HostsEditor.PinsVia(path)
                .Where(p => pins.ContainsKey(p.Key) && IntermediaryDns.ByKey(p.Value) is not null)
                .ToDictionary(p => p.Key, p => IntermediaryDns.ByKey(p.Value)!.Key, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            return new([], [], null, "hosts не прочитался: " + ex.GetBaseException().Message);
        }

        if (pins.Count == 0)
            return new([], [], null, null);

        var names = pins.Keys.Order(StringComparer.Ordinal).ToList();

        // 1. Сервер профиля — о его именах, имена без профиля — у всех: так
        // старый пин узнаёт своего посредника (присвоение профиля). Вопросы к одному
        // серверу идут одним соединением (IntermediaryDns.AskServerAsync).
        var asked = new List<(Intermediary Server, List<string> Own, List<string> Names)>();

        foreach (var server in IntermediaryDns.Servers)
        {
            var mine = names.Where(n => profiles.TryGetValue(n, out var key) && key == server.Key).ToList();
            var all = mine.Concat(names.Where(n => !profiles.ContainsKey(n))).ToList();

            if (all.Count > 0)
                asked.Add((server, mine, all));
        }

        var replies = await Task.WhenAll(asked.Select(a => AskAsync(net, a.Server, a.Names, cancellationToken)));
        var silent = asked.Zip(replies).Where(p => p.Second is null).Select(p => p.First.Server.Name).ToList();

        // Ключ → ответ и его посредники. Молчащего сервера здесь нет: по нему ничего не меняется.
        var heard = asked.Zip(replies)
            .Where(p => p.Second is not null)
            .ToDictionary(
                p => p.First.Server.Key,
                p => new Heard(p.Second!, IntermediaryDns.Relays(p.Second!, p.First.Own), IntermediaryDns.StrongRelays(p.Second!)));

        IReadOnlyList<string> Given(string key, string name, bool strong = false) =>
            heard.TryGetValue(key, out var h) ? IntermediaryDns.RelaysFor(h.Answers, strong ? h.Strong : h.Relays, name) : [];

        // 2. Присвоение: пин без профиля, чей адрес посредник отдаёт этому имени
        // как своего посредника, — его. Первый по порядку доверия.
        var assigned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names.Where(n => !profiles.ContainsKey(n)))
        {
            if (IntermediaryDns.Servers.FirstOrDefault(s => Given(s.Key, name).Intersect(pins[name], StringComparer.Ordinal).Any())
                is { } owner)
            {
                assigned[name] = owner.Key;
            }
        }

        string? Profile(string name) =>
            profiles.TryGetValue(name, out var key) ? key : assigned.GetValueOrDefault(name);

        // 3. Живость — только первого адреса каждого имени, одной пробой на адрес:
        // плановая проверка дешёвая, второй адрес спрашивается, лишь когда первый молчит.
        var verdicts = new Dictionary<string, (PinVerdict Verdict, string Name)>(StringComparer.Ordinal);

        async Task<PinVerdict> ProbeAsync(string address, string name)
        {
            // Отказ (403) зависит от имени: корень API отвечает им и через живого
            // посредника. Вердикт чужого имени тут не годится — адрес пробуется
            // этим. Прочие вердикты от имени не зависят: адрес — одна проба.
            if (verdicts.TryGetValue(address, out var first)
                && (first.Verdict != PinVerdict.Refused || string.Equals(first.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return first.Verdict;
            }

            var key = verdicts.ContainsKey(address) ? address + " " + name.ToLowerInvariant() : address;

            if (verdicts.TryGetValue(key, out var again))
                return again.Verdict;

            PinVerdict verdict;

            try
            {
                verdict = await net.Probe(address, name, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                verdict = PinVerdict.Dead;
            }

            verdicts[key] = (verdict, name);

            return verdict;
        }

        var heads = names.ToDictionary(n => n, n => pins[n].FirstOrDefault(Checkable), StringComparer.OrdinalIgnoreCase);
        var alive = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (heads[name] is { } head && !alive.ContainsKey(head))
                alive[head] = await ProbeAsync(head, name) != PinVerdict.Dead;
        }

        var before = LoadSuspects(suspectsPath);

        // Не отвечает больше половины адресов разом — это сбой сети, а не смерть
        // адресов. 29.09 в 14:58 проверка сочла мёртвыми 68 имён сразу, среди
        // них заведомо живые, и «заменила» настоящие адреса Spotify
        // (35.186.224.x) на прокси XBOX. При таком раскладе не меняем ничего.
        int deadCount = alive.Values.Count(ok => !ok);

        if (alive.Count >= 4 && deadCount * 2 > alive.Count)
        {
            // Сбой — не свидетельство: новых подозреваемых он не прибавляет, но
            // ответившие сейчас подозрение снимают. Иначе адрес из позапрошлой
            // проверки, ответивший в этой, при следующем моргании сошёл бы за
            // второй промах подряд.
            SaveSuspects(suspectsPath, before.Where(a => alive.TryGetValue(a, out var ok) && !ok).ToList());

            return new([], [], null,
                $"не ответили {deadCount} адресов из {alive.Count} — похоже на сбой сети, пины не тронуты", alive.Count)
            { Silent = silent };
        }

        // 4а. Посредник переехал: сервер профиля отдаёт имени других посредников,
        // а первого адреса пина среди них нет. Ждать смерти старого незачем —
        // посредник уже ведёт туда, куда сказал.
        //
        // При живом старом — осторожнее, чем при мёртвом: переезжать только на
        // посредников без правила трёх имён (IntermediaryDns.StrongRelays: настоящий
        // адрес сервиса трём именам профиля посредником не станет), только если
        // старого сервер не отдаёт больше ни одному имени и если второй вопрос
        // говорит то же. GeoHide и Comss перемешивают узлы; отдают ли они имени
        // каждый раз часть узлов, не замерено, — без этого живой пин скакал бы
        // с пары на пару на каждой проверке.
        var moves = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var confirm = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var name in names)
        {
            if (heads[name] is not { } head || Profile(name) is not { } profile || !heard.TryGetValue(profile, out var h))
                continue;

            bool live = alive[head];
            var given = Given(profile, name, strong: live);

            if (given.Count == 0 || given.Contains(head, StringComparer.Ordinal))
                continue;

            if (live && h.Relays.Contains(head))
                continue;

            moves[name] = given;

            if (live)
            {
                if (!confirm.TryGetValue(profile, out var list))
                    confirm[profile] = list = [];

                list.Add(name);
            }
        }

        var again = await Task.WhenAll(confirm.Select(async c =>
            (Names: c.Value, Reply: await AskAsync(net, IntermediaryDns.ByKey(c.Key)!, c.Value, cancellationToken))));

        foreach (var (list, reply) in again)
        {
            foreach (var name in list)
            {
                var head = heads[name]!;

                if (reply is null
                    || reply.Names.Values.Any(a => a.Any(x => x.ToString() == head))
                    || IntermediaryDns.RelaysFor(reply, IntermediaryDns.StrongRelays(reply), name).Count == 0)
                {
                    moves.Remove(name);
                }
            }
        }

        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var via = new Dictionary<string, string>(assigned, StringComparer.OrdinalIgnoreCase);
        var replaced = new List<(string Name, string Line)>();
        var moved = new List<(string Name, string Line)>();
        var reordered = new List<(string Name, string Line)>();
        var pending = new List<string>();
        var dead = new List<string>();
        bool? online = null;

        // Живые из набора посредников, не больше двух, — одной пробой на адрес.
        async Task<List<string>> LiveAsync(IEnumerable<string> addresses, string name)
        {
            var live = new List<string>();

            foreach (var address in addresses)
            {
                if (alive.TryGetValue(address, out var known) && !known)
                    continue;

                // Новый адрес — только с ответом самого сайта: отказ по стране
                // (настоящий адрес, а не посредник) замены не стоит.
                if (await ProbeAsync(address, name) is PinVerdict.Works or PinVerdict.Challenge)
                    live.Add(address);

                if (live.Count == 2)
                    break;
            }

            return live;
        }

        foreach (var name in names)
        {
            var current = pins[name];
            var head = heads[name];
            var profile = Profile(name);

            if (moves.TryGetValue(name, out var given))
            {
                var live = await LiveAsync(given, name);

                if (live.Count > 0)
                {
                    entries[name] = live;
                    via[name] = profile!;
                    moved.Add((name, $"{IntermediaryDns.ByKey(profile)!.Name}: {Join(current)} → {Join(live)}"));
                    continue;
                }
            }

            if (head is null || alive[head])
                continue;

            // 4б. Первый адрес молчит. Одна неудача — только подозрение: адрес
            // мог моргнуть. Меняем со второй подряд или когда человек нажал кнопку.
            if (!byHand && !before.Contains(head))
                continue;

            // 5. Прежде чем верить смерти — есть ли сеть вообще. Нет — замены по
            // смерти не делаются, но переезды, уже проверенные пробой, остаются.
            online ??= await OnlineAsync(net, cancellationToken);

            if (online == false)
                continue;

            if (profile is not null)
            {
                var live = await LiveAsync(Given(profile, name).Where(a => a != head), name);

                if (live.Count > 0)
                {
                    entries[name] = live;
                    via[name] = profile;
                    replaced.Add((name, $"{Join(current)} → {Join(live)}"));
                    continue;
                }
            }

            // Живой из уже записанных — первым. Остальные остаются за ним: имя
            // не теряет адресов, а мёртвый, если оживёт, ещё пригодится. Сперва —
            // ответивший сам; отказавший (403) — лишь когда такого нет: он может
            // быть настоящим адресом сервиса, но и он лучше молчащего.
            string? spare = null;

            foreach (bool strict in new[] { true, false })
            {
                foreach (var address in current.Where(a => a != head && Checkable(a)))
                {
                    var verdict = await ProbeAsync(address, name);

                    if (strict ? verdict is PinVerdict.Works or PinVerdict.Challenge : verdict != PinVerdict.Dead)
                    {
                        spare = address;
                        break;
                    }
                }

                if (spare is not null)
                    break;
            }

            if (spare is not null)
            {
                entries[name] = [spare, .. current.Where(a => a != spare)];

                if (profile is not null)
                    via[name] = profile;

                reordered.Add((name, $"{head} → первым {spare}"));
                continue;
            }

            pending.Add(name);
        }

        // 6. Свой посредник не помог — живой у любого другого, по порядку доверия.
        // Имя с профилем спрашивалось только у своего сервера, и чужие о нём
        // сейчас спрашиваются отдельно — лишь о таких именах, это редкость.
        // Посредники здесь — без правила трёх имён: имя чужому профилю не своё.
        var foreign = new Dictionary<string, Heard>(StringComparer.Ordinal);

        if (pending.Any(profiles.ContainsKey))
        {
            var ask = IntermediaryDns.Servers
                .Select(s => (Server: s, Names: pending.Where(n => profiles.TryGetValue(n, out var k) && k != s.Key).ToList()))
                .Where(a => a.Names.Count > 0)
                .ToList();

            var answers = await Task.WhenAll(ask.Select(a => AskAsync(net, a.Server, a.Names, cancellationToken)));

            foreach (var (a, reply) in ask.Zip(answers))
            {
                if (reply is not null)
                    foreach (var name in a.Names)
                        foreign[a.Server.Key + " " + name.ToLowerInvariant()] = new Heard(reply, IntermediaryDns.Relays(reply, []), IntermediaryDns.StrongRelays(reply));
            }
        }

        foreach (var name in pending)
        {
            var head = heads[name];
            var profile = Profile(name);
            bool found = false;

            foreach (var server in IntermediaryDns.Servers.Where(s => s.Key != profile))
            {
                var given = profiles.ContainsKey(name)
                    ? foreign.TryGetValue(server.Key + " " + name.ToLowerInvariant(), out var f)
                        ? IntermediaryDns.RelaysFor(f.Answers, f.Relays, name)
                        : []
                    : Given(server.Key, name);

                var live = await LiveAsync(given.Where(a => a != head), name);

                if (live.Count == 0)
                    continue;

                entries[name] = live;
                via[name] = server.Key;
                replaced.Add((name, $"{Join(pins[name])} → {Join(live)}"));
                found = true;
                break;
            }

            if (!found)
                dead.Add(name);
        }

        // Подозреваемые — и когда сеть молчала: в следующей проверке промах
        // засчитается вторым, только если сеть тогда будет.
        var suspects = alive.Where(p => !p.Value).Select(p => p.Key).ToList();
        SaveSuspects(suspectsPath, suspects);

        string? offline = online == false
            ? "контрольные адреса (Яндекс) не отвечают — похоже, нет сети; мёртвые пины не тронуты"
            : null;

        if (entries.Count == 0)
        {
            return new([], dead, null, offline, alive.Count)
            {
                Silent = silent,
                Suspects = suspects.Count,
            };
        }

        try
        {
            // Профиль пишется и тем, чьи адреса не менялись (присвоение), — но только
            // вместе с настоящей заменой: ради одной пометки hosts не переписывается.
            // Присвоенным — лишь если их адреса в блоке те же, что были на начале проверки.
            var result = HostsEditor.Repin(entries, path, via: via,
                assignedAt: assigned.Keys.ToDictionary(n => n, n => pins[n], StringComparer.OrdinalIgnoreCase));

            if (path is null)
                HostsEditor.FlushDns();

            if (result.RevertedBy is { } by)
            {
                return new([], dead, result.Backup, $"hosts вернул к своему {by}", alive.Count)
                {
                    Silent = silent,
                    Suspects = suspects.Count,
                };
            }

            return new(Grouped(replaced), dead, result.Backup, offline, alive.Count)
            {
                Silent = silent,
                Moved = Grouped(moved),
                Reordered = Grouped(reordered),
                Suspects = suspects.Count,
                Assigned = assigned.Count,
            };
        }
        catch (Exception ex)
        {
            return new([], dead, null, "hosts не записался: " + ex.GetBaseException().Message, alive.Count)
            {
                Silent = silent,
                Suspects = suspects.Count,
            };
        }
    }

    /// <summary>Есть ли сеть: соединение на 443 хоть с одним контрольным адресом Яндекса.</summary>
    private static async Task<bool> OnlineAsync(PinNet net, CancellationToken cancellationToken)
    {
        try
        {
            return await net.Online(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Адрес, который имеет смысл проверять: разбирается и не заглушка.</summary>
    private static bool Checkable(string address) =>
        System.Net.IPAddress.TryParse(address, out var ip) && !BlockCheck.IsStub(ip);

    private static string Join(IEnumerable<string> addresses) => string.Join(", ", addresses);

    private static IReadOnlyList<string> Grouped(IEnumerable<(string Name, string Line)> lines) =>
        lines
            .GroupBy(x => x.Line, StringComparer.Ordinal)
            .Select(g => $"{g.Key} ({g.Count()} {Names(g.Count())})")
            .ToList();

    internal static IReadOnlySet<string> LoadSuspects(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new HashSet<string>(StringComparer.Ordinal);

            return (System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? [])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Испорченный файл — как первая проверка: подозреваемых нет, менять рано.
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static void SaveSuspects(string path, IReadOnlyCollection<string> addresses)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(addresses.Order(StringComparer.Ordinal).ToArray()));
        }
        catch (IOException)
        {
            // Не записалось — следующая проверка сочтёт промах первым; это осторожная сторона.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
    private static string Names(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => "имя",
        (2 or 3 or 4, not (12 or 13 or 14)) => "имени",
        _ => "имён",
    };
}
