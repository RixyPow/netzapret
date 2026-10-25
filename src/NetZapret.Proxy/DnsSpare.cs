namespace NetZapret.Proxy;

/// <summary>
/// Запасной резолвер движка: на него уходят имена, пока выбранный не отвечает.
/// </summary>
/// <remarks>
/// <para>
/// 25.10 у владельца Comss DNS (83.220.169.155) перестал принимать соединения —
/// TCP на 443 и 853 ноль из трёх, хотя на ping отвечал, — а путь DNS стоял
/// «напрямую». Движок спрашивал только его: каждое имя ждало 11–12 с, Steam
/// открывался по 11 с на запрос, панели подписок не укладывались в свои 12 с,
/// «всё виснет». Программа об этом не говорила нигде. С Quad9 тот же движок
/// отвечал за 35–186 мс.
/// </para>
/// <para>
/// Google — потому что его DoH на 8.8.4.4 у владельца отвечает с 30.09
/// (5 из 5 за 0,07–0,10 с) и он же резолвер по умолчанию. Если выбран сам
/// Google, запасной — Cloudflare: запасной должен быть у другого хозяина,
/// иначе он ляжет вместе с выбранным.
/// </para>
/// </remarks>
public static class DnsSpare
{
    /// <summary>Запасной к резолверу <paramref name="primary"/> (адрес из настроек).</summary>
    public static DnsProvider For(string? primary) =>
        primary is "8.8.4.4" or "8.8.8.8" ? Cloudflare : Google;

    private static DnsProvider Google => DnsSurvey.Providers.First(p => p.TlsAddress == "8.8.4.4");

    private static DnsProvider Cloudflare => DnsSurvey.Providers.First(p => p.TlsAddress == "1.1.1.1");

    /// <summary>Резолвер словами для человека: «Comss.one (83.220.169.155)».</summary>
    public static string Describe(string address) =>
        DnsSurvey.ByAddress(address) is { } provider ? $"{provider.Name} ({address})" : address;
}
