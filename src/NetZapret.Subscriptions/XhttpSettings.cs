using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetZapret.Subscriptions;

/// <summary>
/// Настройки транспорта XHTTP сверх пути и Host — в именах sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Xray пишет их в верблюжьем регистре — полями <c>xhttpSettings</c> или
/// внутри <c>extra</c>, а в ссылке тем же <c>extra</c> одной строкой JSON.
/// Сборка extended ждёт те же настройки, но через подчёркивание.
/// </para>
/// <para>
/// Без них сервер не отвечает вовсе. Замер 23.09 на подписке из девяти
/// серверов XHTTP: все в <c>extra</c> требуют <c>xPaddingBytes: "1"</c>,
/// а мы писали <c>100-1000</c>, и сервер отвергал каждый запрос за 0,1 с —
/// через движок все девять молчали, а TCP и TLS до них проходили.
/// С набивкой из подписки ответили три из трёх проверенных.
/// </para>
/// <para>
/// Переводим только известные поля. Незнакомое поле sing-box не пропускает,
/// а отвергает весь конфиг — и лёг бы туннель целиком, а не один сервер.
/// </para>
/// </remarks>
public static class XhttpSettings
{
    private enum Kind
    {
        /// <summary>Диапазон: «1», «2-4». Число оставляем числом.</summary>
        Range,
        Number,
        Flag,
        Text,
    }

    private static readonly (string Xray, string SingBox, Kind Kind)[] Fields =
    [
        ("xPaddingBytes", "x_padding_bytes", Kind.Range),
        ("noGRPCHeader", "no_grpc_header", Kind.Flag),
        ("noSSEHeader", "no_sse_header", Kind.Flag),
        ("scMaxEachPostBytes", "sc_max_each_post_bytes", Kind.Range),
        ("scMinPostsIntervalMs", "sc_min_posts_interval_ms", Kind.Range),
        ("scMaxBufferedPosts", "sc_max_buffered_posts", Kind.Number),
        ("scStreamUpServerSecs", "sc_stream_up_server_secs", Kind.Range),
        ("xPaddingObfsMode", "x_padding_obfs_mode", Kind.Flag),
        ("xPaddingKey", "x_padding_key", Kind.Text),
        ("xPaddingHeader", "x_padding_header", Kind.Text),
        ("xPaddingPlacement", "x_padding_placement", Kind.Text),
        ("xPaddingMethod", "x_padding_method", Kind.Text),
        ("uplinkHTTPMethod", "uplink_http_method", Kind.Text),
        ("sessionPlacement", "session_placement", Kind.Text),
        ("sessionKey", "session_key", Kind.Text),
        ("seqPlacement", "seq_placement", Kind.Text),
        ("seqKey", "seq_key", Kind.Text),
        ("uplinkDataPlacement", "uplink_data_placement", Kind.Text),
        ("uplinkDataKey", "uplink_data_key", Kind.Text),
        ("uplinkChunkSize", "uplink_chunk_size", Kind.Range),
    ];

    private static readonly (string Xray, string SingBox, Kind Kind)[] XmuxFields =
    [
        ("maxConcurrency", "max_concurrency", Kind.Range),
        ("maxConnections", "max_connections", Kind.Range),
        ("cMaxReuseTimes", "c_max_reuse_times", Kind.Range),
        ("hMaxRequestTimes", "h_max_request_times", Kind.Range),
        ("hMaxReusableSecs", "h_max_reusable_secs", Kind.Range),
        ("hKeepAlivePeriod", "h_keep_alive_period", Kind.Number),
    ];

    /// <summary>Режимы, которые сборка extended принимает.</summary>
    /// <remarks>Прочее она отвергает вместе с конфигом.</remarks>
    public static string? Mode(string? mode) =>
        mode is "auto" or "packet-up" or "stream-up" or "stream-one" ? mode : null;

    /// <summary>
    /// Из <c>extra</c> ссылки — строки JSON в именах Xray.
    /// </summary>
    public static string? FromXrayText(string? extra)
    {
        if (string.IsNullOrWhiteSpace(extra))
            return null;

        try
        {
            using var document = JsonDocument.Parse(extra);
            return FromXray(document.RootElement);
        }
        catch (JsonException)
        {
            // Испорченный extra — не повод терять сервер: с набивкой
            // по умолчанию он хотя бы попробует.
            return null;
        }
    }

    /// <summary>
    /// Из секции <c>xhttpSettings</c> конфига Xray: поля лежат и в ней самой,
    /// и во вложенном <c>extra</c>, который важнее.
    /// </summary>
    public static string? FromXraySection(JsonElement section)
    {
        if (section.ValueKind != JsonValueKind.Object)
            return null;

        var result = Translate(section, Fields, XmuxFields, xray: true) ?? new JsonObject();

        if (section.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Object
            && Translate(extra, Fields, XmuxFields, xray: true) is { } inner)
        {
            foreach (var (key, value) in inner)
                result[key] = value?.DeepClone();
        }

        return result.Count == 0 ? null : result.ToJsonString();
    }

    /// <summary>Из <c>extra</c>, уже разобранного.</summary>
    public static string? FromXray(JsonElement extra) =>
        Translate(extra, Fields, XmuxFields, xray: true)?.ToJsonString();

    /// <summary>
    /// Из транспорта sing-box: имена те же, но отсев тот же — поле, которого
    /// не знает наша сборка, уронило бы конфиг.
    /// </summary>
    public static string? FromSingBox(JsonElement transport) =>
        Translate(transport, Fields, XmuxFields, xray: false)?.ToJsonString();

    private static JsonObject? Translate(
        JsonElement source,
        (string Xray, string SingBox, Kind Kind)[] fields,
        (string Xray, string SingBox, Kind Kind)[] xmuxFields,
        bool xray)
    {
        if (source.ValueKind != JsonValueKind.Object)
            return null;

        var result = new JsonObject();

        foreach (var (xrayName, singName, kind) in fields)
        {
            if (source.TryGetProperty(xray ? xrayName : singName, out var value)
                && Convert(value, kind) is { } converted)
            {
                result[singName] = converted;
            }
        }

        // Набивку выключить нельзя: движок на нуле отвергает конфиг.
        // Пусть лучше останется своя по умолчанию.
        if (result["x_padding_bytes"] is JsonValue padding && IsZero(padding))
            result.Remove("x_padding_bytes");

        if (source.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
        {
            var copy = new JsonObject();

            foreach (var header in headers.EnumerateObject())
            {
                // Host заголовком движок не принимает: для него есть своё поле.
                if (header.Value.ValueKind == JsonValueKind.String
                    && !header.Name.Equals("host", StringComparison.OrdinalIgnoreCase))
                {
                    copy[header.Name] = header.Value.GetString();
                }
            }

            if (copy.Count > 0)
                result["headers"] = copy;
        }

        if (source.TryGetProperty("xmux", out var xmux)
            && Translate(xmux, xmuxFields, [], xray) is { Count: > 0 } mux)
        {
            result["xmux"] = mux;
        }

        return result.Count == 0 ? null : result;
    }

    private static JsonNode? Convert(JsonElement value, Kind kind) => (kind, value.ValueKind) switch
    {
        (Kind.Range, JsonValueKind.Number) => JsonValue.Create(value.GetInt64()),
        (Kind.Range, JsonValueKind.String) when value.GetString() is { Length: > 0 } text => JsonValue.Create(text),

        // Xray пишет диапазон и объектом.
        (Kind.Range, JsonValueKind.Object)
            when value.TryGetProperty("from", out var from) && value.TryGetProperty("to", out var to)
                && from.ValueKind == JsonValueKind.Number && to.ValueKind == JsonValueKind.Number
            => JsonValue.Create($"{from.GetInt64()}-{to.GetInt64()}"),

        (Kind.Number, JsonValueKind.Number) => JsonValue.Create(value.GetInt64()),
        (Kind.Number, JsonValueKind.String) when long.TryParse(value.GetString(), out var number) => JsonValue.Create(number),
        (Kind.Flag, JsonValueKind.True) => JsonValue.Create(true),
        (Kind.Flag, JsonValueKind.False) => JsonValue.Create(false),
        (Kind.Text, JsonValueKind.String) when value.GetString() is { Length: > 0 } text => JsonValue.Create(text),
        _ => null,
    };

    private static bool IsZero(JsonValue value) =>
        value.TryGetValue<long>(out var number) ? number <= 0
        : value.TryGetValue<string>(out var text) && (text.Trim() is "0" or "0-0" || text.TrimStart().StartsWith('-'));
}
