using System.Net;
using System.Text.Json.Nodes;
using NetZapret.Core.Rules;

namespace NetZapret.Proxy;

/// <summary>
/// Через что разрешается имя на этой машине — по шагам, как идёт сам запрос.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 30.09: «добавь себе возможность видеть, через что резолвится DNS».
/// В тот день Claude при поднятых движках висел на «Waiting for Claude»,
/// а Aternos и GitHub отваливались — и прошло полночи, прежде чем выяснилось,
/// что все имена мимо VPN разрешались через туннель: когда сервер замирал,
/// у машины пропадал DNS. «Спрашивать напрямую» на вкладке DNS это сняло.
/// </para>
/// <para>
/// Решения здесь своего нет. Запись в hosts, перехват адресов резолверов
/// туннелем, правила DNS движка — всё читается из того, что есть сейчас:
/// из hosts и из конфига работающего sing-box. Правила DNS проходятся по порядку,
/// первое совпавшее решает, — как это делает сам движок; угадывать по
/// настройкам значило бы завести вторую копию сборки конфига, и она
/// разошлась бы с первой.
/// </para>
/// </remarks>
public static class DnsPath
{
    /// <summary>
    /// Шаги разрешения имени словами — по строке на шаг.
    /// </summary>
    /// <param name="hosts">Все записи hosts: имя → адреса (<see cref="HostsFile.Read"/>).</param>
    /// <param name="engineConfig">Конфиг работающего sing-box; <c>null</c> — не прочитался.</param>
    /// <param name="tunnelRunning">Поднят ли туннель прямо сейчас.</param>
    /// <param name="systemResolvers">Резолверы Windows префиксами (<see cref="SystemResolvers.Discover"/>).</param>
    public static IReadOnlyList<string> Explain(
        string name,
        IReadOnlyDictionary<string, List<IPAddress>> hosts,
        JsonNode? engineConfig,
        bool tunnelRunning,
        IReadOnlyList<string> systemResolvers)
    {
        var lines = new List<string>();
        name = name.Trim().TrimEnd('.').ToLowerInvariant();

        // Hosts Windows читает раньше любой сети: ни резолвер, ни движок
        // такого имени не увидят вовсе.
        if (hosts.TryGetValue(name, out var pinned) && pinned.Count > 0)
        {
            lines.Add($"hosts: {string.Join(", ", pinned)} — Windows отвечает сама, запрос в сеть не уходит");
            return lines;
        }

        var resolvers = systemResolvers.Count == 0
            ? "резолверы не найдены"
            : string.Join(", ", systemResolvers.Select(Plain));

        if (!tunnelRunning || engineConfig is null)
        {
            lines.Add($"резолвер Windows ({resolvers}) — туннель не поднят, программа в разрешение имён не вмешивается");
            return lines;
        }

        var tun = (engineConfig["inbounds"] as JsonArray)?
            .FirstOrDefault(i => (string?)i?["type"] == "tun");

        if (tun is null)
        {
            lines.Add($"резолвер Windows ({resolvers}) — в конфиге движка нет туннеля, запрос идёт мимо него");
            return lines;
        }

        // Выборочный перехват заводит в туннель только перечисленные адреса,
        // и среди них — адреса резолверов Windows: так движок видит запросы.
        // Без списка перехват полный, и туда попадает всё.
        var capture = (tun["route_address"] as JsonArray)?
            .Select(n => (string?)n)
            .OfType<string>()
            .ToList() ?? [];

        var captured = capture.Count == 0
            ? systemResolvers.ToList()
            : systemResolvers.Where(r => capture.Any(c => Covers(c, r))).ToList();

        if (captured.Count == 0)
        {
            lines.Add($"резолвер Windows ({resolvers}) — его адреса не в перехвате туннеля, движок запрос не видит");
            return lines;
        }

        lines.Add($"Windows спрашивает {string.Join(", ", captured.Select(Plain))} — запрос уходит в туннель, отвечает движок");

        var dns = engineConfig["dns"];
        var (tag, why) = Match(dns, name);

        lines.Add($"движок: {Describe(dns, tag, name)}" + (why is null ? string.Empty : $" ({why})"));

        return lines;
    }

    /// <summary>Первое совпавшее правило DNS движка; не совпало ни одно — final.</summary>
    internal static (string Tag, string? Why) Match(JsonNode? dns, string name)
    {
        foreach (var rule in (dns?["rules"] as JsonArray ?? []).OfType<JsonObject>())
        {
            // Правило с условием, которого здесь не проверить (набор правил,
            // процесс, тип запроса кроме A/AAAA), честнее пропустить, чем
            // выдать за совпавшее: такие движок сам не пишет.
            if (rule.ContainsKey("rule_set") || rule.ContainsKey("process_name"))
                continue;

            bool hasName = rule.ContainsKey("domain") || rule.ContainsKey("domain_suffix");
            bool matched = Names(rule, "domain").Any(d => d == name)
                || Names(rule, "domain_suffix").Any(s => name == s || name.EndsWith("." + s, StringComparison.Ordinal));

            if (hasName && !matched)
                continue;

            if ((string?)rule["server"] is not { } server)
                continue;

            var why = !hasName
                ? "правило для всех имён"
                : Names(rule, "domain").Any(d => d == name) ? "имя в правиле DNS" : "домен в правиле DNS";

            return (server, why);
        }

        return ((string?)dns?["final"] ?? "?", "ни одно правило не подошло — резолвер по умолчанию");
    }

    private static IEnumerable<string> Names(JsonObject rule, string key) =>
        (rule[key] as JsonArray ?? [])
            .Select(n => ((string?)n ?? string.Empty).Trim().TrimEnd('.').ToLowerInvariant())
            .Where(n => n.Length > 0);

    /// <summary>Резолвер движка словами.</summary>
    public static string Describe(JsonNode? dns, string tag, string name)
    {
        var server = (dns?["servers"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .FirstOrDefault(s => (string?)s["tag"] == tag);

        if (server is null)
            return $"резолвер «{tag}» — в конфиге его нет";

        var type = (string?)server["type"] ?? "?";

        switch (type)
        {
            case "fakeip":
                return $"подставной адрес из {(string?)server["inet4_range"] ?? "диапазона fakeip"} — имя уходит в туннель, "
                    + "настоящий адрес узнаёт сервер VPN, когда пойдёт соединение";

            case "local":
                return "резолвер Windows, мимо туннеля";

            case "hosts":
                var address = (server["predefined"]?[name] as JsonArray)?.Select(n => (string?)n).FirstOrDefault();
                return $"подставленный адрес {address ?? "?"} (config/addresses.yaml)";
        }

        var kind = type switch
        {
            "https" => "DoH",
            "tls" => "DoT",
            "quic" => "DoQ",
            "h3" => "DoH3",
            "udp" => "обычный DNS по UDP",
            "tcp" => "обычный DNS по TCP",
            _ => type,
        };

        var target = (string?)server["server"] ?? "?";

        return server["detour"] is { } detour
            ? $"{kind} к {target} через туннель (выход «{(string?)detour}») — зависит от здоровья VPN-сервера"
            : $"{kind} к {target} напрямую, мимо туннеля";
    }

    /// <summary>Покрывает ли префикс перехвата адрес резолвера.</summary>
    private static bool Covers(string capture, string resolver)
    {
        try
        {
            var address = IPAddress.Parse(Plain(resolver));
            return IpCidrRange.Parse(capture).Contains(address);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Адрес без хвоста /32 и /128 — для людей.</summary>
    private static string Plain(string prefix)
    {
        int slash = prefix.IndexOf('/');
        return slash < 0 ? prefix : prefix[..slash];
    }
}
