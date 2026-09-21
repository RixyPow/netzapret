namespace NetZapret.Core.Rules;

/// <summary>Что получилось из переноса.</summary>
public sealed record Migration
{
    public required RouteBook Book { get; init; }

    /// <summary>
    /// Правила, которые книгой не выражаются и остаются как были.
    /// </summary>
    /// <remarks>
    /// Правила по адресам: книга говорит именами, а подсеть именем
    /// не назовёшь. Их четыре у владельца — ipset телеграма, дискорда,
    /// ютуба и твиттера, — и терять их нельзя: они ловят соединения,
    /// идущие по адресу без всякого имени, то есть ровно то, чего
    /// доменное правило не видит.
    /// </remarks>
    public IReadOnlyList<RoutingRule> Kept { get; init; } = [];

    /// <summary>Что стоит сказать вслух про перенос.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// Переносит прежние правила и пины в книгу маршрутов.
/// </summary>
/// <remarks>
/// <para>
/// Самая рискованная часть первой цели 0.7.0. У владельца восемьдесят
/// восемь правил и сто двенадцать пинов — работа месяцев, — и потерять
/// нельзя ни одного.
/// </para>
/// <para>
/// Поэтому перенос обратим: из книги собираются прежние правила,
/// и сравнить их с исходными можно дословно. Проверка на этом
/// и построена — не «перенеслось похоже», а «собралось обратно
/// то же самое».
/// </para>
/// <para>
/// Чистая функция: ни диска, ни настроек. Пины передаются готовыми,
/// а не читаются отсюда, — иначе проверку пришлось бы ставить
/// над системным файлом.
/// </para>
/// </remarks>
public static class RouteBookMigration
{
    /// <param name="pins">Что прибито нами: имя → адрес.</param>
    public static Migration From(RuleSet rules, IReadOnlyCollection<string>? pins = null)
    {
        var entries = new List<RouteEntry>();
        var kept = new List<RoutingRule>();
        var notes = new List<string>();

        foreach (var rule in rules.Rules)
        {
            if (NameOf(rule) is not { } name)
            {
                kept.Add(rule);
                continue;
            }

            entries.Add(new RouteEntry
            {
                Name = name,
                Choice = ChoiceOf(rule.Mode),
                Recipe = rule.Mode == RoutingMode.Desync ? rule.Recipe : null,
            });
        }

        // Пины после правил: имя может быть названо и там и там — tmdb
        // у владельца и выведен напрямую, и прибит. Это не противоречие,
        // оба означают «мимо всего», но обе записи нужны: снимут пин —
        // маршрут останется.
        foreach (var pinned in pins ?? [])
        {
            var name = Trim(pinned);

            if (name.Length > 0)
                entries.Add(new RouteEntry { Name = name, Choice = RouteChoice.Pin });
        }

        if (kept.Count > 0)
        {
            notes.Add($"правил по адресам: {kept.Count} — книгой не выражаются "
                + "и остаются как были. Они ловят соединения по адресу, без имени, "
                + "и доменным правилом такое не поймать.");
        }

        var book = new RouteBook
        {
            Entries = entries,
            Clashes = RouteClashes.Find(entries),
        };

        if (book.Clashes.Count > 0)
        {
            notes.Add($"противоречий найдено: {book.Clashes.Count}. "
                + "Они были и раньше — книга лишь называет их вслух.");
        }

        return new Migration { Book = book, Kept = kept, Notes = notes };
    }

    /// <summary>
    /// Собирает прежние правила обратно из книги.
    /// </summary>
    /// <remarks>
    /// Ради проверки, а не ради работы. Перенос, который нельзя обратить,
    /// проверяется только на глаз, а на глаз восемьдесят восемь правил
    /// не проверишь.
    /// </remarks>
    public static IReadOnlyList<RoutingRule> Back(RouteBook book)
    {
        var rules = new List<RoutingRule>();

        foreach (var entry in book.Entries)
        {
            // Пин правилом маршрутизации не был и не станет: он живёт
            // в hosts. Обратно он и не собирается.
            if (entry.Choice == RouteChoice.Pin)
                continue;

            rules.Add(new RoutingRule
            {
                Match = entry.IsGroup ? MatchKind.HostList : MatchKind.Domain,
                Value = entry.IsGroup ? $"config/lists/{entry.Name}.txt" : "*." + entry.Name,
                Mode = ModeOf(entry.Choice),
                Recipe = entry.Recipe,
            });
        }

        return rules;
    }

    /// <summary>
    /// Как правило называется в книге; <c>null</c> — не выражается.
    /// </summary>
    /// <remarks>
    /// Список каталога становится группой: <c>config/lists/discord.txt</c> —
    /// это <c>discord</c>. Ради этого группы и заводились: без них
    /// восемьдесят один список развернулся бы в несколько сотен строк
    /// отдельных имён.
    /// </remarks>
    public static string? NameOf(RoutingRule rule) => rule.Match switch
    {
        MatchKind.HostList => Trim(System.IO.Path.GetFileNameWithoutExtension(rule.Value)),
        MatchKind.Domain => Trim(rule.Value),

        // Правила по адресам и по именам программ — не про имена вовсе.
        _ => null,
    };

    public static RouteChoice ChoiceOf(RoutingMode mode) => mode switch
    {
        RoutingMode.Desync => RouteChoice.Desync,
        RoutingMode.Proxy => RouteChoice.Vpn,
        _ => RouteChoice.Direct,
    };

    public static RoutingMode ModeOf(RouteChoice choice) => choice switch
    {
        RouteChoice.Desync => RoutingMode.Desync,
        RouteChoice.Vpn => RoutingMode.Proxy,
        _ => RoutingMode.Direct,
    };

    private static string Trim(string value) =>
        value.Trim().Trim('"', '\'').TrimStart('*', '.').Trim();
}
