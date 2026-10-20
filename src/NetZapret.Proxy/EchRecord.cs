using System.Text;

namespace NetZapret.Proxy;

/// <summary>
/// Запись HTTPS имени с конфигурацией ECH: какое имя браузер покажет
/// провайдеру вместо настоящего.
/// </summary>
/// <remarks>
/// <para>
/// С ECH (Encrypted Client Hello) настоящее имя в приветствии зашифровано,
/// а открытым стоит общее имя из конфигурации — у Cloudflare это
/// <c>cloudflare-ech.com</c>. Провайдер режет по открытому имени, и сайт,
/// закрытый для пробы с настоящим именем, в браузере с ECH открывается, —
/// а рецепт десинка ему нужен не на своё имя, а на общее (ntc.party,
/// zapret-Checker, №88–98: lostfilm.tv открылся, когда в список добавили
/// cloudflare-ech.com). Проба ECH не шлёт, и её вердикт о браузере
/// с включённым DoH не говорил ничего.
/// </para>
/// <para>
/// Конфигурация — поле <c>ech=</c> записи HTTPS (RFC 9460), base64
/// списка ECHConfig (draft-ietf-tls-esni): у версии <c>0xfe0d</c> общее имя
/// стоит за ключом HPKE и наборами шифров. Разбирается только оно.
/// </para>
/// </remarks>
public static class EchRecord
{
    /// <summary>Версия ECHConfig, которую умеют браузеры и Cloudflare (draft-13 и далее).</summary>
    private const ushort Version = 0xfe0d;

    /// <summary>
    /// Общее имя из записи HTTPS имени; <c>null</c> — записи нет, ECH в ней
    /// нет или DoH не ответил.
    /// </summary>
    /// <param name="timeout">
    /// На весь вопрос, а не на каждый резолвер: резолверов четыре, и закрытому
    /// имени проверка и так платит секунды — ещё по четыре на каждый молчащий
    /// DoH она не должна.
    /// </param>
    public static async Task<string?> PublicNameAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);

            using var http = new HttpClient { Timeout = timeout };

            var record = await DohResolver.HttpsRecordAsync(host, http, limit.Token);

            return record is null ? null : PublicName(record);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Общее имя из текста записи HTTPS: «1 . alpn=h3,h2 ech=AEX+… ipv4hint=…».
    /// </summary>
    public static string? PublicName(string presentation)
    {
        foreach (var field in presentation.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!field.StartsWith("ech=", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = field[4..].Trim('"');

            try
            {
                return PublicNameOf(Convert.FromBase64String(text));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Общее имя первой знакомой конфигурации из списка ECHConfig; <c>null</c> — нет такой.
    /// </summary>
    internal static string? PublicNameOf(byte[] list)
    {
        // ECHConfigList: длина (2), затем конфигурации подряд —
        // version (2), length (2), contents.
        if (list.Length < 2)
            return null;

        int end = Math.Min(list.Length, 2 + ((list[0] << 8) | list[1]));
        int pos = 2;

        while (pos + 4 <= end)
        {
            int version = (list[pos] << 8) | list[pos + 1];
            int length = (list[pos + 2] << 8) | list[pos + 3];
            int body = pos + 4;

            if (body + length > end)
                return null;

            if (version == Version && NameOf(list, body, body + length) is { } name)
                return name;

            pos = body + length;
        }

        return null;
    }

    /// <summary>
    /// Общее имя из contents конфигурации версии <see cref="Version"/>:
    /// config_id (1), kem_id (2), public_key (2+n), cipher_suites (2+n),
    /// maximum_name_length (1), public_name (1+n), extensions (2+n).
    /// </summary>
    private static string? NameOf(byte[] list, int pos, int end)
    {
        pos += 1 + 2;

        if (!Skip16(list, ref pos, end) || !Skip16(list, ref pos, end))
            return null;

        pos += 1;

        if (pos >= end)
            return null;

        int nameLength = list[pos];
        pos += 1;

        if (nameLength == 0 || pos + nameLength > end)
            return null;

        var name = Encoding.ASCII.GetString(list, pos, nameLength);

        return name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? name : null;
    }

    /// <summary>Пропускает поле с двухбайтовой длиной; <c>false</c> — вышли за край.</summary>
    private static bool Skip16(byte[] list, ref int pos, int end)
    {
        if (pos + 2 > end)
            return false;

        int length = (list[pos] << 8) | list[pos + 1];
        pos += 2 + length;

        return pos <= end;
    }
}
