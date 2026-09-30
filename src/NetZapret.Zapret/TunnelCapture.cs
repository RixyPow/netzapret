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
    public static void Apply(List<string> arguments, IReadOnlyList<IPAddress>? servers)
    {
        var filter = Filter((servers ?? []).Take(Limit).ToList());

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
    public static int Applied(IReadOnlyList<string> arguments, IReadOnlyList<IPAddress> servers)
    {
        var kept = servers.Take(Limit).ToList();
        var filter = Filter(kept);

        return filter.Length > 0
            && arguments.Any(a => a.StartsWith(Key, StringComparison.OrdinalIgnoreCase)
                && a.Contains(filter, StringComparison.Ordinal))
            ? kept.Count
            : 0;
    }

    /// <summary>Условие «пакет не от сервера и не к нему»; нет адресов — пусто.</summary>
    public static string Filter(IReadOnlyList<IPAddress> servers)
    {
        var tests = new List<string>();

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
}
