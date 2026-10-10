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

    /// <param name="fakeRanges">Диапазоны подставных адресов движка (<c>inet4_range</c>, <c>inet6_range</c>).</param>
    /// <param name="servers">Адреса серверов VPN из конфига движка.</param>
    public WatchContext(IEnumerable<string> fakeRanges, IEnumerable<IPAddress> servers)
    {
        _fake = fakeRanges
            .Select(r => { try { return IpCidrRange.Parse(r); } catch (RuleConfigurationException) { return null; } })
            .OfType<IpCidrRange>()
            .ToList();

        _servers = servers.Select(Normal).ToHashSet();
    }

    /// <summary>Без движка: ни подставных адресов, ни серверов.</summary>
    public static WatchContext None { get; } = new([], []);

    public bool IsFake(IPAddress address) => _fake.Any(r => r.Contains(Normal(address)));

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
