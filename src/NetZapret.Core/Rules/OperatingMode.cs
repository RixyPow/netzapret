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
    /// Сети виртуальных локалок для игр: Radmin VPN — 26.0.0.0/8, Hamachi —
    /// 25.0.0.0/8 и 2620:9b::/96.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Владелец 14.10: функцию Радмина не встраиваем, но и мешать ему не должны.
    /// Пакеты к этим адресам идут через адаптер Радмина или Hamachi уже
    /// расшифрованными — это сама игра по «локальной сети». Десинк в них
    /// вставлял бы подделки, туннелю и правилам там делать нечего.
    /// </para>
    /// <para>
    /// Сети чужие — их «одолжили» у военных ведомств Великобритании и США.
    /// В ipset-all Flowseal, по которому работает game filter, лежат три куска
    /// 25/8 (25.25.25.0/24, 25.26.27.0/24, 25.129.196.0/22, сверка 14.10):
    /// с ним winws2 подделывал бы игру через Hamachi. Откуда они там —
    /// не выяснено.
    /// </para>
    /// <para>
    /// Сети записаны по общеизвестному, не сняты с адаптера: ни Радмина,
    /// ни Hamachi у владельца нет. Чем Radmin адресует IPv6, не проверено;
    /// если это fc00::/7, он и так локальный и winws2 его не берёт.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> VirtualLans { get; } = ["25.0.0.0/8", "26.0.0.0/8", "2620:9b::/96"];

    /// <summary>
    /// Проверяет, относится ли адрес к локальным: loopback, приватные диапазоны
    /// RFC 1918, link-local, CGNAT, виртуальные локалки (<see cref="VirtualLans"/>),
    /// а также их аналоги в IPv6.
    /// </summary>
    public static bool IsLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || IsUniqueLocalV6(address) || IsHamachiV6(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var octets = address.GetAddressBytes();

        return octets[0] switch
        {
            10 => true,                                     // 10.0.0.0/8
            25 or 26 => true,                               // Hamachi и Radmin VPN
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

    /// <summary>2620:9b::/96 — Hamachi.</summary>
    private static bool IsHamachiV6(IPAddress address)
    {
        var bytes = address.GetAddressBytes();

        return bytes[0] == 0x26 && bytes[1] == 0x20 && bytes[2] == 0x00 && bytes[3] == 0x9b
            && bytes[4..12].All(b => b == 0);
    }
}
