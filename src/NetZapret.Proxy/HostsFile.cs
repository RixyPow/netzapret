using System.Net;
using System.Net.Sockets;

namespace NetZapret.Proxy;

/// <summary>
/// Читает системный файл hosts.
/// </summary>
/// <remarks>
/// <para>
/// Нужен потому, что hosts бьёт любой резолв, включая наш. Домен, прибитый
/// там к адресу, никогда не дойдёт до sing-box и не получит fakeip — Windows
/// ответит из файла. Для правила <c>mode: proxy</c> это значит, что оно молча
/// не работает: трафик уходит напрямую на прибитый адрес, под блокировку.
/// </para>
/// <para>
/// Ровно так и вышло с Canva, RuTracker и LinkedIn: записи оставил редактор
/// hosts из Zapret GUI, и со стороны это выглядело как неисправный VPN.
/// Bandcamp в файле не значился и работал — по этому расхождению причину
/// и нашли.
/// </para>
/// <para>
/// Только чтение. Файл ведёт другая программа, и вычищать её записи мы
/// не вправе: они могут быть нужны для десинка.
/// </para>
/// </remarks>
public static class HostsFile
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers",
        "etc",
        "hosts");

    /// <summary>
    /// Возвращает соответствие «имя — прибитые адреса».
    /// </summary>
    public static IReadOnlyDictionary<string, List<IPAddress>> Read(string? path = null)
    {
        var result = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);
        var target = path ?? DefaultPath;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(target);
        }
        catch (Exception)
        {
            // Недоступен или отсутствует — считаем, что записей нет.
            // Ради чтения hosts запуск останавливать незачем.
            return result;
        }

        foreach (var raw in lines)
        {
            var line = raw;
            int comment = line.IndexOf('#');

            if (comment >= 0)
                line = line[..comment];

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            // Минимум адрес и одно имя.
            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address))
                continue;

            foreach (var name in parts.Skip(1))
            {
                if (!result.TryGetValue(name, out var list))
                    result[name] = list = [];

                if (!list.Contains(address))
                    list.Add(address);
            }
        }

        return result;
    }

    /// <summary>
    /// Ищет адреса, прибитые к домену или любому его поддомену.
    /// </summary>
    /// <param name="domain">Имя без ведущей звёздочки, например <c>canva.com</c>.</param>
    /// <remarks>
    /// Поддомены учитываются потому, что правило пишется на всю зону
    /// (<c>*.canva.com</c>), а в hosts обычно перечислены конкретные имена
    /// вроде <c>static.canva.com</c>. Проверять только точное совпадение
    /// значило бы пропустить как раз то, что чаще всего и прибито.
    /// </remarks>
    public static IReadOnlyList<IPAddress> FindPinned(
        IReadOnlyDictionary<string, List<IPAddress>> hosts,
        string domain)
    {
        var found = new List<IPAddress>();

        foreach (var (name, addresses) in hosts)
        {
            bool matches = string.Equals(name, domain, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

            if (!matches)
                continue;

            foreach (var address in addresses)
            {
                // Петлевые и нулевые адреса ставят, чтобы заблокировать имя,
                // а не увести его. Заводить их в туннель бессмысленно.
                if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any))
                    continue;

                if (!found.Contains(address))
                    found.Add(address);
            }
        }

        return found;
    }

    public static string ToCidr(IPAddress address) =>
        $"{address}/{(address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32)}";

    /// <summary>
    /// Находит адреса, прибитые в hosts к доменам с правилом <c>mode: proxy</c>,
    /// чтобы завести их в туннель принудительно.
    /// </summary>
    /// <remarks>
    /// Иначе такие домены оставались бы вне туннеля независимо от правил:
    /// fakeip им не выдаётся, а перехват идёт по адресу назначения. Заводим
    /// прибитый адрес — трафик доходит до sing-box, тот по SNI опознаёт домен
    /// и применяет к нему обычное правило.
    /// </remarks>
    /// <param name="notes">Что нашлось — для показа пользователю.</param>
    /// <param name="hostsPath">Путь к файлу; <c>null</c> — системный.</param>
    public static IReadOnlyList<string> CollectPinnedProxyAddresses(
        Core.Rules.RuleSet ruleSet,
        out IReadOnlyList<string> notes,
        string? hostsPath = null)
    {
        var found = new List<string>();
        var messages = new List<string>();
        notes = messages;

        var hosts = Read(hostsPath);

        if (hosts.Count == 0)
            return found;

        foreach (var rule in ruleSet.Rules)
        {
            if (rule.Mode != Core.Rules.RoutingMode.Proxy || rule.Match != Core.Rules.MatchKind.Domain)
                continue;

            var domain = rule.Value.StartsWith("*.", StringComparison.Ordinal)
                ? rule.Value[2..]
                : rule.Value;

            if (domain.Contains('*'))
                continue;

            var pinned = FindPinned(hosts, domain);

            if (pinned.Count == 0)
                continue;

            foreach (var address in pinned)
            {
                var cidr = ToCidr(address);

                if (!found.Contains(cidr))
                    found.Add(cidr);
            }

            messages.Add(
                $"{domain} прибит в hosts к {string.Join(", ", pinned)} — " +
                "адрес заведён в туннель, иначе правило не сработало бы");
        }

        return found;
    }
}
