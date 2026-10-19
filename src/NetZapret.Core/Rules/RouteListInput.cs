using System.Text.RegularExpressions;
using NetZapret.Core.Connections;

namespace NetZapret.Core.Rules;

/// <summary>Что вышло из вставленного списка и что с ним будет.</summary>
public sealed record RouteListPlan
{
    /// <summary>Что запишется: имена сайтов (без <c>*.</c>) и подсети, по порядку вставки.</summary>
    public IReadOnlyList<string> Sites { get; init; } = [];

    /// <summary>Подсети и адреса — правилом <c>ip</c>.</summary>
    public IReadOnlyList<string> Subnets { get; init; } = [];

    /// <summary>Из записываемых — те, что сейчас ведёт правило сервиса другим путём: своё его перекроет.</summary>
    public IReadOnlyList<string> Overrides { get; init; } = [];

    /// <summary>Уже есть своё правило — путь меняется в его строке.</summary>
    public IReadOnlyList<string> AlreadyOwn { get; init; } = [];

    /// <summary>Уже идут выбранным путём по правилу сервиса — своё правило было бы двойником.</summary>
    public IReadOnlyList<string> SameRoute { get; init; } = [];

    /// <summary>Имена самой программы — прокси Telegram, проверка сети Windows.</summary>
    public IReadOnlyList<string> ProgramOwn { get; init; } = [];

    /// <summary>Не похоже ни на сайт, ни на адрес.</summary>
    public IReadOnlyList<string> NotNames { get; init; } = [];

    /// <summary>Сколько всего запишется.</summary>
    public int Count => Sites.Count + Subnets.Count;
}

/// <summary>
/// Список сайтов, вставленный в «Маршруты» разом (идея №14, владелец 19.10: «делай»).
/// </summary>
/// <remarks>
/// <para>
/// Откуда взялось: 10.10 владелец скопировал из «Наблюдения» 77 имён и спросил,
/// удобно ли вписывать их в «Маршруты». Поле принимало одно имя, а список —
/// только по одному.
/// </para>
/// <para>
/// Один путь на всю вставку: выбор на каждую строку — снова сорок решений,
/// а поправить отдельное потом можно в его строке. Разбор имени тот же, что
/// у поля «Маршрутов» (<see cref="DomainInput"/>, <see cref="AddressInput"/>).
/// </para>
/// </remarks>
public static class RouteListInput
{
    /// <summary>
    /// Зоны подставных имён прокси Telegram (tg-ws-proxy): <c>kwsN.&lt;зона&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Из журналов наблюдения владельца, 10.10–19.10: процесс tg-ws-proxy.exe
    /// ходил ровно на эти зоны, и больше их не открывал никто. Открытым текстом
    /// в самом tg-ws-proxy их нет. Маршрут на них уведёт прокси Telegram мимо
    /// того пути, на котором он работает.
    /// </remarks>
    private static readonly string[] TelegramProxyZones =
    [
        "pclead.co.uk", "offshor.co.uk", "cakeisalie.co.uk", "noskomnadzor.co.uk", "notelega.co.uk",
        "kartoshka.co.uk", "sorokdva.co.uk", "sorokodin.co.uk", "lovetrue.co.uk",
        "pyatdesyatdva.co.uk", "pyatdesyatodin.co.uk",
    ];

    /// <summary>Проверка сети Windows: по ней система решает, есть ли интернет.</summary>
    private static readonly string[] WindowsChecks = ["msftncsi.com", "msftconnecttest.com"];

    private static readonly Regex TelegramProxyName = new(@"^kws\d+\.[a-z0-9-]+\.co\.uk$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Части вставленного: строки, запятые, точки с запятой, пробелы. Комментарий
    /// с <c>#</c> до конца строки отбрасывается — так пишутся наши списки.
    /// </summary>
    public static IReadOnlyList<string> Split(string? text) =>
        [.. (text ?? string.Empty)
            .Split('\n')
            .Select(line => line.IndexOf('#') is var at and >= 0 ? line[..at] : line)
            .SelectMany(line => line.Split([',', ';', ' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)];

    /// <summary>Список ли это, а не одно имя: две части и больше.</summary>
    public static bool IsList(string? text) => Split(text).Count >= 2;

    /// <summary>Имя самой программы — его в маршруты не пускаем.</summary>
    public static bool IsProgramOwn(string name)
    {
        var host = name.Trim().TrimEnd('.').ToLowerInvariant();

        return TelegramProxyName.IsMatch(host)
            || TelegramProxyZones.Concat(WindowsChecks).Any(zone => host == zone || host.EndsWith("." + zone, StringComparison.Ordinal));
    }

    /// <summary>
    /// Что запишется из вставленного и почему остальное — нет.
    /// </summary>
    /// <param name="engine">
    /// Движок правил последней сборки — тот же, что у <c>nz where</c>: он
    /// говорит, какое правило ведёт имя сейчас. <c>null</c> — не собран;
    /// тогда своё правило ищется только по точному значению в <paramref name="own"/>.
    /// </param>
    /// <param name="own">Свои правила файла — для случая без движка.</param>
    /// <param name="chosen">Путь, выбранный для всей вставки.</param>
    /// <param name="toSite">
    /// Сводить имя к сайту (<see cref="WatchEntry.SiteOf"/>): длинные имена CDN
    /// меняются, и правило на точное имя завтра ни с чем не совпало бы.
    /// </param>
    public static RouteListPlan Plan(
        string? text,
        RuleEngine? engine,
        IReadOnlyList<UserRuleEntry> own,
        RoutingMode chosen,
        bool toSite = true)
    {
        var sites = new List<string>();
        var subnets = new List<string>();
        var overrides = new List<string>();
        var already = new List<string>();
        var same = new List<string>();
        var program = new List<string>();
        var notNames = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in Split(text))
        {
            string value;
            bool subnet = false;

            if (AddressInput.Normalize(part) is { } address)
            {
                value = address;
                subnet = true;
            }
            else if (DomainInput.Normalize(part) is { } name)
            {
                value = toSite ? WatchEntry.SiteOf(name) : name;
            }
            else
            {
                if (seen.Add("?" + part))
                    notNames.Add(part);

                continue;
            }

            if (!seen.Add(value))
                continue;

            if (!subnet && IsProgramOwn(value))
            {
                program.Add(value);
                continue;
            }

            switch (Judge(value, subnet, engine, own, chosen))
            {
                case Verdict.Own:
                    already.Add(value);
                    continue;
                case Verdict.Same:
                    same.Add(value);
                    continue;
                case Verdict.Override:
                    overrides.Add(value);
                    break;
            }

            (subnet ? subnets : sites).Add(value);
        }

        return new RouteListPlan
        {
            Sites = sites,
            Subnets = subnets,
            Overrides = overrides,
            AlreadyOwn = already,
            SameRoute = same,
            ProgramOwn = program,
            NotNames = notNames,
        };
    }

    /// <summary>
    /// Записывает план в файл своих правил — тем же видом, что поле «Маршрутов»:
    /// сайт — <c>*.имя</c>, адрес — правилом <c>ip</c>. Без рецепта: десинк
    /// без рецепта решает пресет. Сохраняет вызывающий.
    /// </summary>
    /// <returns>Записанные значения правил по порядку.</returns>
    public static IReadOnlyList<(MatchKind Match, string Value)> Write(RouteListPlan plan, RoutingMode mode, UserRulesFile file)
    {
        var written = new List<(MatchKind, string)>();

        foreach (var site in plan.Sites)
        {
            file.Set(MatchKind.Domain, "*." + site, mode, recipe: null);
            written.Add((MatchKind.Domain, "*." + site));
        }

        foreach (var subnet in plan.Subnets)
        {
            file.Set(MatchKind.Ip, subnet, mode, recipe: null);
            written.Add((MatchKind.Ip, subnet));
        }

        return written;
    }

    private enum Verdict { New, Override, Own, Same }

    private static Verdict Judge(string value, bool subnet, RuleEngine? engine, IReadOnlyList<UserRuleEntry> own, RoutingMode chosen)
    {
        if (engine is null)
        {
            var exact = subnet ? value : "*." + value;

            return own.Any(e => e.Match == (subnet ? MatchKind.Ip : MatchKind.Domain)
                    && string.Equals(e.Value, exact, StringComparison.OrdinalIgnoreCase))
                ? Verdict.Own
                : Verdict.New;
        }

        // Подсеть спрашивается своим началом: правило по адресу, накрывшее
        // начало, накрывает и сеть — точнее без перебора адресов не сказать.
        var target = subnet ? value.Split('/')[0] : value;
        var decision = engine.Evaluate(ConnectionEvent.Describe(target));

        if (decision.Rule is not { } rule || rule.Source == RuleSource.Fallback)
            return Verdict.New;

        // Своё — домен и адрес человека и его списки; часть сервиса, которой
        // он выбрал путь, тоже записана им, но своим правилом не считается.
        bool mine = rule.Source == RuleSource.User
            && (rule.Match is MatchKind.Domain or MatchKind.Ip
                || (rule.Match == MatchKind.HostList && OwnLists.IsOwn(rule.Value)));

        if (mine)
            return Verdict.Own;

        return decision.Mode == chosen ? Verdict.Same : Verdict.Override;
    }
}
