using System.Net;
using System.Net.Sockets;

namespace NetZapret.Proxy;

/// <summary>
/// Сети Cloudflare и его запасные адреса, на которых отвечает любой его сайт.
/// </summary>
/// <remarks>
/// <para>
/// Перечень — официальный (<c>cloudflare.com/ips-v4</c>, сверен 05.11), а не
/// списки Zapret GUI: в <c>ipset-cloudflare1</c> лежит Amazon, в
/// <c>cloudflare-ipset.txt</c> — Hetzner (см. <c>AddressOwners</c>). Плюс две
/// сети вне перечня, которые Cloudflare тоже обслуживает, — 8.6.112.0/24 и
/// 8.47.69.0/24.
/// </para>
/// <para>
/// Зачем запасные. На ntc.party (тема 17013, 29.10–02.11) у сайтов Cloudflare
/// на 188.114.96.0/22, 104.16.0.0/12 и 172.64.0.0/13 резали приветствие,
/// разбитое на два пакета, и часть адресов закрывали по TCP целиком, а на
/// 8.6.112.x и 8.47.69.x блока не было; люди прибивали имя туда в hosts.
/// Замер 05.11 у владельца (curl через Wi-Fi, под нашим десинком, 20 сайтов):
/// оба запасных отдают любой сайт Cloudflare тот же ответ до байта (discord.com
/// 170 418 Б, shopify 676 178, cloudflare.com 1 334 523); www.patreon.com
/// на своём 104.16.25.14 молчит 8 с, на обоих запасных отвечает; check-host.net
/// на 8.6.112.6 молчит, на 8.47.69.6 отвечает — поэтому пробуются оба.
/// Имя, которое режут по имени (rutracker.org, www.canva.com), запасной адрес
/// не спасает: молчит и там.
/// </para>
/// </remarks>
public static class CloudflareNet
{
    /// <summary>Запасные адреса по порядку проб.</summary>
    public static IReadOnlyList<string> Spares { get; } = ["8.47.69.6", "8.6.112.6"];

    private static readonly (uint From, uint To)[] Ranges = Parse(
    [
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "8.6.112.0/24", "8.47.69.0/24",
    ]);

    /// <summary>Адрес в сетях Cloudflare (только IPv4).</summary>
    public static bool Contains(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        uint value = ToUInt(address);

        return Ranges.Any(r => value >= r.From && value <= r.To);
    }

    public static bool Contains(string? address) =>
        IPAddress.TryParse(address, out var parsed) && Contains(parsed);

    private static (uint, uint)[] Parse(string[] cidrs) => cidrs.Select(text =>
    {
        int slash = text.IndexOf('/');
        uint start = ToUInt(IPAddress.Parse(text[..slash]));
        int bits = int.Parse(text[(slash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        uint mask = uint.MaxValue << (32 - bits);

        return (start & mask, (start & mask) | ~mask);
    }).ToArray();

    private static uint ToUInt(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }
}
