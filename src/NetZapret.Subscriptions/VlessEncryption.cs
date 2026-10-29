namespace NetZapret.Subscriptions;

/// <summary>
/// Шифрование VLESS (<c>encryption</c>, «VLESS Encryption» из Xray) так, как его понимает sing-box.
/// </summary>
/// <remarks>
/// <para>
/// До 29.10 параметр не читался вовсе: сервер, который его требует, получал
/// VLESS без шифрования и молча считался мёртвым. Найдено по вики zapret.moe
/// (xray/vless-encryption, xray/vless-stack-map).
/// </para>
/// <para>
/// Наш sing-box 1.14.3-extended-2.7.3 его знает — замер 29.10 на loopback: вход
/// VLESS с <c>decryption</c> и выход с <c>encryption</c> из одного движка, ключи
/// x25519; режимы native, xorpub и random, 0rtt и 1rtt — ответ проходит; чужой
/// ключ — «chacha20poly1305: message authentication failed»; сервер с шифрованием
/// и клиент без него — то же, обрыв. С сервером Xray не проверено.
/// </para>
/// <para>
/// Разбирает движок строже Xray, и любая непонятая строка роняет весь конфиг
/// со всеми серверами («initialize outbound[N]: parse encryption: …»). Что он
/// принимает, сверено <c>sing-box check</c> 29.10: префикс
/// <c>mlkem768x25519plus</c> строчными; режим native, xorpub или random;
/// 0rtt или 1rtt; дальше только ключи — base64url без «=», 32 байта (x25519)
/// или 1184 (открытый ключ ML-KEM-768, каждый 12-битный коэффициент меньше
/// 3329 — иначе «mlkem: invalid polynomial encoding»), ключей может быть
/// несколько. Наполнение Xray между rtt и ключами («100-111-1111.75-0-111»)
/// движок принимает за ключ и падает; обычный base64 с «+/» — тоже. Такой
/// сервер пропускается (<see cref="ProxyServer.IsSupportedBySingBox"/>), а не
/// роняет туннель.
/// </para>
/// </remarks>
public static class VlessEncryption
{
    private const string Prefix = "mlkem768x25519plus";

    /// <summary>
    /// Значение для sing-box: пустая строка — без шифрования; <c>null</c> — движок такого не разберёт.
    /// </summary>
    public static string? ForSingBox(string? encryption)
    {
        if (string.IsNullOrWhiteSpace(encryption) || encryption.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var value = encryption.Trim();
        var parts = value.Split('.');

        if (parts.Length < 4
            || parts[0] != Prefix
            || parts[1] is not ("native" or "xorpub" or "random")
            || parts[2] is not ("0rtt" or "1rtt"))
        {
            return null;
        }

        return parts.Skip(3).All(IsKey) ? value : null;
    }

    private static bool IsKey(string text)
    {
        if (text.Length == 0 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            return false;

        byte[] bytes;

        try
        {
            bytes = Convert.FromBase64String(Padded(text.Replace('-', '+').Replace('_', '/')));
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length == 32 || bytes.Length == 1184 && IsMlKemKey(bytes);
    }

    private static string Padded(string text) => (text.Length % 4) switch
    {
        2 => text + "==",
        3 => text + "=",
        _ => text,
    };

    /// <summary>Открытый ключ ML-KEM-768: 768 коэффициентов по 12 бит, каждый меньше q = 3329.</summary>
    private static bool IsMlKemKey(byte[] key)
    {
        for (int i = 0; i < 1152; i += 3)
        {
            int first = key[i] | ((key[i + 1] & 0x0F) << 8);
            int second = (key[i + 1] >> 4) | (key[i + 2] << 4);

            if (first >= 3329 || second >= 3329)
                return false;
        }

        return true;
    }
}
