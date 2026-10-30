using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetZapret.Subscriptions;

/// <summary>Настройки mKCP в именах сборки extended — или почему их не собрать.</summary>
/// <param name="Options">Объект JSON без <c>type</c>; <c>null</c> — когда отказ.</param>
/// <param name="Refusal">Почему движок этот сервер не повезёт; <c>null</c> — повезёт.</param>
public sealed record MkcpResult(string? Options, string? Refusal);

/// <summary>
/// mKCP из ссылки, конфига Xray или sing-box — в транспорт <c>mkcp</c> сборки extended.
/// </summary>
/// <remarks>
/// <para>
/// mKCP сборки extended — прежний mKCP Xray (до 26): всякий пакет защищён
/// либо простой проверкой («original»), либо AES-GCM от <c>seed</c>, поверх —
/// заголовок-подделка (srtp, utp, wechat-video, dtls, wireguard). Xray 26
/// перенёс то и другое в маски <c>finalmask</c>, а голый mKCP у него без
/// всякой защиты. Замер 30.10 на петле, клиент — sing-box 2.7.3, сервер —
/// Xray 26.10.29 и 26.9.30: маски <c>mkcp-legacy</c> «пустая» или с
/// <c>value</c> (seed), за ними заголовок srtp, utp, wechat, dtls, wireguard —
/// ответ за 0,10–0,16 с; голый mKCP, заголовок раньше защиты и заголовок
/// <c>dns</c> — молчание. Незнакомый заголовок сборка молча заменяет «none»
/// (<c>NewPacketHeader</c>), поэтому он здесь — отказ, а не догадка.
/// </para>
/// <para>
/// Ссылка без <c>fm</c> читается по-старому: <c>headerType</c> и <c>seed</c>.
/// Сервер Xray 26 с голым mKCP выглядит в ссылке так же — и не ответит;
/// отличить по ссылке нечем.
/// </para>
/// <para>
/// Прочие маски (<c>xdns</c>, <c>noise</c>, <c>salamander</c>…) сборка не знает.
/// «🇵🇱 Польша — XDNS» у владельца — mKCP внутри DNS-запросов через шесть
/// резолверов Яндекса; Xray 26.9.30 и 26.10.29 с его ссылкой 30.10 не получили
/// ответа ни через них, ни через DoH Cloudflare, сам адрес на DNS молчит —
/// сервер мёртв и для настоящего Xray.
/// </para>
/// </remarks>
public static class MkcpSettings
{
    /// <summary>Заголовки сборки extended (transport/v2raykcp/header.go).</summary>
    private static readonly HashSet<string> Headers = new(StringComparer.Ordinal)
    {
        "srtp", "utp", "wechat-video", "dtls", "wireguard",
    };

    private sealed record Numbers(
        long? Mtu, long? Tti, long? Uplink, long? Downlink, bool Congestion, long? ReadBuffer, long? WriteBuffer);

    /// <summary>Из ссылки vless:// или trojan://.</summary>
    public static MkcpResult FromLink(IReadOnlyDictionary<string, string> query)
    {
        string? Get(string key) =>
            query.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        long? Number(string key) => long.TryParse(Get(key), out var n) ? n : null;

        var numbers = new Numbers(
            Number("mtu"), Number("tti"), Number("uplinkCapacity"), Number("downlinkCapacity"),
            Get("congestion") is "1" or "true", Number("readBufferSize"), Number("writeBufferSize"));

        JsonNode? masks = null;
        if (Get("fm") is { } fm)
        {
            try
            {
                masks = JsonNode.Parse(fm);
            }
            catch (JsonException)
            {
                return new(null, "маскировку mKCP (fm) не разобрать");
            }
        }

        return Build(numbers, Get("headerType"), Get("seed"), masks);
    }

    /// <summary>Из vmess:// — у v2rayN заголовок в <c>type</c>, seed в <c>path</c>.</summary>
    public static MkcpResult FromVmess(string? header, string? seed) =>
        Build(new Numbers(null, null, null, null, false, null, null), header, seed, null);

    /// <summary>Из <c>streamSettings</c> конфига Xray.</summary>
    public static MkcpResult FromXray(JsonElement stream)
    {
        var kcp = Section(stream, "kcpSettings");

        var numbers = new Numbers(
            Number(kcp, "mtu"), Number(kcp, "tti"), Number(kcp, "uplinkCapacity"), Number(kcp, "downlinkCapacity"),
            Flag(kcp, "congestion"), Number(kcp, "readBufferSize"), Number(kcp, "writeBufferSize"));

        var masks = Section(stream, "finalmask") is { ValueKind: JsonValueKind.Object } section
            ? JsonNode.Parse(section.GetRawText())
            : null;

        return Build(numbers, Text(Section(kcp, "header"), "type"), Text(kcp, "seed"), masks);
    }

    /// <summary>Из секции <c>transport</c> конфига sing-box — она уже в нужных именах.</summary>
    public static MkcpResult FromSingBox(JsonElement transport)
    {
        var numbers = new Numbers(
            Number(transport, "mtu"), Number(transport, "tti"), Number(transport, "uplink_capacity"),
            Number(transport, "downlink_capacity"), Flag(transport, "congestion"),
            Number(transport, "read_buffer_size"), Number(transport, "write_buffer_size"));

        return Build(numbers, Text(transport, "header_type"), Text(transport, "seed"), null);
    }

    private static MkcpResult Build(Numbers numbers, string? header, string? seed, JsonNode? masks)
    {
        if (masks is not null)
        {
            var (maskHeader, maskSeed, refusal) = FromMasks(masks);
            if (refusal is not null)
                return new(null, refusal);

            header = maskHeader;
            seed = maskSeed;
        }

        var headerType = HeaderName(header);
        if (headerType is null)
            return new(null, $"заголовок mKCP «{header}» движок не знает");

        var options = new JsonObject();

        // Пределы — прежние Xray; вне их — значение по умолчанию. Это не
        // придирка: при tti больше 1000 сборка делит на ноль (1000 / TTI
        // в GetSendingInFlightSize), а ссылку пишет поставщик.
        Put(options, "mtu", numbers.Mtu, 576, 1460);
        Put(options, "tti", numbers.Tti, 10, 100);
        Put(options, "uplink_capacity", numbers.Uplink, 1, 10_000);
        Put(options, "downlink_capacity", numbers.Downlink, 1, 10_000);
        Put(options, "read_buffer_size", numbers.ReadBuffer, 1, 64);
        Put(options, "write_buffer_size", numbers.WriteBuffer, 1, 64);

        if (numbers.Congestion)
            options["congestion"] = true;

        if (headerType != "none")
            options["header_type"] = headerType;

        if (!string.IsNullOrEmpty(seed))
            options["seed"] = seed;

        return new(options.ToJsonString(), null);
    }

    /// <summary>
    /// Маски Xray 26 — в заголовок и seed прежнего mKCP.
    /// </summary>
    /// <remarks>
    /// Годится одна форма: защита (<c>mkcp-legacy</c> без заголовка — с <c>value</c>
    /// или без), за ней по желанию заголовок. Порядок списка у Xray — от данных
    /// наружу: последним идёт то, что видно в сети. Принимаются и объект
    /// <c>{"udp": [...]}</c>, и одна маска, и список — ссылки пишут по-разному.
    /// </remarks>
    private static (string? Header, string? Seed, string? Refusal) FromMasks(JsonNode masks)
    {
        IEnumerable<JsonNode?> items = masks switch
        {
            JsonObject o when o["udp"] is JsonArray udp => udp,
            JsonObject o when o.ContainsKey("type") => [o],
            JsonArray list => list,
            _ => [],
        };

        bool guarded = false;
        string? seed = null;
        string? header = null;

        foreach (var item in items)
        {
            var type = (item?["type"] as JsonValue)?.TryGetValue<string>(out var t) == true ? t : null;

            if (!string.Equals(type, "mkcp-legacy", StringComparison.OrdinalIgnoreCase))
                return (null, null, $"маскировку «{type ?? "?"}» движок не знает");

            var settings = item?["settings"] as JsonObject;
            var maskHeader = Value(settings, "header");
            var maskValue = Value(settings, "value");

            if (maskHeader is null)
            {
                if (guarded || header is not null)
                    return (null, null, "маски mKCP в порядке, которого движок не повторит");

                guarded = true;
                seed = maskValue;
            }
            else
            {
                if (!guarded || header is not null)
                    return (null, null, "маски mKCP в порядке, которого движок не повторит");

                header = maskHeader;
            }
        }

        if (!guarded)
            return (null, null, "mKCP без защиты (Xray 26) движок не повторит");

        return (header ?? "none", seed, null);
    }

    /// <summary>Имя заголовка Xray — в имя сборки; <c>null</c> — не знает.</summary>
    private static string? HeaderName(string? header)
    {
        var name = string.IsNullOrWhiteSpace(header) ? "none" : header.Trim().ToLowerInvariant();

        if (name == "wechat")
            name = "wechat-video";

        return name == "none" || Headers.Contains(name) ? name : null;
    }

    private static void Put(JsonObject options, string key, long? value, long min, long max)
    {
        if (value is { } n && n >= min && n <= max)
            options[key] = n;
    }

    private static string? Value(JsonObject? settings, string key) =>
        settings?[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static JsonElement Section(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var section)
            ? section
            : default;

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;

    private static long? Number(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var n)
                ? n
                : null;

    private static bool Flag(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;
}
