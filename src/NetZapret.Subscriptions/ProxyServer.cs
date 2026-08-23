namespace NetZapret.Subscriptions;

/// <summary>Протокол прокси-сервера.</summary>
public enum ProxyProtocol
{
    Vless,
    Vmess,
    Trojan,
    Shadowsocks,
    Hysteria2,
}

/// <summary>
/// Один сервер из подписки, приведённый к общему виду.
/// </summary>
/// <remarks>
/// Намеренно не повторяет структуру исходной ссылки: у vmess это base64 от JSON,
/// у vless — query-параметры, у ss пароль зашит в часть до <c>@</c>. Разбор
/// приводит их к одной модели, из которой генератор собирает outbound.
/// </remarks>
public sealed record ProxyServer
{
    public required ProxyProtocol Protocol { get; init; }

    /// <summary>Имя из фрагмента ссылки; используется как тег outbound'а.</summary>
    public required string Tag { get; init; }

    public required string Host { get; init; }

    public required ushort Port { get; init; }

    /// <summary>UUID для vless/vmess, пароль для trojan/ss/hysteria2.</summary>
    public required string Credential { get; init; }

    /// <summary>tcp, ws, grpc, http, httpupgrade, xhttp, quic.</summary>
    public string Transport { get; init; } = "tcp";

    /// <summary>none, tls, reality.</summary>
    public string Security { get; init; } = "none";

    public string? Sni { get; init; }

    /// <summary>Отпечаток uTLS: chrome, firefox, safari и т.п.</summary>
    public string? Fingerprint { get; init; }

    public IReadOnlyList<string> Alpn { get; init; } = Array.Empty<string>();

    /// <summary>xtls-rprx-vision для vless.</summary>
    public string? Flow { get; init; }

    /// <summary>Публичный ключ REALITY (<c>pbk</c>).</summary>
    public string? RealityPublicKey { get; init; }

    /// <summary>Короткий идентификатор REALITY (<c>sid</c>).</summary>
    public string? RealityShortId { get; init; }

    /// <summary>Путь для ws / xhttp / httpupgrade.</summary>
    public string? Path { get; init; }

    /// <summary>Заголовок Host для ws и подобных.</summary>
    public string? HostHeader { get; init; }

    /// <summary>Имя сервиса для grpc.</summary>
    public string? ServiceName { get; init; }

    /// <summary>Метод шифрования Shadowsocks.</summary>
    public string? Method { get; init; }

    /// <summary>Альтернативный идентификатор vmess.</summary>
    public int AlterId { get; init; }

    public bool AllowInsecure { get; init; }

    /// <summary>Остальные параметры ссылки — чтобы ничего не терять при разборе.</summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Движок, способный обслужить этот сервер.</summary>
    /// <remarks>
    /// sing-box не поддерживает транспорт xhttp — это транспорт Xray.
    /// Всё остальное, включая Hysteria2, которого нет в Xray, он умеет.
    /// Подробнее — docs/singbox.md.
    /// </remarks>
    public bool IsSupportedBySingBox =>
        !string.Equals(Transport, "xhttp", StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{Protocol.ToString().ToLowerInvariant()} {Host}:{Port} [{Transport}/{Security}] {Tag}";
}
