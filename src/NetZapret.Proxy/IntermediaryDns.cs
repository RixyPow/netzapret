using System.Net;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Посредник: как назвать, имя его DoH и адреса на случай, когда имя не узналось.</summary>
public sealed record Intermediary(string Name, string DohName, IReadOnlyList<string> Fallback, string Path = "/dns-query");

/// <summary>Что ответили посредники: адреса по именам и кто не ответил вовсе.</summary>
public sealed record IntermediaryAnswers(
    IReadOnlyDictionary<string, IReadOnlyList<PinCandidate>> Answers,
    IReadOnlyList<string> Silent);

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
        new("Comss DNS", "dns.comss.one", ["212.109.221.20", "195.133.25.16", "212.109.195.93"]),
        new("Zapret DNS", "dns.zapret.moe", ["188.114.96.1", "188.114.97.1"]),
        new("GeoHide EU", "eu.geohide.ru", ["5.175.161.3", "5.175.161.40", "143.20.64.192"]),
        new("GeoHide US", "us.geohide.ru", ["192.255.159.240", "192.255.159.241"]),
        new("DNS-AI", "dns.dns-ai.ru", ["194.87.196.13", "45.151.102.219"]),
        new("AstraCat", "dns.astracat.network", ["135.106.217.200", "135.106.197.22"]),
    ];

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

                result.Add(new PinCandidate(address.ToString(), PinSource.Catalog, $"{name}, сейчас"));
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
