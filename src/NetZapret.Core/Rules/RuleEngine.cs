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
        var ordered = rules.ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Ordinal = i;
            ordered[i].Compile();
        }

        // OrderBy устойчив в LINQ-to-objects, поэтому порядок внутри одного
        // MatchKind останется таким же, как в файле.
        var sorted = ordered.OrderBy(r => (int)r.Match).ToList();

        return new RuleEngine(new RuleSet
        {
            Rules = sorted,
            DefaultMode = defaultMode,
            DefaultServer = defaultServer,
            Operating = operating,
        });
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

            case OperatingMode.ProxyAll:
                return new RuleDecision
                {
                    Mode = RoutingMode.Proxy,
                    Rule = null,
                    Server = _ruleSet.DefaultServer,
                    Reason = "режим proxy — весь трафик в туннель",
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

            return new RuleDecision
            {
                Mode = rule.Mode,
                Rule = rule,
                Server = rule.Mode == RoutingMode.Proxy ? rule.Server ?? _ruleSet.DefaultServer : null,
                Reason = reason,
                HadUnevaluableDomainRules = sawUnevaluableDomain,
            };
        }

        return RuleDecision.FromDefault(_ruleSet, sawUnevaluableDomain);
    }

    /// <summary>Есть ли в наборе доменные правила — от этого зависит, нужен ли источник имён.</summary>
    public bool RequiresHostnames => _hasDomainRules;
}
