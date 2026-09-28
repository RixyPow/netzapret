using System.Security.Cryptography;
using System.Text;

namespace NetZapret.Subscriptions;

/// <summary>Подписка, включённая в пул: имя для меток и ссылка.</summary>
/// <param name="Url">Пароль: не печатается и не пишется в журнал.</param>
public sealed record PoolSource(string Name, string Url);

/// <summary>Что дала одна подписка пула.</summary>
/// <param name="Servers">Серверы в порядке подписки, с тегами пула (<see cref="SubscriptionPool.Tag"/>).</param>
/// <param name="FromReserve">Панель не ответила, и серверы взяты из запаса прошлого удачного чтения.</param>
/// <param name="Error">Почему не прочиталась; <c>null</c> — прочиталась или взята из запаса.</param>
public sealed record PoolPart(
    PoolSource Source,
    IReadOnlyList<ProxyServer> Servers,
    bool FromReserve,
    DateTimeOffset? ReserveAt,
    string? Error,
    SubscriptionInfo? Info);

/// <summary>Пул целиком: серверы для движка и отчёт по каждой подписке.</summary>
/// <param name="Servers">Без двойников, с тегами пула — то, что идёт в конфиг.</param>
public sealed record PoolResult(IReadOnlyList<ProxyServer> Servers, IReadOnlyList<PoolPart> Parts);

/// <summary>
/// Серверы из нескольких подписок в одном автоподборе.
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 28.09 (0.9.0). Прежде движок собирался из одной
/// «действующей» подписки, и когда её выход ложился, человек шёл
/// переключать подписки руками. В пуле автоподбор выбирает из всех
/// включённых сразу, а «выбрать» работает для любого сервера.
/// </para>
/// <para>
/// «Только рабочие» (условие владельца) обеспечивает сборка конфига, а не
/// пул: сервер с тремя неудачами замера подряд автоподбор не видит
/// (<c>SingBoxOptions.DeadServerTags</c>), но в конфиге остаётся — лёгший
/// утром к вечеру часто оживает, и без перезапуска движков вернуть его
/// было бы нельзя.
/// </para>
/// <para>
/// Три договорённости с владельцем. Лежащая панель запуск не срывает: берётся
/// последний удачный ответ из запаса, а без запаса подписки просто нет.
/// Один и тот же узел у двух продавцов — одна строка: совпадают протокол,
/// адрес, порт и ключ. Одинаковые имена из разных подписок получают метку
/// подписки: «🇩🇪 Германия · SecureWay», — иначе в списке и в «сейчас …»
/// их не различить.
/// </para>
/// </remarks>
public static class SubscriptionPool
{
    /// <summary>Запас ответов панелей. В runtime\: туда не смотрит git и не берёт архив.</summary>
    public static string DefaultReserveDirectory => Path.Combine("runtime", "subscriptions");

    /// <summary>
    /// Читает все подписки разом и склеивает пул.
    /// </summary>
    /// <remarks>
    /// Разные панели — разом: при запуске движков ждать их одну за другой
    /// значило бы складывать их сроки. Подписки с одной панели — по очереди:
    /// панель у разных подписок часто одна и та же, и несколько запросов
    /// в одну секунду с одного адреса ей не нравятся (урок раздела VPN,
    /// который по той же причине читает подписки по очереди).
    /// </remarks>
    public static async Task<PoolResult> BuildAsync(
        IReadOnlyList<PoolSource> sources,
        CancellationToken cancellationToken,
        string? reserveDirectory = null,
        IReadOnlyList<string>? keys = null)
    {
        var folder = reserveDirectory ?? DefaultReserveDirectory;
        var raw = new PoolPart[sources.Count];

        await Task.WhenAll(sources
            .Select((source, index) => (source, index))
            .GroupBy(x => Panel(x.source.Url), StringComparer.OrdinalIgnoreCase)
            .Select(async panel =>
            {
                foreach (var (source, index) in panel)
                    raw[index] = await ReadAsync(source, folder, cancellationToken);
            }));

        // Отдельные ключи — последним источником: читать их не надо, они уже
        // здесь. Разобрались не все — это видно в папке ключей, пул берёт
        // разобравшиеся.
        var all = raw.ToList();

        if (keys is { Count: > 0 })
        {
            var (parsed, errors) = KeyRing.Parse(keys);

            all.Add(new PoolPart(
                new PoolSource(KeyRing.Name, string.Empty),
                parsed.Select(k => k.Server).ToList(),
                false,
                null,
                parsed.Count == 0 && errors.Count > 0 ? errors[0] : null,
                null));
        }

        var tags = Tags(all.Select(r => (r.Source.Name, r.Servers)).ToList());

        var parts = all
            .Select((part, i) => part with
            {
                Servers = part.Servers.Select((s, j) => s with { Tag = tags[i][j] }).ToList(),
            })
            .ToList();

        return new PoolResult(Merge(parts), parts);
    }

    /// <summary>Чья панель: адрес после развёртки обёртки клиента.</summary>
    private static string Panel(string url)
    {
        try
        {
            return SubscriptionClient.Unwrap(new Uri(url)).Host;
        }
        catch (Exception)
        {
            return url;
        }
    }

    private static async Task<PoolPart> ReadAsync(PoolSource source, string folder, CancellationToken cancellationToken)
    {
        string? error;

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(source.Url), cancellationToken);

            // Ответила, но не разобралась — в запас такое не кладём: он
            // затёр бы последний хороший ответ пустым.
            if (info.Servers.Count > 0 && client.LastBody is { } body)
                SaveReserve(folder, source.Url, body);

            if (info.Servers.Count > 0)
                return new PoolPart(source, info.Servers, false, null, null, info);

            error = info.Errors.Count > 0 ? "не разобралась: " + info.Errors[0] : "серверов нет";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex is OperationCanceledException
                ? $"панель не ответила за {SubscriptionClient.DefaultTimeout.TotalSeconds:0} с"
                : ex.GetBaseException().Message;
        }

        if (LoadReserve(folder, source.Url) is { } reserve && reserve.Servers.Count > 0)
            return new PoolPart(source, reserve.Servers, true, reserve.At, null, null);

        return new PoolPart(source, [], false, null, error, null);
    }

    /// <summary>
    /// Теги пула: по списку на каждую подписку, в том же порядке, что её серверы.
    /// </summary>
    /// <remarks>
    /// Двойник получает тег того, кого повторяет: это один и тот же узел,
    /// и выбор или замер его в любой из подписок — одно и то же. Метка
    /// подписки ставится только при совпадении имён в разных подписках:
    /// у кого подписка одна, теги не меняются вовсе, и прежние замеры
    /// и выбор сервера остаются при своих.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<string>> Tag(
        IReadOnlyList<(string Source, IReadOnlyList<ProxyServer> Servers)> parts) => Tags(parts);

    private static List<List<string>> Tags(IReadOnlyList<(string Source, IReadOnlyList<ProxyServer> Servers)> parts)
    {
        // Первое появление каждого узла: кто он и откуда.
        var first = new Dictionary<string, (int Part, int Index)>(StringComparer.Ordinal);

        for (int p = 0; p < parts.Count; p++)
        {
            for (int i = 0; i < parts[p].Servers.Count; i++)
                first.TryAdd(Identity(parts[p].Servers[i]), (p, i));
        }

        // Сколько разных подписок называют своих (не двойников) одним именем.
        var sourcesByName = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, (p, i)) in first)
        {
            var name = Name(parts[p].Servers[i]);

            if (!sourcesByName.TryGetValue(name, out var set))
                sourcesByName[name] = set = [];

            set.Add(p);
        }

        string Own(int p, int i)
        {
            var name = Name(parts[p].Servers[i]);

            return sourcesByName[name].Count > 1 ? $"{name} · {parts[p].Source}" : name;
        }

        return parts
            .Select((part, p) => part.Servers
                .Select((server, i) =>
                {
                    var (fp, fi) = first[Identity(server)];
                    return Own(fp, fi);
                })
                .ToList())
            .ToList();
    }

    /// <summary>Серверы пула без двойников — первое появление каждого узла.</summary>
    private static List<ProxyServer> Merge(IReadOnlyList<PoolPart> parts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ProxyServer>();

        foreach (var part in parts)
        {
            foreach (var server in part.Servers)
            {
                if (seen.Add(Identity(server)))
                    result.Add(server);
            }
        }

        return result;
    }

    /// <summary>Узел — протокол, адрес, порт и ключ. Имя не в счёт: его выбирает продавец.</summary>
    internal static string Identity(ProxyServer server) =>
        $"{server.Protocol}|{server.Host.Trim().ToLowerInvariant()}|{server.Port}|{server.Credential}";

    private static string Name(ProxyServer server) =>
        string.IsNullOrWhiteSpace(server.Tag) ? $"{server.Host}:{server.Port}" : server.Tag.Trim();

    // --- Запас ----------------------------------------------------------------

    /// <summary>Имя файла запаса — отпечаток ссылки, а не она сама: ссылка — пароль.</summary>
    private static string ReservePath(string folder, string url) =>
        Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..24] + ".txt");

    internal static void SaveReserve(string folder, string url, string body)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var path = ReservePath(folder, url);
            var temp = path + ".tmp";

            File.WriteAllText(temp, body, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Запас — страховка, а не условие работы.
        }
    }

    private static (IReadOnlyList<ProxyServer> Servers, DateTimeOffset At)? LoadReserve(string folder, string url)
    {
        try
        {
            var path = ReservePath(folder, url);

            if (!File.Exists(path))
                return null;

            var (servers, _) = SubscriptionParser.ParseBody(File.ReadAllText(path));

            return (servers, File.GetLastWriteTime(path));
        }
        catch (Exception)
        {
            return null;
        }
    }
}
