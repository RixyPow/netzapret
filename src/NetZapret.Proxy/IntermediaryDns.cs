using System.Net;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>
/// Спрашивает у DNS-посредников (XBOX DNS, Comss), какой адрес они отдают имени сейчас.
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
/// Обычный DNS по UDP, запасом — по TCP, сокет привязан к физическому
/// адаптеру (<see cref="DnsSurvey.PhysicalInterface"/>): при работающих
/// движках иначе ответил бы наш собственный fakeip. Замер 28.09: Xbox DNS
/// 111.88.96.54, Xbox DNS v2 87.228.47.200 и Comss 83.220.169.155 отвечали
/// на прямые запросы; «Xbox DNS (old)» 176.99.11.77 и dns.malw.link — нет,
/// их здесь нет.
/// </para>
/// <para>
/// AstraCat и GeoHide появились в каталоге Zapret GUI 2026.10.01.1. Замер
/// 06.10: оба их сервера отвечают на прямые запросы (AstraCat 135.106.217.200
/// и 135.106.197.22, GeoHide 193.233.112.67 и .68), а GeoHide уже отдаёт
/// не тот адрес, что записан в каталоге, — снимок отстал за пять дней.
/// dns.malw.link и «Xbox DNS (old)» по-прежнему молчат. У «Malw DNS v2»
/// своего сервера нет вовсе: в каталоге это только набор ответов.
/// </para>
/// <para>
/// 08.10, после «блокировки» XBOX 07.10: сайт xbox-dns.ru называет 111.88.96.54
/// и .55, «v2» не называет вовсе, но 87.228.47.200 отвечает; оба отдают уже
/// новые прокси 188.68.214.131 и .144 (замер по одному запросу).
/// </para>
/// </remarks>
public static class IntermediaryDns
{
    /// <summary>Посредники: как назвать и куда спрашивать. Адреса — из списка Zapret GUI.</summary>
    public static IReadOnlyList<(string Name, string Server)> Servers { get; } =
    [
        ("XBOX DNS", "111.88.96.54"),
        ("XBOX DNS v2", "87.228.47.200"),
        ("Comss DNS", "83.220.169.155"),
        ("AstraCat", "135.106.217.200"),
        ("GeoHide", "193.233.112.67"),
    ];

    /// <summary>Адреса, которые посредники отдают имени сейчас, — кандидатами для пина.</summary>
    public static async Task<IReadOnlyList<PinCandidate>> AskAsync(string host, CancellationToken cancellationToken)
    {
        var nic = DnsSurvey.PhysicalInterface();

        var answers = await Task.WhenAll(Servers.Select(async s =>
        {
            var (answer, _, _) = await DnsSurvey.UdpAsync(s.Server, host, 1, nic, cancellationToken);
            answer ??= await DnsSurvey.TcpAsync(s.Server, host, 1, nic, cancellationToken);

            return (s.Name, Addresses: answer is { Code: 0 } ? answer.Addresses : []);
        }));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<PinCandidate>();

        foreach (var (name, addresses) in answers)
        {
            foreach (var address in addresses)
            {
                // Заглушки посредник отдаёт тем, кого обслуживать не хочет.
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

    /// <summary>То же для многих имён, по нескольку разом: DNS дёшев, но не бесплатен.</summary>
    public static async Task<IReadOnlyDictionary<string, IReadOnlyList<PinCandidate>>> AskManyAsync(
        IEnumerable<string> hosts,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyList<PinCandidate>>(StringComparer.OrdinalIgnoreCase);
        using var slots = new SemaphoreSlim(6);

        await Task.WhenAll(hosts.Distinct(StringComparer.OrdinalIgnoreCase).Select(async host =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                var found = await AskAsync(host, cancellationToken);

                lock (result)
                    result[host] = found;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Не ответил посредник — у имени просто нет живых кандидатов.
            }
            finally
            {
                slots.Release();
            }
        }));

        return result;
    }
}
