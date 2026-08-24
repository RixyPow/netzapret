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
    /// Ничего не применяется: весь трафик идёт напрямую. Ни один движок
    /// не запускается.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Рабочий режим: по умолчанию всё отдаётся десинку, а прокси включается
    /// выборочно — правилами для конкретных приложений и доменов.
    /// </summary>
    Selective = 1,

    /// <summary>
    /// Весь трафик через прокси, кроме выведенных напрямую.
    /// </summary>
    /// <remarks>
    /// Правила <c>mode: direct</c> сохраняются: российские сервисы через
    /// зарубежный адрес не работают вовсе — банки и госуслуги требуют
    /// подтверждений, часть отказывает наотрез. Правила на прокси, наоборот,
    /// отбрасываются как избыточные: всё невыведенное и так уходит в туннель.
    /// </remarks>
    ProxyAll = 2,

    /// <summary>
    /// Только десинк: VPN не поднимается вовсе.
    /// </summary>
    /// <remarks>
    /// Отличается от <see cref="Off"/> тем, что winws2 работает. Нужен, когда
    /// подписка кончилась или сервер лёг, а десинка на всё хватает: поднимать
    /// туннель ради ничего значит без причины держать TUN и путать поиск
    /// неисправностей.
    /// </remarks>
    DesyncOnly = 3,

    /// <summary>
    /// Весь трафик через прокси без единого исключения.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="ProxyAll"/> и намеренно неудобно: здесь в туннель
    /// уходят и российские сервисы, которые от этого ломаются. Смысл режима —
    /// проверка и случаи, когда исключения мешают: сравнить поведение,
    /// убедиться, что дело не в правилах.
    /// </remarks>
    ProxyStrict = 4,
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
