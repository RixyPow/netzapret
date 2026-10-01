using System.Net;
using System.Net.Sockets;

namespace NetZapret.Zapret;

/// <summary>
/// Выводит соединения туннеля с его серверами из перехвата winws2 — а с 30.09
/// и UDP к сетям, которым десинк не нужен (<see cref="UdpOffDesync"/>).
/// </summary>
/// <remarks>
/// <para>
/// Замер 30.09 — в <c>TunnelEndpoints</c>: с десинком туннель шёл вдесятеро
/// медленнее, а winws2 занимал ядро целиком. Профиль, который пропустил бы
/// эти пакеты (<c>pass</c>, <c>--ipset-exclude</c>), тут не помощник: до профиля
/// пакет уже снят с сети и отдан в winws2. Убрать его можно только из самого
/// фильтра WinDivert.
/// </para>
/// <para>
/// Ключ — <c>--wf-raw-filter</c>: по справке winws2 «partial raw windivert
/// filter combined by AND, only one allowed». Как он встаёт в итоговый фильтр,
/// снято 30.09 ключом <c>--wf-save</c> на winws2 1.0.3: условием «И» на весь
/// перехват — и на порты, и на все <c>--wf-raw-part</c> пресета.
/// </para>
/// <para>
/// Запись — <c>(… ? false : true)</c>, и другой тут нет. Проверено 30.09
/// на WinDivert.dll из поставки, <c>WinDivertHelperCompileFilter</c>
/// и <c>WinDivertHelperEvalFilter</c>: отрицание скобки, <c>!(…)</c>
/// и <c>not (…)</c>, не разбирается вовсе — winws2 с таким фильтром
/// не запустился бы; а <c>ip.DstAddr!=…</c> ложно на пакете IPv6, где поля
/// нет, и выкинуло бы из перехвата весь IPv6.
/// </para>
/// </remarks>
public static class TunnelCapture
{
    private const string Key = "--wf-raw-filter=";

    /// <summary>
    /// Сколько адресов помещается в фильтр.
    /// </summary>
    /// <remarks>
    /// Фильтр WinDivert ограничен числом проверок, адрес стоит двух. Замер
    /// 30.09 тем же <c>WinDivertHelperCompileFilter</c>: перехват V10 принимает
    /// 75 адресов и не принимает 80; самый широкий из поставки, Default v1
    /// с game filter, — 60 и не принимает 70 («Filter expression too long»),
    /// IPv4 и IPv6 одинаково. Сорок оставляют запас под пресет тяжелее;
    /// остальные серверы остаются в перехвате, как были.
    /// Вместе с подменными адресами и сетями Riot (30.09, winws2 <c>--wf-save</c>
    /// и тот же WinDivert): Default v1 с game filter принимает 52 адреса
    /// и не принимает 56, V10 — 56.
    /// </remarks>
    public const int Limit = 40;

    /// <summary>Дописывает исключение к готовой строке запуска.</summary>
    /// <param name="fakeRange">
    /// Диапазон подменных адресов туннеля (<c>198.18.0.0/15</c>); <c>null</c> — не выводить.
    /// </param>
    /// <param name="udpOff">
    /// Сети, UDP к которым десинку не отдаётся вовсе (<see cref="UdpOffDesync"/>).
    /// </param>
    /// <param name="tun">Адреса нашего TUN: пакеты в туннель и из него.</param>
    public static void Apply(
        List<string> arguments,
        IReadOnlyList<IPAddress>? servers,
        string? fakeRange = null,
        IReadOnlyList<string>? udpOff = null,
        IReadOnlyList<IPAddress>? tun = null)
    {
        var filter = Filter((servers ?? []).Take(Limit).ToList(), fakeRange, udpOff, tun);

        if (filter.Length == 0)
            return;

        int index = arguments.FindIndex(a => a.StartsWith(Key, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            // Глобальный ключ от места в строке не зависит (WinwsCommandLine.SplitGlobals).
            arguments.Insert(0, Key + filter);
            return;
        }

        var theirs = arguments[index][Key.Length..];

        // Ключ у winws2 один на запуск. Фильтр пресета, заданный файлом,
        // сюда не дописать: серверы остаются в перехвате, запуск об этом скажет.
        if (theirs.StartsWith('@'))
            return;

        arguments[index] = $"{Key}({theirs}) and {filter}";
    }

    /// <summary>Сколько из этих адресов строка запуска выводит из перехвата.</summary>
    public static int Applied(
        IReadOnlyList<string> arguments,
        IReadOnlyList<IPAddress> servers,
        string? fakeRange = null,
        IReadOnlyList<string>? udpOff = null,
        IReadOnlyList<IPAddress>? tun = null) =>
        Carries(arguments, servers, fakeRange, udpOff, tun) ? Math.Min(servers.Count, Limit) : 0;

    /// <summary>Встало ли исключение в строку запуска.</summary>
    /// <remarks>
    /// Не встаёт, когда у пресета свой <c>--wf-raw-filter</c> файлом, — и тогда
    /// ничего из переданного из перехвата не выведено.
    /// </remarks>
    public static bool Carries(
        IReadOnlyList<string> arguments,
        IReadOnlyList<IPAddress> servers,
        string? fakeRange = null,
        IReadOnlyList<string>? udpOff = null,
        IReadOnlyList<IPAddress>? tun = null)
    {
        var filter = Filter(servers.Take(Limit).ToList(), fakeRange, udpOff, tun);

        return filter.Length > 0
            && arguments.Any(a => a.StartsWith(Key, StringComparison.OrdinalIgnoreCase)
                && a.Contains(filter, StringComparison.Ordinal));
    }

    /// <summary>Условие «пакет не от сервера и не к нему»; нет адресов — пусто.</summary>
    /// <remarks>
    /// <para>
    /// С диапазоном подменных адресов — ещё и «пакет не в туннель». Имени,
    /// уведённому в VPN, движок выдаёт адрес из 198.18.0.0/15, и соединение
    /// идёт на адаптер туннеля. WinDivert снимает пакеты и с него, а свои
    /// «местные» адреса winws2 исключает сам только для 10/8, 172.16/12,
    /// 192.168/16 и 169.254/16 (текст фильтра, <c>--wf-save</c>, 30.09) —
    /// этого диапазона среди них нет. Если такое имя есть в списках пресета,
    /// десинк резал и подделывал пакеты соединения, которое и так уходит
    /// в туннель и в десинке не нуждается.
    /// </para>
    /// <para>
    /// Подменные адреса IPv6 (fc00::/18) отдельной строки не требуют: fc00::/7
    /// winws2 исключает сам. Чем это исключение отзывается на скорости,
    /// на 30.09 не замерено.
    /// </para>
    /// <para>
    /// С сетями <paramref name="udpOff"/> — ещё и «не UDP к ним». Только
    /// исходящий: UDP winws2 перехватывает на выходе (<c>--wf-udp-out</c>,
    /// снято <c>--wf-save</c> 30.09), а входящий — лишь по разбору нагрузки
    /// в частях фильтра пресета. Сети сливаются в диапазоны: у Riot 22 записи
    /// дают 13 диапазонов и 27 проверок вместо 45. Место в фильтре общее
    /// с серверами туннеля (см. <see cref="Limit"/>).
    /// </para>
    /// </remarks>
    public static string Filter(
        IReadOnlyList<IPAddress> servers,
        string? fakeRange = null,
        IReadOnlyList<string>? udpOff = null,
        IReadOnlyList<IPAddress>? tun = null)
    {
        var tests = new List<string>();

        if (Bounds(fakeRange) is var (first, last))
        {
            tests.Add($"(ip.DstAddr>={first} and ip.DstAddr<={last})");
            tests.Add($"(ip.SrcAddr>={first} and ip.SrcAddr<={last})");
        }

        var ranges = Ranges(udpOff ?? []);

        if (ranges.Count > 0)
        {
            var inside = ranges.Select(r =>
            {
                var field = r.First.AddressFamily == AddressFamily.InterNetwork ? "ip" : "ipv6";
                return $"({field}.DstAddr>={r.First} and {field}.DstAddr<={r.Last})";
            });

            tests.Add($"(udp and ({string.Join(" or ", inside)}))");
        }

        // Адреса нашего TUN — тем же сравнением, что серверы: пакет с ними
        // идёт в туннель или из него, а через десинк он пройдёт на выходе
        // из sing-box (TunnelEndpoints.TunAddresses, замер 01.10).
        foreach (var server in (tun ?? []).Concat(servers))
        {
            // Адрес IPv6 — без зоны (%12): в фильтре ей места нет.
            var (field, text) = server.AddressFamily switch
            {
                AddressFamily.InterNetwork => ("ip", server.ToString()),
                AddressFamily.InterNetworkV6 => ("ipv6", new IPAddress(server.GetAddressBytes()).ToString()),
                _ => (null, null),
            };

            if (field is null)
                continue;

            tests.Add($"{field}.DstAddr={text}");
            tests.Add($"{field}.SrcAddr={text}");
        }

        return tests.Count == 0 ? string.Empty : $"({string.Join(" or ", tests)} ? false : true)";
    }

    /// <summary>Первый и последний адрес диапазона IPv4 вида 198.18.0.0/15; не разобрался — <c>null</c>.</summary>
    public static (IPAddress First, IPAddress Last)? Bounds(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
            return null;

        var parts = range.Trim().Split('/');

        if (parts.Length != 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out int bits)
            || bits is < 8 or > 32)
        {
            return null;
        }

        var bytes = address.GetAddressBytes();
        uint value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        uint mask = bits == 32 ? uint.MaxValue : ~(uint.MaxValue >> bits);
        uint first = value & mask;
        uint last = first | ~mask;

        static IPAddress From(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

        return (From(first), From(last));
    }

    /// <summary>
    /// Сети списком слитых диапазонов: сперва IPv4, затем IPv6, по возрастанию.
    /// </summary>
    /// <remarks>
    /// Соседние и вложенные сети сливаются в одну: 138.0.12.0/23, 138.0.14.0/24
    /// и 138.0.15.0/24 — это один диапазон и две проверки вместо шести.
    /// Неразобранная запись пропускается.
    /// </remarks>
    public static IReadOnlyList<(IPAddress First, IPAddress Last)> Ranges(IEnumerable<string> cidrs)
    {
        var v4 = new List<(UInt128 First, UInt128 Last)>();
        var v6 = new List<(UInt128 First, UInt128 Last)>();

        foreach (var cidr in cidrs)
        {
            if (Span(cidr) is not var (family, first, last))
                continue;

            (family == AddressFamily.InterNetwork ? v4 : v6).Add((first, last));
        }

        return
        [
            .. Merge(v4).Select(r => (Address(r.First, 4), Address(r.Last, 4))),
            .. Merge(v6).Select(r => (Address(r.First, 16), Address(r.Last, 16))),
        ];
    }

    /// <summary>Семейство, первый и последний адрес сети; не разобралась — <c>null</c>.</summary>
    private static (AddressFamily Family, UInt128 First, UInt128 Last)? Span(string? cidr)
    {
        if (string.IsNullOrWhiteSpace(cidr))
            return null;

        var parts = cidr.Trim().Split('/');

        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var address))
            return null;

        int width = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => 0,
        };

        int bits = width;

        if (width == 0 || (parts.Length == 2 && (!int.TryParse(parts[1], out bits) || bits < 0 || bits > width)))
            return null;

        UInt128 value = 0;

        foreach (var octet in address.GetAddressBytes())
            value = (value << 8) | octet;

        // Биты узла; у IPv6 со всеми 128 сдвиг на ширину не определён — отдельно.
        UInt128 host = bits == width
            ? UInt128.Zero
            : width == 128 ? UInt128.MaxValue >> bits : (UInt128.One << (width - bits)) - 1;

        UInt128 start = value & ~host;
        return (address.AddressFamily, start, start | host);
    }

    private static List<(UInt128 First, UInt128 Last)> Merge(List<(UInt128 First, UInt128 Last)> spans)
    {
        var merged = new List<(UInt128 First, UInt128 Last)>();

        foreach (var span in spans.OrderBy(s => s.First))
        {
            if (merged.Count > 0
                && (merged[^1].Last == UInt128.MaxValue || span.First <= merged[^1].Last + 1))
            {
                if (span.Last > merged[^1].Last)
                    merged[^1] = (merged[^1].First, span.Last);

                continue;
            }

            merged.Add(span);
        }

        return merged;
    }

    private static IPAddress Address(UInt128 value, int length)
    {
        var bytes = new byte[length];

        for (int i = length - 1; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return new IPAddress(bytes);
    }
}
