using System.Net;
using System.Net.Sockets;
using NetZapret.Core.Rules;

namespace NetZapret.Core.Connections;

/// <summary>Куда в наблюдении ушло соединение.</summary>
/// <remarks>
/// Шире <see cref="RoutingMode"/>: кроме ответа правил тут есть то, что
/// наблюдение знает само, — локальное и соединения движка.
/// </remarks>
public enum WatchRoute
{
    Direct,
    Proxy,
    Desync,

    /// <summary>Домашняя сеть, loopback, мультикаст — правила их не касаются.</summary>
    Local,

    /// <summary>Соединение самого sing-box: к серверу VPN или выход «напрямую» из туннеля.</summary>
    Engine,
}

/// <summary>
/// Что наблюдению известно о движке: подставные адреса и серверы VPN.
/// </summary>
/// <remarks>
/// <para>
/// Нужен затем, что правила (<see cref="RuleEngine"/>) про движок не знают.
/// Замер 10.10 у владельца, 301 соединение: api.anthropic.com записан
/// «туннель» 18 раз (подставной 198.18.0.11) и «напрямую, локальная сеть»
/// 13 раз (подставной <c>[fc00::b]</c> — правила считают весь fc00::/7
/// домашней сетью), а маршрут Windows вёл оба в <c>netzapret0</c>.
/// sing-box.exe к серверу VPN — «десинк» 29 раз; мультикаст Spotify, Steam
/// и ChatGPT — тоже «десинк».
/// </para>
/// <para>
/// Подставной адрес — единственное, что наблюдение знает о маршруте точно:
/// движок выдаёт его только именам, идущим через VPN.
/// </para>
/// </remarks>
public sealed class WatchContext
{
    /// <summary>Программы движка: их соединения правилами не судятся.</summary>
    private static readonly HashSet<string> EngineProcesses = new(StringComparer.OrdinalIgnoreCase) { "sing-box.exe" };

    private readonly IReadOnlyList<IpCidrRange> _fake;
    private readonly HashSet<IPAddress> _servers;

    /// <summary>Сети, которые забирает TUN; <c>null</c> — неизвестно или всё.</summary>
    private readonly IReadOnlyList<IpCidrRange>? _tunRoutes;

    /// <summary>Сети, выведенные из TUN (<c>route_exclude_address</c>).</summary>
    private readonly IReadOnlyList<IpCidrRange> _tunExcluded;

    /// <param name="fakeRanges">Диапазоны подставных адресов движка (<c>inet4_range</c>, <c>inet6_range</c>).</param>
    /// <param name="servers">Адреса серверов VPN из конфига движка.</param>
    /// <param name="tunRoutes">
    /// Что забирает выборочный TUN (<c>route_address</c>); <c>null</c> — не судить,
    /// мимо ли туннеля соединение: перехват полный или о нём ничего не известно.
    /// </param>
    /// <param name="tunExcluded">Что из TUN выведено (<c>route_exclude_address</c>).</param>
    /// <param name="fakeForEveryName">
    /// Движок отдаёт подставной адрес любому имени («Туннель»): тогда подставной
    /// адрес значит только «через движок», а путь решают правила по имени.
    /// </param>
    public WatchContext(
        IEnumerable<string> fakeRanges,
        IEnumerable<IPAddress> servers,
        IEnumerable<string>? tunRoutes = null,
        IEnumerable<string>? tunExcluded = null,
        bool fakeForEveryName = false)
    {
        _fake = Ranges(fakeRanges);
        _servers = servers.Select(Normal).ToHashSet();
        _tunRoutes = tunRoutes is null ? null : Ranges(tunRoutes);
        _tunExcluded = Ranges(tunExcluded ?? []);
        FakeForEveryName = fakeForEveryName;
    }

    /// <summary>
    /// Подставной адрес у всех имён, не только у «через VPN»
    /// (<c>TunnelEndpoints.FakeForEveryName</c>).
    /// </summary>
    public bool FakeForEveryName { get; }

    private static List<IpCidrRange> Ranges(IEnumerable<string> cidrs) => cidrs
        .Select(r => { try { return IpCidrRange.Parse(r); } catch (RuleConfigurationException) { return null; } })
        .OfType<IpCidrRange>()
        .ToList();

    /// <summary>Без движка: ни подставных адресов, ни серверов.</summary>
    public static WatchContext None { get; } = new([], []);

    public bool IsFake(IPAddress address) => _fake.Any(r => r.Contains(Normal(address)));

    /// <summary>
    /// Уходит ли соединение с этим адресом мимо выборочного TUN.
    /// </summary>
    /// <remarks>
    /// Обсуждение №55 (05.11): при YogaDNS имя 2ip.io по правилу «через VPN»
    /// получило настоящий 188.40.167.81, а не подставной, и наблюдение писало
    /// «туннель» по ответу правил, хотя адрес в TUN не попадал вовсе. Подставной
    /// адрес TUN забирает всегда; настоящий — только когда он в его сетях
    /// (правила по адресу, пины, резолверы) и не выведен из них.
    /// </remarks>
    public bool BypassesTun(IPAddress address)
    {
        if (_tunRoutes is null || _fake.Count == 0)
            return false;

        address = Normal(address);

        return !_tunRoutes.Any(r => r.Contains(address)) || _tunExcluded.Any(r => r.Contains(address));
    }

    public bool IsServer(IPAddress address) => _servers.Contains(Normal(address));

    public static bool IsEngine(string? executableName) => executableName is not null && EngineProcesses.Contains(executableName);

    /// <summary>Мультикаст и широковещание — поиск устройств в сети, а не выход наружу.</summary>
    public static bool IsMulticast(IPAddress address)
    {
        address = Normal(address);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6Multicast;

        var bytes = address.GetAddressBytes();

        return bytes[0] is >= 224 and <= 239 || address.Equals(IPAddress.Broadcast);
    }

    private static IPAddress Normal(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
