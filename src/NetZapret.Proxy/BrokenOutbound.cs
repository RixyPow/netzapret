using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace NetZapret.Proxy;

/// <summary>
/// Сервер, на котором движок отказался подняться, — вон из конфига.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №38: публичные подписки на тысячи серверов несут ключи, которые
/// sing-box не принимает, и один такой ронял туннель целиком — «initialize
/// outbound[N]: …». За неделю три разные причины: REALITY без ключа (25.10),
/// flow xtls-rprx-vision-udp443 (27.10), битый %-код в пути xhttp (28.10).
/// Каждую мы отсеивали при разборе, уже после того, как у человека всё легло.
/// Владелец 28.10 на предложение: «делай» — надзор убирает такой сервер сам
/// и поднимает движок снова.
/// </para>
/// <para>
/// Убирается только выход-сервер. Группы, direct, block и dns не трогаются;
/// если без сервера опустеет группа или на него ссылается правило — конфиг
/// не правится, и движок падает, как прежде: чинить вслепую хуже, чем сказать.
/// </para>
/// </remarks>
public static class BrokenOutbound
{
    private static readonly Regex Initialize = new(
        @"initialize (outbound|endpoint)\[(\d+)\]",
        RegexOptions.CultureInvariant);

    /// <summary>Типы, которые сервером не являются: их отказ — поломка конфига, а не ключа.</summary>
    private static readonly HashSet<string> NotServers = new(StringComparer.OrdinalIgnoreCase)
    {
        "direct", "block", "dns", "selector", "urltest", "loadbalance",
    };

    private static readonly JsonSerializerOptions Written = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Конфиг без сервера, названного в отказе движка.</summary>
    /// <param name="config">Текст конфига sing-box.</param>
    /// <param name="fatal">Строка отказа движка (FATAL).</param>
    /// <returns>Новый текст и имя убранного сервера; <c>null</c> — убирать нечего или нельзя.</returns>
    public static (string Config, string Tag)? Remove(string config, string? fatal)
    {
        if (fatal is null || Initialize.Match(fatal) is not { Success: true } match)
            return null;

        JsonObject root;

        try
        {
            root = JsonNode.Parse(config) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return null;
        }

        var section = match.Groups[1].Value + "s";

        if (root[section] is not JsonArray list
            || !int.TryParse(match.Groups[2].Value, out int index)
            || index < 0 || index >= list.Count
            || list[index] is not JsonObject broken
            || (string?)broken["tag"] is not { Length: > 0 } tag
            || (string?)broken["type"] is not { } type
            || NotServers.Contains(type))
        {
            return null;
        }

        // Группы, где сервер стоит, — без него; опустевшая группа движок
        // уронила бы уже на себе.
        foreach (var group in (root["outbounds"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (group["outbounds"] is not JsonArray members)
                continue;

            var mine = members.Where(m => (string?)m == tag).ToList();

            if (mine.Count == 0)
                continue;

            if (mine.Count == members.Count)
                return null;

            foreach (var member in mine)
                members.Remove(member);

            if ((string?)group["default"] == tag)
                group.Remove("default");
        }

        // Ссылки на сервер вне групп — правило, detour, финал маршрута: без
        // него конфиг не соберётся, а подставлять другой выход — решать за человека.
        list.RemoveAt(index);

        if (Mentions(root, tag))
            return null;

        return (root.ToJsonString(Written), tag);
    }

    /// <summary>Строка, равная тегу, где-нибудь в конфиге.</summary>
    private static bool Mentions(JsonNode? node, string tag) => node switch
    {
        JsonObject o => o.Any(p => p.Key != "tag" && Mentions(p.Value, tag)),
        JsonArray a => a.Any(n => Mentions(n, tag)),
        JsonValue v => v.TryGetValue<string>(out var s) && s == tag,
        _ => false,
    };
}
