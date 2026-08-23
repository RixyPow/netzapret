using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetZapret.Proxy;

/// <summary>
/// Читает адреса резолверов, настроенных в системе.
/// </summary>
/// <remarks>
/// Только чтение: настройки DNS не меняются. Адреса нужны, чтобы при
/// выборочном перехвате завести в туннель сами запросы DNS — иначе fakeip
/// нечего подменять.
/// </remarks>
public static class SystemResolvers
{
    /// <summary>
    /// Признаки собственного туннельного адаптера в описании драйвера.
    /// </summary>
    private static readonly string[] TunnelMarkers = ["sing-tun", "wintun", "netzapret"];

    /// <summary>
    /// Возвращает адреса резолверов активных интерфейсов в виде префиксов.
    /// </summary>
    /// <remarks>
    /// Собственный TUN пропускается намеренно. Его резолвер живёт внутри
    /// туннеля, и заведя этот адрес в перехват, мы получили бы петлю:
    /// запрос к резолверу туннеля отправлялся бы в тот же туннель.
    /// Адаптер вдобавок может остаться поднятым после жёсткой остановки,
    /// и тогда его DNS попадёт в выборку, даже когда sing-box не работает.
    /// </remarks>
    public static IReadOnlyList<string> Discover()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up)
                continue;

            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;

            if (IsOwnTunnel(adapter))
                continue;

            foreach (var address in adapter.GetIPProperties().DnsAddresses)
            {
                // Петлевые резолверы заводить в туннель бессмысленно и опасно:
                // получилась бы петля через собственный процесс.
                if (IPAddress.IsLoopback(address))
                    continue;

                int bits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
                var cidr = $"{address}/{bits}";

                if (seen.Add(cidr))
                    result.Add(cidr);
            }
        }

        return result;
    }

    private static bool IsOwnTunnel(NetworkInterface adapter)
    {
        foreach (var marker in TunnelMarkers)
        {
            if (adapter.Description.Contains(marker, StringComparison.OrdinalIgnoreCase)
                || adapter.Name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
