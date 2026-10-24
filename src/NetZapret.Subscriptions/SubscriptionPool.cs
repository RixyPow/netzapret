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
/// <param name="Deferred">Взята из запаса, не спрашивая панель: её спросят после запуска (<see cref="SubscriptionPool.StartFromReserveFor"/>).</param>
public sealed record PoolPart(
    PoolSource Source,
    IReadOnlyList<ProxyServer> Servers,
    bool FromReserve,
    DateTimeOffset? ReserveAt,
    string? Error,
    SubscriptionInfo? Info,
    bool Deferred = false);

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
    /// Разные панели — разом, подписки с одной панели — по очереди
    /// (<see cref="ByPanel"/>): при запуске движков ждать их одну за другой
    /// значило бы складывать их сроки.
    /// </remarks>
    /// <param name="deferPanels">
    /// Запуск движков: подписку с запасом моложе <see cref="StartFromReserveFor"/>
    /// брать из него, не дожидаясь панели, — её спросит тот, кто звал, после запуска.
    /// </param>
    public static async Task<PoolResult> BuildAsync(
        IReadOnlyList<PoolSource> sources,
        CancellationToken cancellationToken,
        string? reserveDirectory = null,
        IReadOnlyList<string>? keys = null,
        bool deferPanels = false)
    {
        var folder = reserveDirectory ?? DefaultReserveDirectory;
        var raw = new PoolPart[sources.Count];

        await ByPanel(
            sources.Select((source, index) => (source, index)).ToList(),
            x => x.source.Url,
            async x => raw[x.index] = (deferPanels ? Deferred(x.source, folder, DateTimeOffset.Now) : null)
                ?? await ReadAsync(x.source, folder, cancellationToken));

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

    /// <summary>
    /// Обходит подписки: разные панели — разом, подписки одной панели — по очереди,
    /// в том порядке, в каком даны.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Одна панель — по очереди, потому что несколько запросов в одну секунду
    /// с одного адреса ей не нравятся. Разные — разом, потому что иначе их сроки
    /// складываются: 22.10 панель Secureway молчала (12 с до таймаута и напрямую,
    /// и через туннель), а TRUST, отвечающая за 0,4–0,7 с, ждала её на вкладке VPN
    /// все 12 с — обе строки висели на «читаю…».
    /// </para>
    /// <para>
    /// Общее для движка (<see cref="BuildAsync"/>) и вкладки VPN — до 22.10
    /// вкладка читала строго по одной.
    /// </para>
    /// </remarks>
    public static Task ByPanel<T>(IReadOnlyList<T> items, Func<T, string> url, Func<T, Task> read) =>
        Task.WhenAll(items
            .GroupBy(item => Panel(url(item)), StringComparer.OrdinalIgnoreCase)
            .Select(async panel =>
            {
                foreach (var item in panel)
                    await read(item);
            }));

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

    /// <summary>
    /// Сколько прочитанная подписка считается свежей и не перечитывается.
    /// </summary>
    /// <remarks>
    /// Владелец, 28.09: «почему так часто проводится чтение подписок». Раздел
    /// VPN перечитывал все подписки по очереди при каждом заходе и после
    /// каждого щелчка — выключатель «в работе», переименование, удаление, —
    /// а движок ещё раз при каждом запуске. Лежащая «Основная» стоила каждый
    /// раз 12 с ожидания. Серверы у продавцов меняются раз в дни, не минуты;
    /// силой перечитывают ⟳ и «Обновить».
    /// </remarks>
    public static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(30);

    /// <summary>
    /// До какого возраста запасом можно запускать движки, не дожидаясь панели.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Панель TRUST 16.10 отвечала 502 через 10 с, и запуск ждал её столько же:
    /// владелец принимал это за «движки поднимаются не с первого раза». Свежий
    /// запас (<see cref="FreshFor"/>) и так читается без сети; старше получаса
    /// запуск шёл к панели и ждал её, хотя в запасе лежал вчерашний вечер.
    /// </para>
    /// <para>
    /// Сутки, а не любой возраст: серверы у продавцов меняются раз в дни,
    /// и запас после выходных скорее устарел — тогда честнее подождать панель,
    /// чем поднять туннель на старые ключи. Моложе — панель спрашивается после
    /// запуска, а изменившийся список предлагается перезапуском, как у
    /// перечитывания без живых выходов.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan StartFromReserveFor = TimeSpan.FromHours(24);

    /// <summary>Подписка из запаса без панели — для запуска; <c>null</c> — запас не годится, читать как обычно.</summary>
    internal static PoolPart? Deferred(PoolSource source, string folder, DateTimeOffset now)
    {
        if (LoadReserve(folder, source.Url) is not { } kept || kept.Info.Servers.Count == 0)
            return null;

        var age = now - kept.At;

        // Свежий читается без сети и так, и спрашивать его потом незачем.
        if (age < FreshFor || age >= StartFromReserveFor)
            return null;

        return new PoolPart(source, kept.Info.Servers, false, kept.At, null, kept.Info, Deferred: true);
    }

    /// <summary>
    /// Те же ли серверы: адреса, ключи, параметры и имена — то, без чего новый
    /// список в движок не попадёт. Квота и срок не в счёт.
    /// </summary>
    public static bool SameServers(IReadOnlyList<ProxyServer> before, IReadOnlyList<ProxyServer> after)
    {
        static HashSet<string> Of(IReadOnlyList<ProxyServer> servers) =>
            servers.Select(s => Identity(s) + "|" + s.Tag).ToHashSet(StringComparer.Ordinal);

        return Of(before).SetEquals(Of(after));
    }

    /// <summary>Одна подписка: свежий запас без сети, иначе панель, при отказе — запас любой давности.</summary>
    /// <param name="force">Идти к панели, даже если запас свежий: ⟳ и «Обновить».</param>
    public static async Task<SubscriptionRead> ReadOneAsync(
        string url,
        bool force,
        CancellationToken cancellationToken,
        string? reserveDirectory = null)
    {
        var folder = reserveDirectory ?? DefaultReserveDirectory;

        if (!force && LoadReserve(folder, url) is { } fresh
            && fresh.Info.Servers.Count > 0
            && DateTimeOffset.Now - fresh.At < FreshFor)
        {
            return new SubscriptionRead(fresh.Info, fresh.At, SubscriptionReadSource.Fresh, null);
        }

        string error;

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(url), cancellationToken);

            // Ответила, но не разобралась — в запас такое не кладём: он
            // затёр бы последний хороший ответ пустым.
            if (info.Servers.Count > 0 && client.LastBody is { } body)
                SaveReserve(folder, url, body, info);

            // Расход — для полоски и прогноза на вкладке VPN (SubscriptionUsage, 19.10).
            // Только при своей папке запаса: чужая — это проверка, и писать
            // её подписки в историю рабочей установки незачем.
            if (reserveDirectory is null)
                SubscriptionUsage.Record(url, info, DateTimeOffset.Now);

            if (info.Servers.Count > 0 || info.Errors.Count == 0)
                return new SubscriptionRead(info, DateTimeOffset.Now, SubscriptionReadSource.Panel, null);

            error = "не разобралась: " + info.Errors[0];

            if (LoadReserve(folder, url) is not { } kept || kept.Info.Servers.Count == 0)
                return new SubscriptionRead(info, DateTimeOffset.Now, SubscriptionReadSource.Panel, null);

            return new SubscriptionRead(kept.Info, kept.At, SubscriptionReadSource.Reserve, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Тишина без сети — тоже не панель: 23.10 с поднятым TUN и без Wi-Fi
            // чтение ждало все 12 с.
            error = ex is not OperationCanceledException ? PanelError.Describe(ex)
                : !NetZapret.Core.PhysicalNetwork.Up() ? NetZapret.Core.PhysicalNetwork.Offline
                : $"панель не ответила за {SubscriptionClient.DefaultTimeout.TotalSeconds:0} с";
        }

        if (await ReadFallbackAsync(folder, url, cancellationToken) is { } rescued)
            return rescued with { Error = $"{error} — прочитана по запасному адресу поставщика" };

        if (LoadReserve(folder, url) is { } reserve && reserve.Info.Servers.Count > 0)
            return new SubscriptionRead(reserve.Info, reserve.At, SubscriptionReadSource.Reserve, error);

        return new SubscriptionRead(null, null, SubscriptionReadSource.None, error);
    }

    /// <summary>
    /// Запасной адрес панели (<c>fallback-url</c>), если основной не ответил.
    /// </summary>
    /// <remarks>
    /// Адрес поставщик называет заранее, в прошлом удачном ответе, — потому он
    /// и лежит в запасе. Как в Happ: на ошибку, тишину или срок. Прочитанное
    /// ложится в запас под основной ссылкой: в книге подписка та же, и ссылка
    /// в ней не меняется (этим занят <c>new-url</c>). Неудача запасного не
    /// заменяет причину основного: человеку важнее, почему не ответила его панель.
    /// </remarks>
    private static async Task<SubscriptionRead?> ReadFallbackAsync(string folder, string url, CancellationToken cancellationToken)
    {
        if (LoadReserve(folder, url)?.Info.FallbackUrl is not { } fallback
            || string.Equals(fallback, url, StringComparison.Ordinal))
            return null;

        try
        {
            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(fallback), cancellationToken);

            if (info.Servers.Count == 0 || client.LastBody is not { } body)
                return null;

            // Запасная панель могла не назвать запасного адреса — прежний остаётся.
            info = info with { FallbackUrl = info.FallbackUrl ?? fallback };
            SaveReserve(folder, url, body, info);

            return new SubscriptionRead(info, DateTimeOffset.Now, SubscriptionReadSource.Panel, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<PoolPart> ReadAsync(PoolSource source, string folder, CancellationToken cancellationToken)
    {
        var read = await ReadOneAsync(source.Url, force: false, cancellationToken, folder);

        return read.Source switch
        {
            SubscriptionReadSource.Fresh or SubscriptionReadSource.Panel when read.Info!.Servers.Count > 0
                => new PoolPart(source, read.Info.Servers, false, null, null, read.Info),
            SubscriptionReadSource.Reserve
                => new PoolPart(source, read.Info!.Servers, true, read.At, null, null),
            SubscriptionReadSource.Panel
                => new PoolPart(source, [], false, null,
                    read.Info!.Errors.Count > 0 ? "не разобралась: " + read.Info.Errors[0] : "серверов нет", read.Info),
            _ => new PoolPart(source, [], false, null, read.Error, null),
        };
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

        // Одинаковые имена разных узлов внутри одной подписки — суффиксом
        // « #2», « #3», ровно как делает сборка конфига (AssignUniqueTags):
        // иначе движок дописывал бы его сам, и окно не узнавало бы сервер
        // по тегу — так карточка текущего сервера не нашла «proxy-3 #22»
        // (Trust, 28.09). Пул раздаёт уникальные имена сам, и движку
        // дописывать нечего.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "direct", "auto" };
        var unique = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, (p, i)) in first)
        {
            var baseTag = Own(p, i);
            var tag = baseTag;

            for (int suffix = 2; !used.Add(tag); suffix++)
                tag = $"{baseTag} #{suffix}";

            unique[key] = tag;
        }

        return parts
            .Select(part => part.Servers
                .Select(server => unique[Identity(server)])
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

    /// <summary>
    /// Узел — всё, чем сервер отличается на проводе. Имя не в счёт: его выбирает продавец.
    /// </summary>
    /// <remarks>
    /// Не только адрес, порт и ключ: у Trust (28.09) все страны сидят на одном
    /// входе 131.123.25.7:443 с одним ключом и различаются SNI и параметрами
    /// Reality — по ним вход и разводит на выходы. Первая версия склейки
    /// этого не видела и сложила «Германию», «Францию» и ещё полтора десятка
    /// в один сервер.
    /// </remarks>
    internal static string Identity(ProxyServer server) => string.Join("|",
        server.Protocol, server.Host.Trim().ToLowerInvariant(), server.Port, server.Credential,
        server.Transport, server.Security, server.Sni, server.Flow,
        server.RealityPublicKey, server.RealityShortId, server.Path, server.HostHeader, server.ServiceName,
        server.ObfsType, server.ObfsPassword);

    private static string Name(ProxyServer server) =>
        string.IsNullOrWhiteSpace(server.Tag) ? $"{server.Host}:{server.Port}" : server.Tag.Trim();

    // --- Запас ----------------------------------------------------------------

    /// <summary>Имя файла запаса — отпечаток ссылки, а не она сама: ссылка — пароль.</summary>
    private static string ReservePath(string folder, string url) =>
        Path.Combine(folder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..24] + ".txt");

    /// <summary>
    /// Стирает запасы всех подписок, кроме названных; возвращает, сколько стёрто.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Владелец 30.09. Запас — весь ответ панели, с ключами серверов, то есть
    /// тот же пароль, что и ссылка. Подписку убирали, а её запас оставался
    /// в runtime\subscriptions навсегда: у владельца лежал запас WOW VPN,
    /// которой в списке уже не было. В git и в архив runtime\ не идёт,
    /// но держать чужие ключи без нужды незачем.
    /// </para>
    /// <para>
    /// От обратного — «оставить эти», а не «стереть ту»: имя файла — отпечаток
    /// ссылки, и по нему не узнать, чей это запас. Так уходят и запасы,
    /// оставшиеся от подписок, убранных до этой правки.
    /// </para>
    /// </remarks>
    public static int KeepReserves(IEnumerable<string?> urls, string? reserveDirectory = null)
    {
        var folder = reserveDirectory ?? DefaultReserveDirectory;

        if (!Directory.Exists(folder))
            return 0;

        var keep = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => Path.GetFileName(ReservePath(folder, url!)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int removed = 0;

        foreach (var body in Directory.GetFiles(folder, "*.txt"))
        {
            if (keep.Contains(Path.GetFileName(body)))
                continue;

            try
            {
                File.Delete(body);
                File.Delete(body + ".meta");
                removed++;
            }
            catch (Exception)
            {
                // Занят или нет прав — сотрётся при следующей чистке.
            }
        }

        return removed;
    }

    /// <summary>
    /// Квота и срок лежат рядом с телом: они приходят заголовками, а не в теле,
    /// и без них строка подписки из запаса теряла бы «86 дн» и остаток трафика.
    /// </summary>
    /// <remarks>
    /// С 15.10 — и остальное, что поставщик шлёт заголовками: поддержка, сайт,
    /// объявление и запасной адрес панели. Без них карточка подписки из запаса
    /// теряла бы кнопки, а запасной адрес и нужен ровно тогда, когда панель
    /// не отвечает, — то есть когда кроме запаса взять его неоткуда.
    /// </remarks>
    private sealed record ReserveMeta(
        string? Title, long Upload, long Download, long Total, DateTimeOffset? Expires, double? Interval,
        string? Support = null, string? WebPage = null, string? Announce = null, string? Fallback = null);

    internal static void SaveReserve(string folder, string url, string body, SubscriptionInfo? info = null)
    {
        try
        {
            Directory.CreateDirectory(folder);

            var path = ReservePath(folder, url);

            if (info is not null)
            {
                var meta = new ReserveMeta(
                    info.Title, info.UploadBytes, info.DownloadBytes, info.TotalBytes, info.ExpiresAt, info.UpdateIntervalHours,
                    info.SupportUrl, info.WebPageUrl, info.Announce, info.FallbackUrl);
                File.WriteAllText(path + ".meta", System.Text.Json.JsonSerializer.Serialize(meta), new UTF8Encoding(false));
            }

            // Тело — последним: по его времени судят о свежести.
            var temp = path + ".tmp";

            File.WriteAllText(temp, body, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Запас — страховка, а не условие работы.
        }
    }

    /// <summary>
    /// Запас подписки — под новым адресом, когда поставщик её перенёс (<c>new-url</c>).
    /// </summary>
    /// <remarks>
    /// Запас лежит под отпечатком ссылки: без копии новая ссылка начинала бы
    /// с пустого места, и первый же отказ новой панели оставил бы без серверов.
    /// Старый запас уберёт обычная уборка — его ссылки в книге больше нет.
    /// Копия хранит время файла, и свежесть запаса не врёт.
    /// </remarks>
    public static void MoveReserve(string oldUrl, string newUrl, string? reserveDirectory = null)
    {
        var folder = reserveDirectory ?? DefaultReserveDirectory;

        try
        {
            var from = ReservePath(folder, oldUrl);
            var to = ReservePath(folder, newUrl);

            if (File.Exists(from + ".meta"))
                File.Copy(from + ".meta", to + ".meta", overwrite: true);

            if (File.Exists(from))
                File.Copy(from, to, overwrite: true);
        }
        catch (Exception)
        {
            // Запас — страховка: новая ссылка прочитается и без него.
        }
    }

    /// <summary>
    /// Подписка из запаса, без обращения к панели: серверы, квота и срок
    /// на момент последнего ответа. <c>null</c> — запаса нет.
    /// </summary>
    /// <remarks>
    /// Для тех, кому нужен только остаток трафика (предупреждение перед замером
    /// скорости, 30.09): ходить ради него к панели незачем.
    /// </remarks>
    public static (SubscriptionInfo Info, DateTimeOffset At)? Kept(string url, string? reserveDirectory = null) =>
        LoadReserve(reserveDirectory ?? DefaultReserveDirectory, url);

    /// <summary>
    /// Запас, который вкладке VPN показать, пока спрашивается панель; <c>null</c> —
    /// показывать нечего или панель спрашиваться не будет.
    /// </summary>
    /// <remarks>
    /// Владелец 22.10: «запас подписки показывать сразу с пометкой „обновляю…“,
    /// ответ панели подставлять, когда придёт». Прежде строка подписки ждала
    /// панель на «читаю…»: у лежащей Secureway — все 12 с до таймаута, хотя
    /// её одиннадцать серверов лежали в запасе. Свежий запас (моложе
    /// <see cref="FreshFor"/>) и без того отдаётся <see cref="ReadOneAsync"/>
    /// без сети — показывать его дважды незачем.
    /// </remarks>
    public static (SubscriptionInfo Info, DateTimeOffset At)? KeptWhileAsking(
        string url, bool force, DateTimeOffset now, string? reserveDirectory = null)
    {
        if (Kept(url, reserveDirectory) is not { } kept || kept.Info.Servers.Count == 0)
            return null;

        return force || now - kept.At >= FreshFor ? kept : null;
    }

    private static (SubscriptionInfo Info, DateTimeOffset At)? LoadReserve(string folder, string url)
    {
        try
        {
            var path = ReservePath(folder, url);

            if (!File.Exists(path))
                return null;

            var (servers, errors) = SubscriptionParser.ParseBody(File.ReadAllText(path));

            ReserveMeta? meta = null;

            try
            {
                if (File.Exists(path + ".meta"))
                    meta = System.Text.Json.JsonSerializer.Deserialize<ReserveMeta>(File.ReadAllText(path + ".meta"));
            }
            catch (Exception)
            {
                // Без квоты и срока запас всё равно годен.
            }

            var info = new SubscriptionInfo
            {
                Servers = servers,
                Errors = errors,
                Title = meta?.Title,
                UploadBytes = meta?.Upload ?? 0,
                DownloadBytes = meta?.Download ?? 0,
                TotalBytes = meta?.Total ?? 0,
                ExpiresAt = meta?.Expires,
                UpdateIntervalHours = meta?.Interval,
                SupportUrl = meta?.Support,
                WebPageUrl = meta?.WebPage,
                Announce = meta?.Announce,
                FallbackUrl = meta?.Fallback,
            };

            return (info, File.GetLastWriteTime(path));
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Откуда взялась подписка.</summary>
public enum SubscriptionReadSource
{
    /// <summary>Ничего: панель не ответила, запаса нет.</summary>
    None,

    /// <summary>Свежий запас, к панели не ходили.</summary>
    Fresh,

    /// <summary>Ответ панели только что.</summary>
    Panel,

    /// <summary>Панель подвела — запас прошлого удачного чтения.</summary>
    Reserve,
}

/// <summary>Чтение одной подписки.</summary>
/// <param name="Info"><c>null</c>, только когда <see cref="Source"/> — <see cref="SubscriptionReadSource.None"/>.</param>
/// <param name="At">Когда получен ответ панели, из которого взяты серверы.</param>
/// <param name="Error">Почему панель подвела; у запаса — причина, по которой пришлось к нему идти.</param>
public sealed record SubscriptionRead(SubscriptionInfo? Info, DateTimeOffset? At, SubscriptionReadSource Source, string? Error);
