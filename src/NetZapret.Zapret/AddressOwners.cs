using System.Net;
using System.Net.Sockets;
using NetZapret.Core.Rules;

namespace NetZapret.Zapret;

/// <summary>
/// Чья сеть у адреса — Cloudflare, Google, Amazon… — по спискам адресов Zapret GUI.
/// </summary>
/// <remarks>
/// <para>
/// Колонка «Сеть» проверки блокировок (владелец 27.10: «ну можно попробовать»).
/// Нужна она ради одного вывода: «режут по адресу» у сайта на Cloudflare — это
/// беда сети доставки, а не сайта, и лечится иначе. Чужой справочник (ipinfo,
/// whois) не спрашивается: это отправка адресов человека постороннему сервису.
/// </para>
/// <para>
/// Берутся только списки с источником — номером сети в шапке файла (ipinfo AS…)
/// или официальным перечнем (Telegram) — и Google, у которого шапки нет, а сети
/// его собственные (сверка 27.10). Прочие подписаны не тем, что в них лежит:
/// в ipset-cloudflare1 есть сети Amazon (3.64.0.0/12, 44.224.0.0/11),
/// в ipset-steam — узлы Akamai, в ipset-github — Azure; назвать по ним сеть
/// значило бы соврать. cloudflare-ipset.txt тоже мимо: в шапке AS13335, а внутри
/// и Hetzner (95.216.0.0/15), и Akamai.
/// </para>
/// <para>
/// Первый совпавший источник побеждает. Взятые списки между собой не пересекаются
/// (сверка 27.10), кроме двух списков Google и одного адреса OVH (5.196.172.106)
/// в списке Cloudflare — поэтому OVH раньше.
/// </para>
/// </remarks>
public sealed class AddressOwners
{
    public static IReadOnlyList<(string Owner, string File)> Sources { get; } =
    [
        ("Telegram", "ipset-telegram.txt"),
        ("Meta", "ipset-facebook.txt"),
        ("Google", "ipset-google.txt"),
        ("Google", "ipset-usa-google.txt"),
        ("OVH", "ipset-ovh.txt"),
        ("Cloudflare", "ipset-cloudflare.txt"),
        ("Fastly", "ipset-fastly.txt"),
        ("Akamai", "ipset-akamai.txt"),
        ("Amazon", "ipset-amazon.txt"),
        ("Hetzner", "ipset-hetzner.txt"),
        ("DigitalOcean", "ipset-digitalocean.txt"),
        ("CDN77", "ipset-datacamp.txt"),
    ];

    private readonly List<(string Owner, (uint From, uint To)[] V4, IpCidrRange[] V6)> _owners;

    private AddressOwners(List<(string, (uint, uint)[], IpCidrRange[])> owners) => _owners = owners;

    /// <summary>Пусто: ни одного списка не нашлось.</summary>
    public static AddressOwners None { get; } = new([]);

    /// <summary>
    /// Читает списки из <c>lists</c> корня Zapret; в рабочей копии — из копии
    /// Zapret GUI (<c>vendor/zapret-gui/lists</c>).
    /// </summary>
    public static AddressOwners Load(string? zapretRoot)
    {
        var owners = new List<(string, (uint, uint)[], IpCidrRange[])>();

        foreach (var (owner, file) in Sources)
        {
            if (Find(file, zapretRoot) is not { } path)
                continue;

            var ranges = AddressListReader.Expand([path], null, out _);

            owners.Add(Parse(owner, ranges));
        }

        return new AddressOwners(owners);
    }

    /// <summary>Из готовых строк CIDR — для тестов и своих перечней.</summary>
    public static AddressOwners From(params (string Owner, string[] Ranges)[] sources) =>
        new(sources.Select(s => Parse(s.Owner, s.Ranges)).ToList());

    /// <summary>Чья сеть; <c>null</c> — ни в одном списке.</summary>
    public string? Of(string? address)
    {
        if (address is null || !IPAddress.TryParse(address, out var ip))
            return null;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            uint value = ToUInt(ip);

            foreach (var (owner, v4, _) in _owners)
            {
                if (Inside(v4, value))
                    return owner;
            }

            return null;
        }

        foreach (var (owner, _, v6) in _owners)
        {
            if (v6.Any(r => r.Contains(ip)))
                return owner;
        }

        return null;
    }

    private static string? Find(string file, string? zapretRoot)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrEmpty(zapretRoot))
            candidates.Add(Path.Combine(zapretRoot, "lists", file));

        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "vendor", "zapret-gui", "lists", file));

        return candidates.FirstOrDefault(File.Exists);
    }

    private static (string, (uint, uint)[], IpCidrRange[]) Parse(string owner, IReadOnlyList<string> ranges)
    {
        var v4 = new List<(uint From, uint To)>();
        var v6 = new List<IpCidrRange>();

        foreach (var text in ranges)
        {
            int slash = text.IndexOf('/');

            if (slash < 0 || !IPAddress.TryParse(text[..slash], out var ip) || !int.TryParse(text[(slash + 1)..], out int bits))
                continue;

            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                v6.Add(IpCidrRange.Parse(text));
                continue;
            }

            if (bits is < 0 or > 32)
                continue;

            uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
            uint from = ToUInt(ip) & mask;

            v4.Add((from, from | ~mask));
        }

        // Отсортированы по началу, а вложенные и пересекающиеся слиты: поиск —
        // делением пополам по непересекающимся отрезкам.
        var merged = new List<(uint From, uint To)>();

        foreach (var range in v4.OrderBy(r => r.From))
        {
            if (merged.Count > 0 && range.From <= merged[^1].To)
            {
                var last = merged[^1];
                merged[^1] = (last.From, Math.Max(last.To, range.To));
            }
            else
            {
                merged.Add(range);
            }
        }

        return (owner, merged.ToArray(), v6.ToArray());
    }

    private static bool Inside((uint From, uint To)[] ranges, uint value)
    {
        int low = 0, high = ranges.Length - 1;

        while (low <= high)
        {
            int middle = (low + high) / 2;

            if (value < ranges[middle].From)
                high = middle - 1;
            else if (value > ranges[middle].To)
                low = middle + 1;
            else
                return true;
        }

        return false;
    }

    private static uint ToUInt(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
    }
}
