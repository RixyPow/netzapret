using NetZapret.Proxy;
using NetZapret.Subscriptions;

namespace NetZapret.Mobile.Data;

/// <summary>Сервер в списке вкладки VPN — без ключей и адресов, только то, что видно человеку.</summary>
/// <param name="Tag">Тег пула — по нему движок выбирает и меряет сервер.</param>
internal sealed record ServerRow(string Tag, string Name, string Country, string Protocol);

/// <summary>Папка вкладки VPN: подписка либо «Отдельные ключи».</summary>
/// <param name="Url">Ссылка-пароль; у папки ключей — <c>null</c>.</param>
internal sealed record ServerFolder(
    string Name,
    string? Url,
    bool Working,
    bool IsKeys,
    IReadOnlyList<ServerRow> Servers,
    string Detail,
    string? Warning);

/// <summary>
/// Подписки и их серверы для вкладки VPN — из запаса, без сети.
/// </summary>
/// <remarks>
/// <para>
/// Заход на вкладку панели не спрашивает: запас прошлого чтения
/// (<see cref="SubscriptionPool.Kept"/>) показывается сразу, а за свежим
/// человек идёт кнопкой «Обновить». На телефоне это ещё и трафик
/// мобильной сети, и заряд.
/// </para>
/// <para>
/// Теги — ровно те, что получит движок: <see cref="SubscriptionPool.Tag"/> по
/// подпискам в работе в порядке книги и «Отдельным ключам» последними
/// (ui.md §5 п. 9). Иначе «Выбрать» поставило бы в настройки тег, которого
/// в конфиге нет.
/// </para>
/// </remarks>
internal static class VpnData
{
    public static IReadOnlyList<ServerFolder> Read(ISubscriptionStore store)
    {
        var entries = store.Entries();
        var keys = store.Keys();
        bool keysWorking = store.KeysInPool();

        var kept = entries.Select(e => SafeKept(e.Url)).ToList();
        var keyServers = keys.Count == 0 ? [] : KeyRing.Parse(keys).Servers.Select(k => k.Server).ToList();

        // Пул: подписки в работе по порядку книги, ключи — последними.
        var pool = new List<(string Source, IReadOnlyList<ProxyServer> Servers)>();
        var where = new List<int>();

        for (int i = 0; i < entries.Count; i++)
        {
            if (!entries[i].Working)
                continue;

            pool.Add((entries[i].Name, kept[i]?.Info.Servers ?? []));
            where.Add(i);
        }

        if (keysWorking && keyServers.Count > 0)
        {
            pool.Add((KeyRing.Name, keyServers));
            where.Add(-1);
        }

        var tags = SubscriptionPool.Tag(pool);
        var tagOf = new Dictionary<int, IReadOnlyList<string>>();

        for (int p = 0; p < where.Count; p++)
            tagOf[where[p]] = tags[p];

        var folders = new List<ServerFolder>();
        var now = DateTimeOffset.Now;

        for (int i = 0; i < entries.Count; i++)
        {
            var servers = kept[i]?.Info.Servers ?? [];
            var own = tagOf.GetValueOrDefault(i);

            // Не в работе — у сервера тега пула нет; показываем его своим тегом.
            var rows = servers.Select((s, j) => Row(own is not null && j < own.Count ? own[j] : s.Tag, s)).ToList();

            folders.Add(new ServerFolder(
                entries[i].Name,
                entries[i].Url,
                entries[i].Working,
                false,
                rows,
                Describe(kept[i], now),
                kept[i]?.Info.ExpiryWarningText(now)));
        }

        if (keys.Count > 0)
        {
            var own = tagOf.GetValueOrDefault(-1);

            folders.Add(new ServerFolder(
                KeyRing.Name,
                null,
                keysWorking,
                true,
                keyServers.Select((s, j) => Row(own is not null && j < own.Count ? own[j] : s.Tag, s)).ToList(),
                Plural(keyServers.Count) + (keyServers.Count < keys.Count ? $" · не разобралось: {keys.Count - keyServers.Count}" : string.Empty),
                null));
        }

        return folders;
    }

    /// <summary>Все теги пула — для «Замерить все».</summary>
    public static IReadOnlyList<string> PoolTags(IReadOnlyList<ServerFolder> folders) =>
        folders.Where(f => f.Working).SelectMany(f => f.Servers).Select(s => s.Tag).Distinct(StringComparer.Ordinal).ToList();

    private static ServerRow Row(string tag, ProxyServer server)
    {
        var (country, _) = CountryTag.Split(tag);
        return new ServerRow(tag, TunnelStatus.NameOf(tag), country, Describe(server));
    }

    /// <summary>«VLESS · Reality · xhttp» — реализация, защита и транспорт, без адреса и ключа.</summary>
    private static string Describe(ProxyServer server)
    {
        var name = server.Protocol switch
        {
            ProxyProtocol.Vless => "VLESS",
            ProxyProtocol.Vmess => "VMess",
            _ => server.ProtocolName,
        };

        var parts = new List<string> { name };

        if (server.Security.Equals("reality", StringComparison.OrdinalIgnoreCase))
            parts.Add("Reality");
        else if (server.Security.Equals("tls", StringComparison.OrdinalIgnoreCase) && server.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess)
            parts.Add("TLS");

        if (!string.IsNullOrWhiteSpace(server.Transport) && !server.Transport.Equals("tcp", StringComparison.OrdinalIgnoreCase)
            && server.Protocol is ProxyProtocol.Vless or ProxyProtocol.Vmess or ProxyProtocol.Trojan)
        {
            parts.Add(server.Transport);
        }

        return string.Join(" · ", parts);
    }

    private static (SubscriptionInfo Info, DateTimeOffset At)? SafeKept(string url)
    {
        try
        {
            return SubscriptionPool.Kept(url);
        }
        catch (Exception)
        {
            // Испорченный запас — то же, что его отсутствие: «Обновить» перепишет.
            return null;
        }
    }

    private static string Describe((SubscriptionInfo Info, DateTimeOffset At)? kept, DateTimeOffset now)
    {
        if (kept is not { } k)
            return "Серверов ещё нет — нажмите «Обновить».";

        var parts = new List<string> { Plural(k.Info.Servers.Count), "прочитана " + Ago(now - k.At) };

        if (k.Info.TotalBytes > 0)
            parts.Add($"трафик {Size(k.Info.UsedBytes)} из {Size(k.Info.TotalBytes)}");

        if (k.Info.ExpiresAt is { } expires)
            parts.Add("до " + expires.ToLocalTime().ToString("dd.MM.yyyy"));

        return string.Join(" · ", parts);
    }

    public static string Plural(int count) => Shell.Ui.Plural(count, "сервер", "сервера", "серверов");

    public static string Ago(TimeSpan span) => span.TotalMinutes < 1 ? "только что"
        : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} мин назад"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} ч назад"
        : $"{(int)span.TotalDays} дн назад";

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.#} ТБ",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} МБ",
        _ => $"{bytes / 1024.0:0} КБ",
    };
}
