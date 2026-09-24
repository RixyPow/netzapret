using System.Net;

namespace NetZapret.Subscriptions;

/// <summary>
/// Имена панелей подписок — чтобы разрешать их в обход туннеля.
/// </summary>
/// <remarks>
/// <para>
/// При DNS через туннель адрес панели спрашивался через туннель, и когда
/// выход лежал, подписка не читалась с «Этот хост неизвестен» (жалоба
/// пользователя 24.09). Замкнутый круг: за новыми серверами идут ровно
/// тогда, когда старые мертвы, а мёртвый выход не даёт узнать адрес панели.
/// Имена самих серверов этот круг уже обходили через bootstrap; панели
/// в нём не было.
/// </para>
/// <para>
/// Отдаётся только имя узла. Ссылка целиком равносильна паролю — ключ
/// в её пути, — а имя панели одно на тысячи подписчиков провайдера и ключом
/// не является. В журнал не пишется и оно.
/// </para>
/// </remarks>
public static class SubscriptionHosts
{
    /// <summary>Имена узлов из ссылок: без повторов, без голых адресов и битых ссылок.</summary>
    public static IReadOnlyList<string> From(IEnumerable<string?> urls)
    {
        var hosts = new List<string>();

        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)
                || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            // Голый адрес разрешать не нужно: DNS его не касается вовсе.
            if (uri.HostNameType != UriHostNameType.Dns || IPAddress.TryParse(uri.Host, out _))
                continue;

            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();

            if (host.Length > 0 && !hosts.Contains(host))
                hosts.Add(host);
        }

        return hosts;
    }
}
