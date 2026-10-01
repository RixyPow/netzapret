using System.Text.Json;

namespace NetZapret.Subscriptions;

/// <summary>
/// Читает сервер из исходящего соединения в формате sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Issue #3, 23.09: подписка ltvpn в INCY показывает сто тридцать пять
/// серверов, у нас — ноль, при том что срок и лимит из заголовков пришли.
/// Мы представляемся панели движком — <c>sing-box/1.14.0</c>, см.
/// <see cref="SubscriptionClient"/>, — и панель в ответ отдаёт готовый
/// конфиг sing-box. Разбор JSON понимал только Xray: там сервер лежит
/// в <c>protocol</c> и <c>settings</c>, здесь — в <c>type</c>,
/// <c>server</c> и <c>server_port</c>. Каждое исходящее молча
/// пропускалось, как «direct» или «block».
/// </para>
/// <para>
/// Отличаются исходящие по полю <c>type</c>: у Xray его нет, у sing-box
/// оно обязательно.
/// </para>
/// </remarks>
internal static class SingBoxOutboundReader
{
    /// <summary>Исходящее ли это sing-box, а не Xray.</summary>
    public static bool Is(JsonElement outbound) =>
        outbound.ValueKind == JsonValueKind.Object
        && outbound.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String;

    public static bool TryRead(JsonElement outbound, out ProxyServer? server, out string? problem)
    {
        server = null;
        problem = null;

        var type = Text(outbound, "type")!.ToLowerInvariant();

        // Служебные: напрямую, в никуда, DNS и группы выбора. Группы у нас
        // свои — автоподбор и селектор собирает генератор, — и чужие поверх
        // них были бы вторым источником правды.
        if (type is "direct" or "block" or "dns" or "selector" or "urltest")
            return false;

        var kind = type switch
        {
            "vless" => ProxyProtocol.Vless,
            "vmess" => ProxyProtocol.Vmess,
            "trojan" => ProxyProtocol.Trojan,
            "shadowsocks" => ProxyProtocol.Shadowsocks,
            "hysteria2" => ProxyProtocol.Hysteria2,
            "tuic" => ProxyProtocol.Tuic,
            "hysteria" => ProxyProtocol.Hysteria,
            "anytls" => ProxyProtocol.AnyTls,
            _ => (ProxyProtocol?)null,
        };

        var tag = Empty(Text(outbound, "tag"));

        if (kind is null)
        {
            problem = $"outbound «{tag ?? type}»: протокол {type} не поддерживается";
            return false;
        }

        var host = Empty(Text(outbound, "server"));
        var port = Port(outbound);

        // У Hysteria первой версии ключ — auth_str, и он бывает пустым:
        // сервер без авторизации законен.
        var credential = kind is ProxyProtocol.Vless or ProxyProtocol.Vmess or ProxyProtocol.Tuic
            ? Empty(Text(outbound, "uuid"))
            : kind == ProxyProtocol.Hysteria
                ? Text(outbound, "auth_str") ?? string.Empty
                : Empty(Text(outbound, "password"));

        if (host is null || port == 0 || credential is null)
        {
            problem = $"outbound «{tag ?? type}»: не хватает адреса, порта или ключа";
            return false;
        }

        var tls = Section(outbound, "tls");
        var reality = Section(tls, "reality");
        var utls = Section(tls, "utls");

        // Hysteria2 без TLS не бывает: он встроен в протокол, и в конфиге
        // секция может стоять без enabled. Так же его помечает и разбор ссылок.
        // То же у TUIC, Hysteria первой версии и AnyTLS.
        bool builtInTls = kind is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic
            or ProxyProtocol.Hysteria or ProxyProtocol.AnyTls;

        var security = Flag(reality, "enabled") ? "reality"
            : Flag(tls, "enabled") || builtInTls ? "tls"
            : "none";

        var transportSection = Section(outbound, "transport");
        var transport = kind is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic or ProxyProtocol.Hysteria
            ? "udp"
            : Empty(Text(transportSection, "type")) ?? "tcp";

        // У Hysteria2 obfs — объект с типом и паролем, у первой версии — строка-пароль.
        var obfs = Section(outbound, "obfs");
        var legacyObfs = kind == ProxyProtocol.Hysteria ? Empty(Text(outbound, "obfs")) : null;

        server = new ProxyServer
        {
            Protocol = kind.Value,
            Tag = tag ?? $"{type} {host}",
            Host = host,
            Port = port,
            Credential = credential,
            Transport = transport,
            Security = security,
            Sni = Empty(Text(tls, "server_name")),
            Fingerprint = Flag(utls, "enabled") ? Empty(Text(utls, "fingerprint")) : null,
            Alpn = Strings(tls, "alpn"),
            AllowInsecure = Flag(tls, "insecure"),
            Flow = Empty(Text(outbound, "flow")),
            RealityPublicKey = Empty(Text(reality, "public_key")),
            RealityShortId = Empty(Text(reality, "short_id")),
            Path = Empty(Text(transportSection, "path")),
            HostHeader = HostHeader(transportSection),
            ServiceName = Empty(Text(transportSection, "service_name")),
            XhttpMode = transport == "xhttp" ? XhttpSettings.Mode(Empty(Text(transportSection, "mode"))) : null,
            XhttpOptions = transport == "xhttp" ? XhttpSettings.FromSingBox(transportSection) : null,

            // У vmess поле security — шифр, у shadowsocks шифр зовётся method.
            Method = kind == ProxyProtocol.Shadowsocks
                ? Empty(Text(outbound, "method"))
                : kind == ProxyProtocol.Vmess ? Empty(Text(outbound, "security")) : null,
            AlterId = Number(outbound, "alter_id"),
            ObfsType = Empty(Text(obfs, "type")),
            ObfsPassword = legacyObfs ?? Empty(Text(obfs, "password")),
            Password = kind == ProxyProtocol.Tuic ? Empty(Text(outbound, "password")) : null,
            CongestionControl = kind == ProxyProtocol.Tuic ? Empty(Text(outbound, "congestion_control")) : null,
            UdpRelayMode = kind == ProxyProtocol.Tuic ? Empty(Text(outbound, "udp_relay_mode")) : null,

            // Запасные — те же, что у разбора ссылок: без скорости движок
            // не соберёт исходящий (см. ProxyUriParser).
            UpMbps = kind == ProxyProtocol.Hysteria ? Positive(Number(outbound, "up_mbps"), 10) : 0,
            DownMbps = kind == ProxyProtocol.Hysteria ? Positive(Number(outbound, "down_mbps"), 50) : 0,
        };

        return true;
    }

    /// <summary>
    /// Заголовок Host: у ws он в headers, у http и httpupgrade — полем host,
    /// у http вдобавок списком.
    /// </summary>
    private static string? HostHeader(JsonElement transport)
    {
        var headers = Section(transport, "headers");

        if (Empty(Text(headers, "Host")) is { } header)
            return header;

        if (Empty(Text(transport, "host")) is { } single)
            return single;

        return Strings(transport, "host").FirstOrDefault();
    }

    private static JsonElement Section(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var section)
            ? section
            : default;

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static bool Flag(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.True;

    private static int Number(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : 0;

    private static int Positive(int value, int fallback) => value > 0 ? value : fallback;

    private static IReadOnlyList<string> Strings(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value))
            return [];

        if (value.ValueKind == JsonValueKind.String)
            return Empty(value.GetString()) is { } one ? [one] : [];

        if (value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString() ?? string.Empty)
            .Where(v => v.Length > 0)
            .ToList();
    }

    /// <summary>Порт бывает и числом, и строкой — поставщики пишут по-разному.</summary>
    private static ushort Port(JsonElement parent)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty("server_port", out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetUInt16(out var number) => number,
            JsonValueKind.String when ushort.TryParse(value.GetString(), out var text) => text,
            _ => 0,
        };
    }

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
