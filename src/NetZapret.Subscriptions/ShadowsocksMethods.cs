namespace NetZapret.Subscriptions;

/// <summary>
/// Шифр Shadowsocks так, как его понимает sing-box.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №38 (24.10): туннель не поднимался вовсе — «create service:
/// initialize outbound[260]: unknown method: chacha20-poly1305». Подписка
/// записала шифр так, как его пишут Xray и Clash, а sing-box знает его только
/// под полным именем, и один такой сервер из сотен ронял весь конфиг.
/// </para>
/// <para>
/// Что sing-box принимает, проверено самим `sing-box check` на нашей сборке
/// 1.14.3-extended-2.7.3 (25.10): <c>chacha20-poly1305</c>,
/// <c>xchacha20-poly1305</c>, <c>plain</c>, <c>chacha20</c> и имена в верхнем
/// регистре — «unknown method»; полные имена, AEAD, 2022 и старые потоковые
/// шифры — принимает. Короткие имена переводятся в полные, незнакомое —
/// не годится (<see cref="ProxyServer.IsSupportedBySingBox"/>): такой сервер
/// пропускается, а не роняет туннель.
/// </para>
/// </remarks>
public static class ShadowsocksMethods
{
    /// <summary>Шифр, когда подписка его не назвала, — как прежде.</summary>
    public const string Default = "aes-256-gcm";

    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "none",
        "aes-128-gcm", "aes-192-gcm", "aes-256-gcm",
        "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "aes-128-ctr", "aes-192-ctr", "aes-256-ctr",
        "aes-128-cfb", "aes-192-cfb", "aes-256-cfb",
        "rc4-md5", "chacha20-ietf", "xchacha20",
    };

    /// <summary>Как тот же шифр называют Xray и Clash.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["chacha20-poly1305"] = "chacha20-ietf-poly1305",
        ["xchacha20-poly1305"] = "xchacha20-ietf-poly1305",
        ["plain"] = "none",
    };

    /// <summary>Имя шифра для sing-box; <c>null</c> — sing-box такого не знает.</summary>
    public static string? ForSingBox(string? method)
    {
        if (string.IsNullOrWhiteSpace(method))
            return Default;

        var name = method.Trim().ToLowerInvariant();

        if (Aliases.TryGetValue(name, out var full))
            return full;

        return Known.Contains(name) ? name : null;
    }
}
