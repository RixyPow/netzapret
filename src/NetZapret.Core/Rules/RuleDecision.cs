namespace NetZapret.Core.Rules;

/// <summary>
/// Результат прогона соединения через набор правил.
/// </summary>
public readonly record struct RuleDecision
{
    public required RoutingMode Mode { get; init; }

    /// <summary>Сработавшее правило либо <c>null</c>, если применён default.</summary>
    public RoutingRule? Rule { get; init; }

    public string? Server { get; init; }

    /// <summary>Пояснение для лога: что именно совпало.</summary>
    public string? Reason { get; init; }

    /// <summary>
    /// Правила <see cref="MatchKind.Domain"/>, которые не удалось проверить,
    /// потому что доменное имя для адреса неизвестно.
    /// </summary>
    /// <remarks>
    /// Отдельный флаг, а не молчаливый промах: в PoC это ожидаемое состояние,
    /// и без него лог «сработал default» невозможно отличить от «мы просто
    /// не смогли посмотреть». Ровно эту дыру закрывает fakeip.
    /// </remarks>
    public bool HadUnevaluableDomainRules { get; init; }

    public static RuleDecision FromDefault(RuleSet ruleSet, bool unevaluableDomains) => new()
    {
        Mode = ruleSet.DefaultMode,
        Rule = null,
        Server = ruleSet.DefaultServer,
        Reason = "default",
        HadUnevaluableDomainRules = unevaluableDomains,
    };
}
