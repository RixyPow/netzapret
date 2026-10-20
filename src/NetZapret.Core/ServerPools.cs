namespace NetZapret.Core;

/// <summary>Пул серверов — папка на вкладке VPN, в которую ведёт маршрут «через VPN».</summary>
/// <param name="Id">
/// Ключ пула: на него ссылаются правила (<see cref="ServerPools.RuleServer"/>)
/// и теги групп движка. Не имя — переименование не должно рвать маршруты.
/// </param>
/// <param name="Name">Имя на экране: «Россия», «Скоростной».</param>
/// <param name="Servers">Теги серверов пула; сервер может лежать в нескольких пулах.</param>
/// <param name="Preferred">Закреплённый в пуле сервер; <c>null</c> — автоподбор внутри пула.</param>
/// <param name="Held">
/// Закреплённый до включения автоподбора: вернётся, когда автоподбор
/// выключат (<see cref="ServerPools.SetAuto"/>). Движок его не читает.
/// </param>
/// <remarks>
/// Автоподбор — отсутствие закрепления, а не своё поле: так пулы,
/// записанные до тумблера (19.10), читаются как были, и движку с надзором
/// по-прежнему хватает одного <see cref="Preferred"/>.
/// </remarks>
public sealed record ServerPool(
    string Id,
    string Name,
    IReadOnlyList<string> Servers,
    string? Preferred = null,
    string? Held = null);

/// <summary>
/// Пулы серверов: разные выходы разным маршрутам.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 19.10 (обсуждение №19, идеи №6): пул — папка серверов через все
/// подписки; поначалу все серверы в пуле по умолчанию, и у обычного человека
/// не меняется ничего. Пулом по умолчанию остаётся прежняя группа выбора
/// движка (<c>auto</c>) со своим закреплением (<see cref="AppSettings.PreferredServer"/>),
/// в настройках его нет — он всегда «все серверы».
/// </para>
/// <para>
/// Маршрут ссылается на пул полем сервера правила: <c>server: pool:ключ</c>.
/// Поле было и прежде — для тега одного сервера, — так что файл правил
/// старых версий читается как читался, а неизвестный пул движок ведёт
/// в пул по умолчанию (<c>SingBoxConfigCompiler.ResolveOutbound</c>).
/// </para>
/// </remarks>
public static class ServerPools
{
    /// <summary>Приставка пула в поле сервера правила.</summary>
    public const string RulePrefix = "pool:";

    /// <summary>Значение поля сервера правила, ведущего в пул.</summary>
    public static string RuleServer(string id) => RulePrefix + id;

    /// <summary>Ключ пула из поля сервера правила; <c>null</c> — правило ведёт не в пул.</summary>
    public static string? IdOf(string? ruleServer) =>
        ruleServer is not null
        && ruleServer.StartsWith(RulePrefix, StringComparison.OrdinalIgnoreCase)
        && ruleServer.Length > RulePrefix.Length
            ? ruleServer[RulePrefix.Length..].Trim()
            : null;

    /// <summary>Тег группы выбора пула в конфиге движка.</summary>
    /// <remarks>Окно и надзор узнают группы пулов по этой приставке.</remarks>
    public static string SelectorTag(string id) => SelectorPrefix + id;

    /// <summary>Тег автоподбора пула в конфиге движка.</summary>
    public static string LatencyTag(string id) => SelectorPrefix + id + "-latency";

    /// <summary>Приставка тегов групп пулов.</summary>
    public const string SelectorPrefix = "pool-";

    /// <summary>Пул по ключу; <c>null</c> — нет такого.</summary>
    public static ServerPool? Find(IEnumerable<ServerPool> pools, string? id) =>
        id is null ? null : pools.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Новый пул с ключом, которого ещё нет.</summary>
    /// <remarks>
    /// Ключ — восемь знаков: он попадает в тег движка и в файл правил,
    /// имя на кириллице там читать неудобно, а уникальность среди
    /// своих пулов проверяется здесь же.
    /// </remarks>
    public static ServerPool Create(IEnumerable<ServerPool> existing, string name)
    {
        var taken = existing.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string id;

        do
            id = Guid.NewGuid().ToString("N")[..8];
        while (taken.Contains(id));

        return new ServerPool(id, name.Trim(), []);
    }

    /// <summary>Кладёт сервер в пул; уже лежащий не дублируется.</summary>
    public static IReadOnlyList<ServerPool> Put(IReadOnlyList<ServerPool> pools, string id, string server) =>
        [.. pools.Select(p => p.Id == id && !p.Servers.Contains(server, StringComparer.Ordinal)
            ? p with { Servers = [.. p.Servers, server] }
            : p)];

    /// <summary>Вынимает сервер из пула; закреплённый в нём снимается с закрепления.</summary>
    public static IReadOnlyList<ServerPool> Take(IReadOnlyList<ServerPool> pools, string id, string server) =>
        [.. pools.Select(p => p.Id == id
            ? p with
            {
                Servers = [.. p.Servers.Where(s => s != server)],
                Preferred = p.Preferred == server ? null : p.Preferred,
                Held = p.Held == server ? null : p.Held,
            }
            : p)];

    /// <summary>Закрепляет сервер в пуле — «Выбрать» в папке пула; автоподбор выключается.</summary>
    public static IReadOnlyList<ServerPool> Pin(IReadOnlyList<ServerPool> pools, string id, string server) =>
        [.. pools.Select(p => p.Id == id ? p with { Preferred = server, Held = null } : p)];

    /// <summary>
    /// Тумблер автоподбора пула (владелец 20.10: «сделай автовыбор тумблером
    /// для каждого пула»).
    /// </summary>
    /// <remarks>
    /// Включение снимает закрепление, но помнит сервер. Выключение возвращает
    /// его, если он ещё в пуле, иначе закрепляет <paramref name="fallback"/> —
    /// окно даёт быстрейший живой по замерам. Закреплять некого — пул остаётся
    /// на автоподборе: пул без выхода хуже пула с автоподбором.
    /// </remarks>
    public static IReadOnlyList<ServerPool> SetAuto(
        IReadOnlyList<ServerPool> pools, string id, bool auto, string? fallback = null) =>
        [.. pools.Select(p =>
        {
            if (p.Id != id)
                return p;

            if (auto)
                return p.Preferred is null ? p : p with { Preferred = null, Held = p.Preferred };

            if (p.Preferred is not null)
                return p;

            var back = p.Held is { } held && p.Servers.Contains(held, StringComparer.Ordinal) ? held
                : fallback is not null && p.Servers.Contains(fallback, StringComparer.Ordinal) ? fallback
                : null;

            return back is null ? p : p with { Preferred = back, Held = null };
        })];

    /// <summary>
    /// Мёртвые по замерам серверы, которые всё же идут в конфиг ради пулов:
    /// закреплённые в пуле и все серверы пула, где живых нет.
    /// </summary>
    /// <remarks>
    /// Мёртвые в конфиг не берутся (окно, «в пуле только рабочие», 28.09).
    /// Пул, мёртвый целиком, без этого исчез бы из конфига, и его маршруты
    /// ушли бы в пул по умолчанию молча — и тогда, когда человек велел
    /// ждать (<see cref="AppSettings.DeadPoolToDefault"/>).
    /// </remarks>
    public static IReadOnlySet<string> KeptDespiteDeath(
        IEnumerable<ServerPool> pools, IEnumerable<string> present, IEnumerable<string> dead)
    {
        var here = present.ToHashSet(StringComparer.Ordinal);
        var gone = dead.ToHashSet(StringComparer.Ordinal);
        var keep = new HashSet<string>(StringComparer.Ordinal);

        foreach (var pool in pools)
        {
            var members = pool.Servers.Where(here.Contains).ToList();

            if (members.Count > 0 && members.All(gone.Contains))
                keep.UnionWith(members);

            if (pool.Preferred is { } pinned && here.Contains(pinned))
                keep.Add(pinned);
        }

        return keep;
    }

    /// <summary>
    /// Чьи адреса выводить из перехвата десинка первыми: закреплённый сервер,
    /// закреплённые в пулах, за ними прочие серверы пулов.
    /// </summary>
    /// <remarks>
    /// Из перехвата выводится не больше 40 адресов (TunnelCapture), и серверы
    /// пулов не должны остаться за чертой: под десинком туннель душило (замер 30.09).
    /// </remarks>
    public static IReadOnlyList<string> CaptureFirst(string? preferred, IEnumerable<ServerPool> pools)
    {
        var list = pools.ToList();

        return [.. new[] { preferred }
            .Concat(list.Select(p => p.Preferred))
            .Concat(list.SelectMany(p => p.Servers))
            .OfType<string>()
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Пулы, на которые ссылаются серверы правил; ссылки на несуществующие не в счёт.</summary>
    public static IReadOnlySet<string> Referenced(IEnumerable<string?> ruleServers, IEnumerable<ServerPool> pools)
    {
        var known = pools.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        return ruleServers
            .Select(IdOf)
            .OfType<string>()
            .Where(known.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
