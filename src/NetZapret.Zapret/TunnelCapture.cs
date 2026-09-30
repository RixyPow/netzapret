using System.Net;
using System.Net.Sockets;

namespace NetZapret.Zapret;

/// <summary>
/// Выводит соединения туннеля с его серверами из перехвата winws2.
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
    /// </remarks>
    public const int Limit = 40;

    /// <summary>Дописывает исключение к готовой строке запуска.</summary>
    /// <param name="fakeRange">
    /// Диапазон подменных адресов туннеля (<c>198.18.0.0/15</c>); <c>null</c> — не выводить.
    /// </param>
    public static void Apply(List<string> arguments, IReadOnlyList<IPAddress>? servers, string? fakeRange = null)
    {
        var filter = Filter((servers ?? []).Take(Limit).ToList(), fakeRange);

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
        IReadOnlyList<string> arguments, IReadOnlyList<IPAddress> servers, string? fakeRange = null)
    {
        var kept = servers.Take(Limit).ToList();
        var filter = Filter(kept, fakeRange);

        return filter.Length > 0
            && arguments.Any(a => a.StartsWith(Key, StringComparison.OrdinalIgnoreCase)
                && a.Contains(filter, StringComparison.Ordinal))
            ? kept.Count
            : 0;
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
    /// </remarks>
    public static string Filter(IReadOnlyList<IPAddress> servers, string? fakeRange = null)
    {
        var tests = new List<string>();

        if (Bounds(fakeRange) is var (first, last))
        {
            tests.Add($"(ip.DstAddr>={first} and ip.DstAddr<={last})");
            tests.Add($"(ip.SrcAddr>={first} and ip.SrcAddr<={last})");
        }

        foreach (var server in servers)
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
}
