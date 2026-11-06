using System.Net;
using System.Net.Sockets;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;

namespace NetZapret.Proxy;

/// <summary>Что ответила Windows на имя «через VPN».</summary>
public enum TunnelNameKind
{
    /// <summary>Подставной адрес движка — имя пойдёт в туннель.</summary>
    Fake,

    /// <summary>Настоящий адрес — соединение пойдёт мимо туннеля.</summary>
    Real,

    /// <summary>Имя не разрешилось — судить не о чем.</summary>
    Unresolved,
}

/// <param name="Real">Настоящие адреса — у <see cref="TunnelNameKind.Real"/>.</param>
public sealed record TunnelNameVerdict(TunnelNameKind Kind, string Name, IReadOnlyList<IPAddress> Addresses, IReadOnlyList<IPAddress> Real);

/// <summary>
/// Получают ли имена «через VPN» подставной адрес, когда их разрешает сама Windows.
/// </summary>
/// <remarks>
/// <para>
/// В «Гибриде» TUN выборочный: в туннель попадают подставные адреса, которые
/// движок выдаёт именам «через VPN», и немногие сети из правил. Имя, получившее
/// настоящий адрес, уходит мимо туннеля, и ни окно, ни движок этого не видят.
/// </para>
/// <para>
/// Обсуждение №55 (05.11): у человека с YogaDNS 2ip.io по правилу «через VPN»
/// получил 188.40.167.81 и пошёл напрямую, а «Наблюдение» писало «туннель».
/// Тот же исход у любого, чей DNS отвечает раньше движка или мимо него: другой
/// DNS-клиент, резолвер, которого TUN не забирает (сеть сменилась после
/// запуска — резолверы читаются при сборке конфига), запрос Windows сразу
/// по всем адаптерам. Вопрос задаётся тем же путём, что у любой программы, —
/// через DNS Windows, поэтому видно то, что увидит браузер.
/// </para>
/// </remarks>
public static class TunnelNameCheck
{
    /// <summary>
    /// Имена, которые правила ведут в VPN, — до <paramref name="count"/>, в порядке правил.
    /// </summary>
    /// <param name="pinned">
    /// Имена из hosts, все действующие строки, не только нашего блока: им адрес
    /// даёт файл, а не движок, и в туннель их ведёт сеть пина в TUN.
    /// </param>
    public static IReadOnlyList<string> Candidates(RuleEngine engine, IReadOnlySet<string> pinned, int count = 3)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in engine.RuleSet.Rules.Where(r => r.Mode == RoutingMode.Proxy))
        {
            IEnumerable<string> names = rule.Match switch
            {
                MatchKind.Domain => [rule.Value],
                MatchKind.HostList => rule.HostListDomains.Take(20),
                _ => [],
            };

            foreach (var raw in names)
            {
                var name = Plain(raw);

                if (name is null || pinned.Contains(name) || !seen.Add(name))
                    continue;

                // Выше может стоять правило, уводящее имя в другую сторону:
                // судить надо по имени, которое и правда идёт в VPN.
                var decision = engine.Evaluate(new ConnectionEvent
                {
                    Timestamp = DateTimeOffset.Now,
                    Protocol = ProtocolKind.Tcp,
                    RemotePort = 443,
                    Hostname = name,
                });

                if (decision.Mode != RoutingMode.Proxy)
                    continue;

                result.Add(name);

                if (result.Count >= count)
                    return result;
            }
        }

        return result;
    }

    /// <summary>Имя из шаблона: <c>*.2ip.io</c> → <c>2ip.io</c>; шаблон внутри — <c>null</c>.</summary>
    internal static string? Plain(string value)
    {
        var name = value.Trim().ToLowerInvariant().TrimEnd('.');

        if (name.StartsWith("*.", StringComparison.Ordinal))
            name = name[2..];

        name = name.TrimStart('.');

        return name.Length == 0 || name.Contains('*') || !name.Contains('.') || name.Contains(' ')
            ? null
            : name;
    }

    /// <summary>Судит ответ Windows на одно имя.</summary>
    /// <remarks>
    /// Смотрятся только семейства, для которых у движка есть подставной
    /// диапазон: без IPv6 у TUN подставного IPv6 нет, и настоящий AAAA
    /// туннеля не касается — без IPv6 в Windows по нему не соединиться.
    /// </remarks>
    public static TunnelNameVerdict Judge(string name, IReadOnlyList<IPAddress> addresses, IReadOnlyList<string> fakeRanges)
    {
        var ranges = fakeRanges
            .Select(r => { try { return IpCidrRange.Parse(r); } catch (RuleConfigurationException) { return null; } })
            .OfType<IpCidrRange>()
            .ToList();

        var families = fakeRanges
            .Select(r => r.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork)
            .ToHashSet();

        var judged = addresses
            .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Where(a => families.Contains(a.AddressFamily))
            .ToList();

        if (judged.Count == 0)
            return new(TunnelNameKind.Unresolved, name, addresses, []);

        var real = judged.Where(a => !ranges.Any(r => r.Contains(a))).ToList();

        return new(real.Count > 0 ? TunnelNameKind.Real : TunnelNameKind.Fake, name, judged, real);
    }

    /// <summary>
    /// Спрашивает Windows о каждом имени по очереди, до первого ответа.
    /// </summary>
    /// <returns><c>null</c> — имён нет.</returns>
    public static async Task<TunnelNameVerdict?> CheckAsync(
        IReadOnlyList<string> names,
        IReadOnlyList<string> fakeRanges,
        CancellationToken cancellationToken,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        resolve ??= Dns.GetHostAddressesAsync;

        TunnelNameVerdict? last = null;

        foreach (var name in names)
        {
            IPAddress[] addresses;

            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limit.CancelAfter(TimeSpan.FromSeconds(4));
                addresses = await resolve(name, limit.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                last = new(TunnelNameKind.Unresolved, name, [], []);
                continue;
            }

            var verdict = Judge(name, addresses, fakeRanges);

            if (verdict.Kind != TunnelNameKind.Unresolved)
                return verdict;

            last = verdict;
        }

        return last;
    }

    /// <summary>
    /// Резолверы Windows, которых TUN не забирает: их запросы и ответы идут мимо движка.
    /// </summary>
    /// <param name="resolvers">Резолверы префиксами (<see cref="SystemResolvers.Discover"/>).</param>
    /// <param name="tunRoutes">Сети TUN (<see cref="TunnelEndpoints.TunRoutes"/>).</param>
    /// <remarks>
    /// Резолверы читаются при сборке конфига (<c>TunnelConfig</c>), и смену сети
    /// надзор не замечает: новый роутер с другим адресом остаётся вне TUN до
    /// перезапуска движков. Резолвер по локальному IPv6 (<c>fe80::</c>) сюда
    /// тоже попадает отдельно — <see cref="IsLinkLocal"/>.
    /// </remarks>
    public static IReadOnlyList<string> Outside(IReadOnlyList<string> resolvers, IReadOnlyList<string> tunRoutes)
    {
        var routes = tunRoutes
            .Select(r => { try { return IpCidrRange.Parse(r); } catch (RuleConfigurationException) { return null; } })
            .OfType<IpCidrRange>()
            .ToList();

        return [.. resolvers.Where(r =>
            IPAddress.TryParse(r.Split('/')[0], out var address)
            && !routes.Any(range => range.Contains(address)))];
    }

    /// <summary>
    /// Резолвер по локальному IPv6 (<c>fe80::/10</c>) — обычно роутер.
    /// </summary>
    /// <remarks>
    /// Маршрут к нему через TUN конфиг ставит (<see cref="SystemResolvers.ToPrefix"/>
    /// отрезает номер адаптера), но запрос к такому адресу Windows отправляет
    /// с номером адаптера, и, по устройству IPv6, именно в тот адаптер, мимо
    /// маршрутов. Не замерено: у владельца такого резолвера нет.
    /// </remarks>
    public static bool IsLinkLocal(string resolver) =>
        IPAddress.TryParse(resolver.Split('/')[0], out var address) && address.IsIPv6LinkLocal;
}
