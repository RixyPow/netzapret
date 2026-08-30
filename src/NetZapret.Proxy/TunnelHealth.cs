using System.Net;
using System.Net.Sockets;

namespace NetZapret.Proxy;

/// <summary>Что происходит с туннелем на момент проверки.</summary>
public enum TunnelState
{
    /// <summary>Движки не запущены — туннеля нет и быть не должно.</summary>
    Off,

    /// <summary>Через туннель проходит трафик.</summary>
    Alive,

    /// <summary>Туннель поднят, но не пропускает ничего.</summary>
    Dead,

    /// <summary>Спросить не удалось; утверждать нечего.</summary>
    Unknown,
}

/// <summary>Куда вышел замер и через что он шёл.</summary>
/// <remarks>
/// Второе не менее важно первого. Внешний адрес сам по себе не говорит,
/// чей он: с включённым туннелем ответ службы определения адреса может
/// прийти и по домашнему каналу, и тогда «страна выхода» описывает
/// провайдера, а не сервер подписки.
/// </remarks>
public sealed record ExitReading
{
    public string? Address { get; init; }

    /// <summary>Двухбуквенный код страны; <c>null</c> — не выяснилось.</summary>
    public string? Country { get; init; }

    /// <summary>Шёл ли сам замер через туннель.</summary>
    public required bool Tunnelled { get; init; }

    /// <summary>
    /// Туннель заведомо выходит в России.
    /// </summary>
    /// <remarks>
    /// Требует обоих условий сразу. Российская страна при замере мимо туннеля
    /// — это домашний провайдер, и приписывать её туннелю нельзя: именно так
    /// рождается совет сменить рабочий сервер ни за чем.
    /// </remarks>
    public bool TunnelExitsDomestically =>
        Tunnelled && string.Equals(Country, "RU", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Отличает беду туннеля от беды сайта.
/// </summary>
/// <remarks>
/// <para>
/// Заведено по случаю, стоившему дня разбирательств. У пользователя разом
/// перестали работать WhatsApp и Telegram. Проверка показала на них
/// блокировки и предложила менять маршруты — а в логе sing-box лежало
/// около четырёх тысяч одинаковых строк «timeout: no recent network
/// activity», и ни один из пятнадцати серверов подписки не отвечал.
/// Туннель был мёртв целиком, и все вердикты по проксируемым именам
/// описывали не сайты, а его труп.
/// </para>
/// <para>
/// Отсюда правило: пока не известно, жив ли туннель, судить о сайтах,
/// которые в него заведены, нельзя. Проверка обязана сказать это первой
/// строкой, а не прятать за два десятка вердиктов.
/// </para>
/// </remarks>
public static class TunnelHealth
{
    /// <summary>Диапазон fakeip для IPv4 по умолчанию.</summary>
    public const string DefaultFakeIpV4Range = "198.18.0.0/15";

    /// <summary>Диапазон fakeip для IPv6 по умолчанию.</summary>
    public const string DefaultFakeIpV6Range = "fc00::/18";

    /// <summary>
    /// Адрес выдан подменой, а значит соединение уйдёт в туннель.
    /// </summary>
    /// <remarks>
    /// Единственный доступный снаружи признак «это пойдёт через туннель».
    /// Настоящий адрес означает обратное: правило маршрутизации отправило
    /// имя напрямую, и что бы мы по нему ни измерили, к туннелю оно
    /// отношения не имеет.
    /// </remarks>
    public static bool IsFakeIp(IPAddress address, string? v4Range = null, string? v6Range = null)
    {
        var range = address.AddressFamily == AddressFamily.InterNetworkV6
            ? v6Range ?? DefaultFakeIpV6Range
            : v4Range ?? DefaultFakeIpV4Range;

        return InRange(address, range);
    }

    /// <summary>Попадает ли адрес в подсеть, записанную как CIDR.</summary>
    public static bool InRange(IPAddress address, string cidr)
    {
        var slash = cidr.IndexOf('/');

        if (slash < 0
            || !IPAddress.TryParse(cidr[..slash], out var network)
            || !int.TryParse(cidr[(slash + 1)..], out var bits))
        {
            return false;
        }

        if (network.AddressFamily != address.AddressFamily)
            return false;

        var left = address.GetAddressBytes();
        var right = network.GetAddressBytes();

        if (left.Length != right.Length || bits < 0 || bits > left.Length * 8)
            return false;

        // Побайтно, пока хватает целых байтов; остаток — маской по старшим битам.
        int whole = bits / 8;

        for (int i = 0; i < whole; i++)
        {
            if (left[i] != right[i])
                return false;
        }

        int rest = bits % 8;

        if (rest == 0)
            return true;

        var mask = (byte)(0xFF << (8 - rest));

        return (left[whole] & mask) == (right[whole] & mask);
    }

    /// <summary>
    /// Стоит ли верить вердиктам по именам, заведённым в туннель.
    /// </summary>
    /// <remarks>
    /// Мёртвый туннель делает бессмысленным любой вывод о проксируемом имени:
    /// измерена труба, а не сайт. Неизвестное состояние такого права не даёт —
    /// молчание проверки хуже ложной тревоги, но хуже обоих утверждение,
    /// которого никто не мерил.
    /// </remarks>
    public static bool VerdictsAreMeaningful(TunnelState state) =>
        state is not TunnelState.Dead;

    /// <summary>Короткая строка о состоянии — для шапки отчёта.</summary>
    public static string Describe(TunnelState state) => state switch
    {
        TunnelState.Off => "не запущен",
        TunnelState.Alive => "проходит трафик",
        TunnelState.Dead => "поднят, но не пропускает ничего",
        _ => "состояние не выяснено",
    };
}
