using System.Net;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Посредник: как назвать, имя его DoH и адреса на случай, когда имя не узналось.</summary>
/// <param name="Key">
/// Ключ профиля в пометке пина <see cref="HostsEditor.ViaMark"/>: латиницей и без
/// пробелов, не меняется вместе с <paramref name="Name"/>.
/// </param>
/// <param name="RelayName">
/// Служебное имя, на которое сервер отвечает адресом своего посредника; <c>null</c> — нет такого.
/// </param>
public sealed record Intermediary(
    string Name,
    string Key,
    string DohName,
    IReadOnlyList<string> Fallback,
    string Path = "/dns-query",
    string? RelayName = null);

/// <summary>Ответ одного сервера посредника на опрос автозамены.</summary>
/// <param name="Names">Имя → адреса (вместе с контрольными именами).</param>
/// <param name="RelayCheck">Ответ на <see cref="Intermediary.RelayName"/>; пусто — не спрашивали или нет.</param>
public sealed record ServerAnswers(
    IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> Names,
    IReadOnlyList<IPAddress> RelayCheck)
{
    /// <summary>
    /// Имена, ответ о которых не дошёл: таймаут, обрыв, SERVFAIL. В <see cref="Names"/>
    /// у них пусто, но это не «пустой ответ» — посредник ничего о них не сказал.
    /// </summary>
    public IReadOnlySet<string> Unanswered { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Что ответили посредники: адреса по именам и кто не ответил вовсе.</summary>
public sealed record IntermediaryAnswers(
    IReadOnlyDictionary<string, IReadOnlyList<PinCandidate>> Answers,
    IReadOnlyList<string> Silent);

/// <summary>Что посредник делает с именем сейчас — для окна пина.</summary>
public enum RelayVerdict
{
    /// <summary>Не ответил по DoH ни по имени сервера, ни по запасным адресам.</summary>
    Silent,

    /// <summary>Отдаёт имени адрес своего посредника.</summary>
    Relays,

    /// <summary>Отдаёт настоящий адрес сервиса — этот сервис он не проксирует.</summary>
    NotProxied,

    /// <summary>Пустой ответ или заглушка 0.0.0.0 — имя не обслуживает.</summary>
    Refuses,

    /// <summary>Сервер ответил на проверку, но ответ об этом имени не дошёл.</summary>
    Unanswered,

    /// <summary>
    /// Отдаёт адрес, о котором по ответу не понять, посредник ли это: он не совпадает
    /// ни с посредником контрольных имён, ни с настоящим адресом. Решает проба.
    /// </summary>
    Unconfirmed,
}

/// <summary>Ответ одного посредника о главном имени сервиса.</summary>
/// <param name="Addresses">
/// При <see cref="RelayVerdict.Relays"/> — адреса посредника, при
/// <see cref="RelayVerdict.NotProxied"/> — настоящие адреса, при
/// <see cref="RelayVerdict.Unconfirmed"/> — то, что отдал; иначе пусто.
/// </param>
/// <param name="ByRelayCheck">Посредник подтверждён ответом на служебное имя (<see cref="Intermediary.RelayName"/>).</param>
public sealed record RelayNow(
    Intermediary Server,
    string Name,
    RelayVerdict Verdict,
    IReadOnlyList<string> Addresses,
    bool ByRelayCheck = false);

/// <summary>
/// Спрашивает у DNS-посредников, какой адрес они отдают имени сейчас.
/// </summary>
/// <remarks>
/// <para>
/// Посредники меняют адреса своих прокси. 28.09 у XBOX DNS умер 87.228.47.204,
/// .195 то пропускал TLS, то нет, а сам XBOX DNS уже раздавал .201 и .203.
/// Снимок каталога от 24.09 знал только .195 и .204: подбор пина предлагал
/// мёртвое, живое владелец нашёл в чужом GUI. Любой снимок стареет —
/// спрашивать надо у самого посредника, в момент подбора.
/// </para>
/// <para>
/// С 03.11 — по DoH, а не обычным DNS. Замер 03.11 у владельца, напрямую через
/// Wi-Fi: Comss (83.220.169.155, 212.109.195.93), AstraCat и DNS-AI на обычный
/// DNS не отвечают вовсе, XBOX на все сайты ИИ отдаёт 0.0.0.0, российские узлы
/// GeoHide отдают свои прокси .67, .68, .88 и 159.194.200.33 — те молчат. Кнопка
/// «Проверить актуальные адреса» живой замены не находила ни одной. По DoH
/// отвечают Comss (dns.comss.one, 35 мс, прокси 206.223.x и ещё два — с
/// ChatGPT 200), GeoHide EU и US, Zapret DNS (dns.zapret.moe, за Cloudflare с
/// 03.11, все сервисы — на один посредник) и DNS-AI. Так же с 02.11 делает
/// Zapret GUI 21.1.7.228: обычный запрос провайдер может перехватить и
/// ответить за сервер, а сертификат DoH он не подделает.
/// </para>
/// <para>
/// Адрес сервера — по имени DoH через честный резолвер
/// (<see cref="DnsSurvey.HonestAsync"/>), запасом — встроенные. Имя переживает
/// переезд: Zapret DNS за месяц сменил три адреса. У GeoHide имя отдаёт ровно
/// узлы из его официального servers.json (eu — семь, us — четыре; сверено
/// 03.11), а сам servers.json из России отдаётся только через туннель.
/// XBOX DNS и российский GeoHide убраны — живых прокси для ИИ у них нет.
/// AstraCat из сети владельца 03.11 не отвечал ни по DoH, ни обычным DNS —
/// оставлен последним, по слову владельца.
/// </para>
/// </remarks>
public static class IntermediaryDns
{
    /// <summary>Посредники в порядке доверия: кандидаты первого пробуются первыми.</summary>
    public static IReadOnlyList<Intermediary> Servers { get; } =
    [
        new("Comss DNS", "comss", "dns.comss.one", ["212.109.221.20", "195.133.25.16", "212.109.195.93"]),

        // relay.check — служебное имя Zapret DNS: сервер отвечает на него адресом
        // своего посредника (так узнаёт посредника Zapret GUI, config/zapret_dns.py).
        new("Zapret DNS", "zapret", "dns.zapret.moe", ["188.114.96.1", "188.114.97.1"], RelayName: "relay.check.dns.zapret.moe"),
        new("GeoHide EU", "geohide-eu", "eu.geohide.ru", ["5.175.161.3", "5.175.161.40", "143.20.64.192"]),
        new("GeoHide US", "geohide-us", "us.geohide.ru", ["192.255.159.240", "192.255.159.241"]),

        // Из списка посредников в обсуждении Flowseal №3423 (со слов админа GeoHide).
        // Замер 06.11 у владельца по DoH: всем четырём контрольным именам — один свой
        // прокси (wlrus — 191.217.212.16, dagami — 45.86.65.240), через него ChatGPT,
        // Gemini и Grok — 200 с настоящим сертификатом, Claude — проверка на робота
        // Cloudflare, как и у Zapret DNS. Прочие новые из того списка (malw, mafioznik,
        // shecan, bezmezhau, magicdns) не ответили. Запасные адреса — честный DoH 06.11.
        new("WLRUS DNS", "wlrus", "dns.wlrus.cfd", ["95.85.240.254", "138.124.254.134"]),
        new("Dagami DNS", "dagami", "dns.dagami.ru", ["45.86.65.240"]),

        new("DNS-AI", "dns-ai", "dns.dns-ai.ru", ["194.87.196.13", "45.151.102.219"]),
        new("AstraCat", "astracat", "dns.astracat.network", ["135.106.217.200", "135.106.197.22"]),
    ];

    /// <summary>Посредник по ключу профиля; <c>null</c> — ключ незнакомый.</summary>
    public static Intermediary? ByKey(string? key) =>
        key is null ? null : Servers.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Скольким разным именам своего профиля сервер должен отдать адрес, чтобы тот
    /// считался его посредником, — как <c>RELAY_MIN_NAMES</c> у Zapret GUI.
    /// </summary>
    internal const int RelayMinNames = 3;

    /// <summary>
    /// Спрашивает один сервер о названных именах, контрольных (<see cref="Controls"/>)
    /// и служебном (<see cref="Intermediary.RelayName"/>); <c>null</c> — сервер не ответил.
    /// </summary>
    public static async Task<ServerAnswers?> AskServerAsync(
        Intermediary server,
        IReadOnlyCollection<string> names,
        CancellationToken cancellationToken)
    {
        var nic = DnsSurvey.PhysicalInterface();

        if (await RouteAsync(server, nic, cancellationToken) is not { } address)
            return null;

        var asked = names.Concat(Controls).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        var unanswered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var slots = new SemaphoreSlim(6);

        // Один клиент на весь опрос сервера: вопросы идут одним соединением HTTP/2.
        // Автозамена спрашивает о каждом имени блока (имена без профиля — у всех
        // посредников), и по соединению на вопрос это были бы сотни рукопожатий
        // за секунды к одному имени DoH (см. DnsSurvey.DohQueryAsync).
        using var http = DnsSurvey.DohClient(address, nic);

        await Task.WhenAll(asked.Select(async host =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                var answer = await DnsSurvey.DohQueryAsync(
                    http, server.DohName, server.Path, host, DnsWire.TypeA, cancellationToken);

                lock (result)
                {
                    result[host] = answer is { Code: 0 } ? answer.Addresses : [];

                    // NXDOMAIN — тоже ответ: имени посредник не знает. А таймаут, обрыв
                    // и SERVFAIL — нет, и окно пина не должно называть их отказом.
                    if (answer is null || answer.Code is not (0 or NxDomain))
                        unanswered.Add(host);
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Один оборванный вопрос не роняет весь опрос: прочие ответы уже
                // пришли, и сервер, до которого достучались, — не молчащий.
                lock (result)
                {
                    result[host] = [];
                    unanswered.Add(host);
                }
            }
            finally
            {
                slots.Release();
            }
        }));

        IReadOnlyList<IPAddress> relay = [];

        try
        {
            if (server.RelayName is { } relayName
                && await DnsSurvey.DohQueryAsync(http, server.DohName, server.Path, relayName, DnsWire.TypeA, cancellationToken)
                    is { Code: 0 } answered)
            {
                relay = answered.Addresses;
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Без служебного имени посредник узнаётся по контрольным — не повод терять опрос.
        }

        return new(result, relay) { Unanswered = unanswered };
    }

    /// <summary>
    /// Посредники сервера по его ответу: адреса, отданные двум контрольным именам
    /// (<see cref="Proxies"/>), трём разным именам его профиля (<see cref="RelayMinNames"/>)
    /// и ответ на служебное имя.
    /// </summary>
    /// <param name="own">
    /// Имена с профилем этого сервера. Правило трёх имён — только по ним, а не по
    /// всем спрошенным: имена без профиля спрашиваются у всех посредников ради
    /// присвоения, и Comss на три имени Google AI отдаёт настоящий адрес Google
    /// (03.11, см. <see cref="Proxies"/>) — он посредником бы и стал.
    /// </param>
    internal static IReadOnlySet<string> Relays(ServerAnswers answers, IReadOnlyCollection<string> own)
    {
        var result = new HashSet<string>(StrongRelays(answers), StringComparer.Ordinal);
        var mine = own.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var group in answers.Names
                     .Where(p => mine.Contains(p.Key))
                     .SelectMany(p => p.Value.Select(a => (Address: a.ToString(), Name: p.Key)))
                     .GroupBy(x => x.Address, StringComparer.Ordinal))
        {
            if (group.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= RelayMinNames)
                result.Add(group.Key);
        }

        result.RemoveWhere(a => !Usable(a));

        return result;
    }

    /// <summary>
    /// Посредники без правила трёх имён: отданные двум контрольным именам разных
    /// владельцев (<see cref="Proxies"/>) и ответ на служебное имя.
    /// </summary>
    /// <remarks>
    /// Настоящим адресом сервиса такой адрес не бывает: адрес Google на claude.ai
    /// не придёт. А правило трёх имён настоящий адрес пропускает и среди имён
    /// своего профиля — бросит посредник проксировать Google, как Comss 03.11, и
    /// трём его именам Google AI придёт один адрес Google. Поэтому пин с живым
    /// первым адресом переезжает только на такие посредники (<see cref="PinRefresh"/>).
    /// </remarks>
    internal static IReadOnlySet<string> StrongRelays(ServerAnswers answers)
    {
        var result = new HashSet<string>(Proxies(answers.Names), StringComparer.Ordinal);

        foreach (var address in answers.RelayCheck)
            result.Add(address.ToString());

        result.RemoveWhere(a => !Usable(a));

        return result;
    }

    private static bool Usable(string address) =>
        IPAddress.TryParse(address, out var ip)
        && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && !BlockCheck.IsStub(ip);

    /// <summary>
    /// Посредники, которых сервер отдаёт имени сейчас, — отсортированные строкой
    /// адреса, а не в порядке ответа, без заглушек.
    /// </summary>
    /// <remarks>
    /// Сортировка нарочно, как в <see cref="ReachAsync"/>: GeoHide и Comss каждый раз
    /// перемешивают узлы, и в порядке ответа имена одного сайта получали бы разные
    /// пары и разный первый адрес (решение 10.10: одному сайту — одни пины).
    /// </remarks>
    internal static IReadOnlyList<string> RelaysFor(ServerAnswers answers, IReadOnlySet<string> relays, string name) =>
        (answers.Names.TryGetValue(name, out var given) ? given : [])
            .Select(a => a.ToString())
            .Where(relays.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Что посредник отдаёт имени прямо сейчас: адрес своего посредника,
    /// настоящий адрес или ничего.
    /// </summary>
    /// <remarks>
    /// Для окна пина, где каждый посредник — свой пункт. Снимок каталога о
    /// посреднике врёт тем больше, чем он старше: у Zapret DNS в снимке 01.11 —
    /// 83.217.211.149, прежний адрес, а с того же дня сервер за Cloudflare
    /// и отдаёт 5.129.221.64. Один вопрос на сервер (имя, контрольные и
    /// служебное — одним соединением, <see cref="AskServerAsync"/>), не залп.
    /// </remarks>
    public static async Task<RelayNow> NowAsync(Intermediary server, string name, CancellationToken cancellationToken)
    {
        // Настоящий адрес — у честного резолвера, тем же шагом: без него «отдаёт
        // настоящий адрес» было бы догадкой (см. Judge).
        var honest = HonestAsync(name, cancellationToken);
        ServerAnswers? answers;

        try
        {
            answers = await AskServerAsync(server, [name], cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Оборванное соединение посередине опроса — для человека то же молчание.
            answers = null;
        }

        return Judge(server, answers, name, await honest);
    }

    /// <summary>Настоящие адреса имени у честного резолвера; <c>null</c> — не узнали.</summary>
    private static async Task<IReadOnlyList<IPAddress>?> HonestAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return (await DnsSurvey.HonestAsync(name, DnsWire.TypeA, DnsSurvey.PhysicalInterface(), cancellationToken))
                is { Code: 0 } answer ? answer.Addresses : null;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Приговор по ответу сервера: посредник — адрес, который проходит
    /// <see cref="StrongRelays"/> (два контрольных сервиса или служебное имя).
    /// </summary>
    /// <param name="honest">Ответ честного резолвера о том же имени; <c>null</c> — не узнали.</param>
    /// <remarks>
    /// Правило трёх имён здесь не годится: спрашивается одно имя. А настоящий
    /// адрес посредник отдаёт тем, кого не проксирует, — 03.11 Comss на имена
    /// Google AI ответил адресом самого Google, и пункт «Comss — сейчас» с ним
    /// прибил бы Gemini к Google, который из России его не открывает. Такой
    /// ответ называется «этот сервис не проксирует», а не предлагается пином.
    /// Но «настоящий» — только когда это подтверждает честный резолвер (та же
    /// сеть /16: у Google и Cloudflare фронты меняются от запроса к запросу).
    /// Адрес, не совпавший ни с посредником контрольных, ни с настоящим, —
    /// «не подтверждено»: посредник может держать сервис на своём узле (в снимке
    /// 01.11 у AstraCat Autodesk и Broadcom на 217.60.179.6) или перемешивать узлы,
    /// как GeoHide и Comss. Такой пункт доступен, а решает проба на главном имени.
    /// </remarks>
    internal static RelayNow Judge(Intermediary server, ServerAnswers? answers, string name, IReadOnlyList<IPAddress>? honest = null)
    {
        if (answers is null)
            return new(server, name, RelayVerdict.Silent, []);

        var strong = StrongRelays(answers);
        var relays = RelaysFor(answers, strong, name);

        if (relays.Count > 0)
        {
            var checkedBy = answers.RelayCheck.Select(a => a.ToString()).ToHashSet(StringComparer.Ordinal);
            return new(server, name, RelayVerdict.Relays, relays, relays.Any(checkedBy.Contains));
        }

        var given = (answers.Names.TryGetValue(name, out var list) ? list : [])
            .Where(a => Usable(a.ToString()))
            .ToList();

        if (given.Count == 0)
        {
            return answers.Unanswered.Contains(name)
                ? new(server, name, RelayVerdict.Unanswered, [])
                : new(server, name, RelayVerdict.Refuses, []);
        }

        var shown = given
            .Select(a => a.ToString())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return honest is not null && given.Any(a => honest.Any(h => SameNet16(a, h)))
            ? new(server, name, RelayVerdict.NotProxied, shown)
            : new(server, name, RelayVerdict.Unconfirmed, shown);
    }

    private static bool SameNet16(IPAddress a, IPAddress b)
    {
        if (a.AddressFamily != b.AddressFamily || a.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var x = a.GetAddressBytes();
        var y = b.GetAddressBytes();

        return x[0] == y[0] && x[1] == y[1];
    }

    /// <summary>Код ответа DNS «нет такого имени».</summary>
    private const int NxDomain = 3;

    /// <summary>Имя, на котором проверяется, что сервер вообще отвечает.</summary>
    private const string Probe = "example.com";

    /// <summary>Адреса, которые посредники отдают имени сейчас, — кандидатами для пина.</summary>
    public static async Task<IReadOnlyList<PinCandidate>> AskAsync(string host, CancellationToken cancellationToken) =>
        (await AskManyAsync([host], cancellationToken)).TryGetValue(host, out var found) ? found : [];

    /// <summary>То же для многих имён.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<PinCandidate>>> AskManyAsync(
        IEnumerable<string> hosts,
        CancellationToken cancellationToken) =>
        (await ReachAsync(hosts, cancellationToken)).Answers;

    /// <summary>
    /// Ответы посредников на имена и список тех, кто не ответил.
    /// </summary>
    /// <remarks>
    /// Рабочий адрес каждого сервера ищется один раз на весь опрос: молчащий
    /// посредник стоит своё ожидание однажды, а не на каждом имени.
    /// </remarks>
    public static async Task<IntermediaryAnswers> ReachAsync(IEnumerable<string> hosts, CancellationToken cancellationToken)
    {
        var nic = DnsSurvey.PhysicalInterface();
        var routes = await Task.WhenAll(Servers.Select(s => RouteAsync(s, nic, cancellationToken)));

        var live = Servers.Zip(routes)
            .Where(p => p.Second is not null)
            .Select(p => (Server: p.First, Address: p.Second!))
            .ToList();

        var silent = Servers.Zip(routes).Where(p => p.Second is null).Select(p => p.First.Name).ToList();
        var wanted = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var asked = wanted.Concat(Controls).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Ответы: посредник → имя → адреса.
        var raw = live.ToDictionary(l => l.Server.Name, _ => new Dictionary<string, IReadOnlyList<IPAddress>>(StringComparer.OrdinalIgnoreCase));
        using var slots = new SemaphoreSlim(6);

        await Task.WhenAll(asked.Select(async host =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                await Task.WhenAll(live.Select(async l =>
                {
                    var answer = await DnsSurvey.DohQueryAsync(
                        l.Address, l.Server.DohName, l.Server.Path, host, DnsWire.TypeA, nic, cancellationToken);

                    lock (raw)
                        raw[l.Server.Name][host] = answer is { Code: 0 } ? answer.Addresses : [];
                }));
            }
            finally
            {
                slots.Release();
            }
        }));

        var proxies = raw.ToDictionary(p => p.Key, p => Proxies(p.Value));
        var result = new Dictionary<string, IReadOnlyList<PinCandidate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var host in wanted)
        {
            result[host] = Candidates(live.Select(l => (
                l.Server.Name,
                // По порядку адресов, а не ответа: GeoHide и Comss каждый раз
                // перемешивают узлы, и имена одного сайта получали бы разные пары
                // (владелец 10.10: «разным доменам одного сайта разные пины — плохая идея»).
                (IReadOnlyList<IPAddress>)(raw[l.Server.Name].TryGetValue(host, out var a) ? a : [])
                    .Where(x => proxies[l.Server.Name].Contains(x.ToString()))
                    .OrderBy(x => x.ToString(), StringComparer.Ordinal)
                    .ToList())));
        }

        return new(result, silent);
    }

    /// <summary>
    /// Имена разных сервисов, о которых посредник спрашивается всегда, — чтобы
    /// узнать его прокси, даже когда умерло одно имя.
    /// </summary>
    private static readonly string[] Controls = ["claude.ai", "chatgpt.com", "grok.com", "gemini.google.com"];

    /// <summary>
    /// Адреса, которые посредник отдаёт хотя бы двум контрольным сервисам
    /// (<see cref="Controls"/>, у всех разные владельцы), — его прокси.
    /// </summary>
    /// <remarks>
    /// Тех, кого не проксирует, посредник отдаёт настоящим адресом. 03.11 Comss
    /// на gemini.google.com, aistudio.google.com и прочие имена Google ответил
    /// адресами самого Google (142.251.x), на Spotify — Spotify; проверка такой
    /// адрес пропускает (сайт же отвечает), и на прогоне по копии hosts владельца
    /// 18 имён Google AI ушли бы на настоящий Google, который из России Gemini не
    /// открывает. «Два разных сайта» не годилось: адрес Google один у google.com,
    /// googleapis.com и googleusercontent.com. Адрес Google в ответ на claude.ai не
    /// придёт никогда, а прокси один на многие сервисы: Comss — 206.223.x на
    /// claude.ai, chatgpt.com, grok.com; у Zapret DNS — 5.129.221.64 на всё.
    /// </remarks>
    internal static IReadOnlySet<string> Proxies(IReadOnlyDictionary<string, IReadOnlyList<IPAddress>> answers) =>
        Controls
            .SelectMany(c => answers.TryGetValue(c, out var a) ? a.Select(x => (Address: x.ToString(), Control: c)) : [])
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .Where(g => g.Select(x => x.Control).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Кандидаты по порядку посредников, без повторов и без заглушек.
    /// </summary>
    internal static IReadOnlyList<PinCandidate> Candidates(IEnumerable<(string Name, IReadOnlyList<IPAddress> Addresses)> answers)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PinCandidate>();

        foreach (var (name, addresses) in answers)
        {
            foreach (var address in addresses)
            {
                // 0.0.0.0 и прочие заглушки посредник отдаёт тем, кого обслуживать не хочет.
                if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                    || BlockCheck.IsStub(address)
                    || !seen.Add(address.ToString()))
                {
                    continue;
                }

                result.Add(new PinCandidate(address.ToString(), PinSource.Catalog, $"{name}, сейчас",
                    Servers.FirstOrDefault(s => s.Name == name)?.Key));
            }
        }

        return result;
    }

    /// <summary>Адрес, по которому сервер отвечает на DoH; <c>null</c> — не ответил ни один.</summary>
    private static async Task<string?> RouteAsync(Intermediary server, int? nic, CancellationToken cancellationToken)
    {
        IReadOnlyList<IPAddress> named = [];

        try
        {
            named = (await DnsSurvey.HonestAsync(server.DohName, DnsWire.TypeA, nic, cancellationToken))?.Addresses ?? [];
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Честный резолвер не ответил — остаются встроенные адреса.
        }

        var addresses = named
            .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .Select(a => a.ToString())
            .Concat(server.Fallback)
            .Distinct(StringComparer.Ordinal)
            .Take(6);

        foreach (var address in addresses)
        {
            if (await DnsSurvey.DohQueryAsync(address, server.DohName, server.Path, Probe, DnsWire.TypeA, nic, cancellationToken) is not null)
                return address;
        }

        return null;
    }
}
