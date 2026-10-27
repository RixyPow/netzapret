using System.Text;

namespace NetZapret.Subscriptions;

/// <summary>
/// Серверы подписок из запаса — словами, без ключей: для отчёта.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт DxHell 27.10: «в приложении всё красное, в Happ всё в порядке».
/// «Наблюдение» показало, что Happ ходит в основном на 103.24.207.40–44,
/// а наш движок и замер не подключались к ним ни разу. Нет ли их в нашей
/// подписке, или они в ней есть и отброшены, — из отчёта было не сказать:
/// он знал только серверы, которые уже проверялись.
/// </para>
/// <para>
/// Пишется имя, описание от панели, протокол, защита и транспорт, адрес
/// и порт, а у отброшенного — причина. Ключ, UUID, пароль, открытый ключ
/// REALITY и путь не пишутся: по ним к серверу подключился бы кто угодно.
/// Адрес не секрет — его видит провайдер и пишет журнал движка.
/// </para>
/// </remarks>
public static class SubscriptionDigest
{
    /// <summary>По подписке на ссылку из книги, по порядку; запаса нет — так и сказано.</summary>
    public static string Describe(IReadOnlyList<string> urls, string? reserveDirectory = null)
    {
        var text = new StringBuilder();

        text.AppendLine("Серверы подписок — из запаса, то есть из последнего ответа панели; ключей здесь нет");

        if (urls.Count == 0)
            text.AppendLine("  подписок нет");

        for (int i = 0; i < urls.Count; i++)
        {
            text.AppendLine();

            if (SubscriptionPool.Kept(urls[i], reserveDirectory) is not { } kept)
            {
                text.AppendLine($"Подписка {i + 1}: запаса нет — панель ни разу не ответила или подписку только добавили");
                continue;
            }

            var servers = kept.Info.Servers;
            var title = string.IsNullOrWhiteSpace(kept.Info.Title) ? string.Empty : $" «{kept.Info.Title.Trim()}»";

            text.AppendLine($"Подписка {i + 1}{title}: запас от {kept.At.ToLocalTime():dd.MM HH:mm}, "
                + $"серверов {servers.Count}, годных {servers.Count(s => s.IsUsableOutbound)}");

            foreach (var server in servers)
                text.AppendLine("  " + Line(server));
        }

        return text.ToString();
    }

    /// <summary>Строка сервера: имя, описание, протокол, адрес — и причина, если отброшен.</summary>
    public static string Line(ProxyServer server)
    {
        var parts = new List<string> { server.Tag };

        if (!string.IsNullOrWhiteSpace(server.Description))
            parts.Add(server.Description.Trim());

        parts.Add(Kind(server));
        parts.Add($"{server.Host}:{server.Port}");

        if (server.Unusable is { } why)
            parts.Add("отброшен: " + why);

        return string.Join(" · ", parts);
    }

    private static string Kind(ProxyServer server)
    {
        var words = new List<string> { server.ProtocolName };

        if (!string.IsNullOrWhiteSpace(server.Security) && !server.Security.Equals("none", StringComparison.OrdinalIgnoreCase))
            words.Add(server.Security.ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(server.Transport))
            words.Add(server.Transport.ToLowerInvariant());

        if (!string.IsNullOrWhiteSpace(server.Flow))
            words.Add(server.Flow);

        if (!string.IsNullOrWhiteSpace(server.Sni))
            words.Add("sni " + server.Sni);

        return string.Join(' ', words);
    }
}
