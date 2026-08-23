using System.Net;
using System.Net.Sockets;

namespace NetZapret.Core.Rules;

/// <summary>
/// Глобальный режим работы диспетчера. Задаёт рамку, внутри которой вообще
/// имеют смысл правила.
/// </summary>
public enum OperatingMode
{
    /// <summary>
    /// Ничего не применяется: весь трафик идёт напрямую, правила не вычисляются.
    /// </summary>
    /// <remarks>
    /// Оговорка: NetZapret в этом режиме гарантирует только отсутствие прокси.
    /// Остановить сам десинк он пока не может — winws2 живёт своей жизнью под
    /// управлением Zapret GUI. Полноценное выключение появится вместе с модулем
    /// управления winws2.
    /// </remarks>
    Off = 0,

    /// <summary>
    /// Рабочий режим: по умолчанию всё отдаётся десинку, а прокси включается
    /// выборочно — правилами для конкретных приложений и доменов.
    /// </summary>
    Selective = 1,

    /// <summary>
    /// Весь трафик через прокси. Правила не вычисляются, но loopback и приватные
    /// сети всё равно остаются прямыми — заворачивать их в туннель бессмысленно
    /// и вредно.
    /// </summary>
    ProxyAll = 2,
}

/// <summary>
/// Адреса, которые не заворачиваются в туннель ни при каких настройках.
/// </summary>
public static class LocalNetworks
{
    /// <summary>
    /// Проверяет, относится ли адрес к локальным: loopback, приватные диапазоны
    /// RFC 1918, link-local, CGNAT, а также их аналоги в IPv6.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IsUniqueLocalV6(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,                                     // 10.0.0.0/8
            127 => true,                                    // 127.0.0.0/8
            169 when octets[1] == 254 => true,              // 169.254.0.0/16 link-local
            172 when octets[1] >= 16 && octets[1] <= 31 => true, // 172.16.0.0/12
            192 when octets[1] == 168 => true,              // 192.168.0.0/16
            100 when octets[1] >= 64 && octets[1] <= 127 => true, // 100.64.0.0/10 CGNAT
            _ => false,
        };
    }

    /// <summary>fc00::/7 — unique local addresses.</summary>
    private static bool IsUniqueLocalV6(IPAddress address) =>
        (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
}
