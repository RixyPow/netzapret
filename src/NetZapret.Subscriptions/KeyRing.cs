namespace NetZapret.Subscriptions;

/// <summary>Отдельный ключ и сервер, который из него разобран.</summary>
/// <param name="Key">Сама строка ключа — пароль: не печатается и не пишется в журнал.</param>
public sealed record KeyServer(string Key, ProxyServer Server);

/// <summary>
/// Отдельные ключи — серверы, выданные не подпиской, а строкой: vless://…
/// </summary>
/// <remarks>
/// <para>
/// Заведено для 0.9.0 (владелец, 28.09): бот Zapret KVN и многие продавцы
/// раздают не ссылку на подписку, а ключ, и вкладка VPN такие отбивала:
/// «нужна http, https либо обёртка». Разбирать их программа умела всегда —
/// подписки из них и состоят; не было входа.
/// </para>
/// <para>
/// Ключи — ещё один источник пула рядом с подписками (<see cref="SubscriptionPool"/>),
/// под именем <see cref="Name"/>: метки пула и двойники считаются так же.
/// </para>
/// </remarks>
public static class KeyRing
{
    /// <summary>Имя источника в пуле и папки на вкладке VPN.</summary>
    public const string Name = "Отдельные ключи";

    /// <summary>Схемы, которые разбирает <see cref="ProxyUriParser"/>.</summary>
    private static readonly string[] Schemes = ["vless://", "trojan://", "hysteria2://", "hy2://", "vmess://", "ss://"];

    /// <summary>Похоже ли на ключ, а не на ссылку подписки.</summary>
    public static bool IsKey(string text) =>
        Schemes.Any(s => text.TrimStart().StartsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Ключи из вставленного текста: по одному на строку или через пробел.
    /// </summary>
    /// <remarks>
    /// Вставляют часто пачкой — бот выдаёт несколько, клиент копирует список.
    /// Всё, что не ключ, отбрасывается: разделители, подписи, пустые строки.
    /// </remarks>
    /// <remarks>
    /// Режется и по началу следующего ключа, а не только по пробелам: поле
    /// ввода — пароль, и при вставке пачки оно выбрасывает переносы строк,
    /// склеивая ключи в один. Схема ищется только отдельно стоящей: «ss://»
    /// сидит внутри «vless://», и по ней ключ VLESS разрезался бы надвое.
    /// </remarks>
    public static IReadOnlyList<string> Split(string text) =>
        text.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(chunk => System.Text.RegularExpressions.Regex.Split(
                chunk, @"(?<![A-Za-z0-9])(?=(?:vless|trojan|hysteria2|hy2|vmess|ss)://)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            .Select(k => k.Trim())
            .Where(IsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Разбирает ключи по одному, чтобы у каждого сервера остался его ключ:
    /// по нему ключ и убирают.
    /// </summary>
    public static (IReadOnlyList<KeyServer> Servers, IReadOnlyList<string> Errors) Parse(IEnumerable<string> keys)
    {
        var servers = new List<KeyServer>();
        var errors = new List<string>();

        foreach (var key in keys)
        {
            if (ProxyUriParser.TryParse(key.Trim(), out var server, out var error) && server is not null)
                servers.Add(new KeyServer(key, server));
            else
                errors.Add(error ?? "ключ не разбирается");
        }

        return (servers, errors);
    }
}
