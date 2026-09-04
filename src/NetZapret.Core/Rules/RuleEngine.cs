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
        var sorted = ordered
            .OrderBy(r => MatchKindPriority.Of(r.Match))
            .ThenBy(r => r.Source == RuleSource.User ? 0 : 1)
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
