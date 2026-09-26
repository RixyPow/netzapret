using NetZapret.Core.Connections;

namespace NetZapret.Core.Rules;

/// <summary>
/// Прогоняет соединение через набор правил и возвращает решение.
/// </summary>
/// <remarks>
/// <para>
/// Приоритет — как задан в ТЗ: сперва все <see cref="MatchKind.Process"/>,
/// затем <see cref="MatchKind.Domain"/>, затем <see cref="MatchKind.Ip"/>,
/// затем default. Внутри класса сохраняется порядок из файла. Сортировка
/// делается один раз в <see cref="Build"/>, а не на каждом соединении.
/// </para>
/// <para>
/// Класс не имеет изменяемого состояния и рассчитан на вызов из нескольких
/// потоков: WFP-подписка отдаёт события из своего пула.
/// </para>
/// </remarks>
public sealed class RuleEngine
{
    private readonly RuleSet _ruleSet;
    private readonly bool _hasDomainRules;

    public RuleEngine(RuleSet ruleSet)
    {
        _ruleSet = ruleSet;
        _hasDomainRules = ruleSet.Rules.Any(r => r.Match == MatchKind.Domain);
    }

    public RuleSet RuleSet => _ruleSet;

    /// <summary>
    /// Собирает движок из сырого списка правил: валидирует, компилирует шаблоны
    /// и раскладывает по приоритету.
    /// </summary>
    public static RuleEngine Build(
        IEnumerable<RoutingRule> rules,
        RoutingMode defaultMode,
        string? defaultServer = null,
        OperatingMode operating = OperatingMode.Selective)
    {
        // Выключенные правила отбрасываются здесь: дальше по конвейеру они
        // только мешали бы — попадали в конфиг движка и в подсчёты.
        var ordered = rules.Where(r => r.Enabled).ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Ordinal = i;
            ordered[i].Compile();
        }

        // OrderBy устойчив в LINQ-to-objects, поэтому порядок внутри одной
        // группы останется таким же, как в файле. Сортировка двухуровневая:
        // сперва класс совпадения, затем слой — выбор человека проверяется
        // раньше заводской настройки того же класса.
        //
        // Третий уровень — своё имя раньше списка сервиса. Оба доменные
        // и оба выбор человека, и прежде решал порядок в файле: списки
        // сервисов пишутся книгой маршрутов выше, свои домены — ниже, и
        // «*.downloads.claude.ai → через VPN» молча проигрывал списку Claude
        // «напрямую», а «*.crunchyroll.com → через VPN» — списку Crunchyroll.
        // Решение владельца 23.09: своё правило побеждает. Имя, названное
        // человеком поимённо, — это и есть его выбор для этого имени.
        var sorted = ordered
            .OrderBy(r => Tier(r.Match, r.Source))
            .ToList();

        return new RuleEngine(new RuleSet
        {
            Rules = sorted,
            DefaultMode = defaultMode,
            DefaultServer = defaultServer,
            Operating = operating,
        });
    }

    /// <summary>
    /// Группа, внутри которой решает порядок в файле; меньше — раньше.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Три уровня сортировки из <see cref="Build"/> одним числом: класс
    /// совпадения, слой, своё имя раньше списка. Вынесено затем, чтобы
    /// перестановка в «Порядке вычисления» спрашивала то же, чем сортирует
    /// движок. Своя копия условия в окне разошлась бы с этой молча —
    /// и окно разрешало бы перетаскивать туда, где порядок ничего не решает.
    /// </para>
    /// </remarks>
    public static int Tier(MatchKind match, RuleSource source)
    {
        bool user = source == RuleSource.User;

        return MatchKindPriority.Of(match) * 4
            + (user ? 0 : 2)
            + (user && match == MatchKind.Domain ? 0 : 1);
    }

    /// <summary>
    /// Решает, куда направить соединение.
    /// </summary>
    public RuleDecision Evaluate(ConnectionEvent connection)
    {
        // Loopback и локальные сети никогда не уводим в туннель: для loopback это
        // гарантированная петля, если в TUN попадёт трафик самого sing-box, а для
        // LAN — потеря доступа к роутеру и принтерам.
        bool isLocal = connection.IsLoopback
            || (connection.RemoteAddress is not null && LocalNetworks.IsLocal(connection.RemoteAddress));

        if (isLocal)
        {
            return new RuleDecision
            {
                Mode = RoutingMode.Direct,
                Rule = null,
                Reason = connection.IsLoopback ? "loopback (жёсткое исключение)" : "локальная сеть (жёсткое исключение)",
            };
        }

        switch (_ruleSet.Operating)
        {
            case OperatingMode.Off:
                return new RuleDecision
                {
                    Mode = RoutingMode.Direct,
                    Rule = null,
                    Reason = "режим off — правила не вычисляются",
                };

            // «Без исключений» — единственный режим, где правила не смотрятся
            // вовсе. Прежде так вёл себя ProxyAll, то есть «кроме РФ»: он
            // отправлял в туннель и российские сервисы, ради исключения
            // которых и заведён. А ProxyStrict, чьё назначение как раз
            // не знать исключений, правила читал. Названия и поведение
            // стояли наоборот.
            case OperatingMode.ProxyStrict:
                return new RuleDecision
                {
                    Mode = RoutingMode.Proxy,
                    Rule = null,
                    Server = _ruleSet.DefaultServer,
                    Reason = "режим «всё через VPN без исключений» — правила не смотрятся",
                };
        }

        bool sawUnevaluableDomain = false;

        foreach (var rule in _ruleSet.Rules)
        {
            if (rule.Match == MatchKind.Domain && connection.Hostname is null)
            {
                sawUnevaluableDomain = true;
                continue;
            }

            if (!rule.Matches(connection, out var reason))
                continue;

            // В «кроме РФ» десинк уступает туннелю. Правило, написанное ради
            // обхода, в этом режиме означает лишь «этот сервис закрыт», а как
            // его открывать, решает режим: туннель справляется со всем, с чем
            // справляется десинк, и ещё с отказом по стране. Исключения —
            // правила «напрямую», ради которых режим и назван «кроме РФ».
            var mode = _ruleSet.Operating == OperatingMode.ProxyAll && rule.Mode == RoutingMode.Desync
                ? RoutingMode.Proxy
                : rule.Mode;

            return new RuleDecision
            {
                Mode = mode,
                Rule = rule,
                Server = mode == RoutingMode.Proxy ? rule.Server ?? _ruleSet.DefaultServer : null,
                Reason = reason,
                HadUnevaluableDomainRules = sawUnevaluableDomain,
            };
        }

        return RuleDecision.FromDefault(_ruleSet, sawUnevaluableDomain);
    }

    /// <summary>Есть ли в наборе доменные правила — от этого зависит, нужен ли источник имён.</summary>
    public bool RequiresHostnames => _hasDomainRules;

    /// <summary>Возвращает движок с заменённой секцией перехвата.</summary>
    internal RuleEngine WithCapture(IReadOnlyList<string> capture) =>
        new(_ruleSet with { CaptureEntries = capture });
}
