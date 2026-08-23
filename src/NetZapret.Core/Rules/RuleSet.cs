namespace NetZapret.Core.Rules;

/// <summary>
/// Полный набор правил маршрутизации + поведение по умолчанию.
/// </summary>
public sealed record RuleSet
{
    /// <summary>Правила в порядке вычисления (уже отсортированы по приоритету класса).</summary>
    public required IReadOnlyList<RoutingRule> Rules { get; init; }

    public required RoutingMode DefaultMode { get; init; }

    public string? DefaultServer { get; init; }

    /// <summary>Глобальный режим работы: он решает, вычисляются ли правила вообще.</summary>
    public OperatingMode Operating { get; init; } = OperatingMode.Selective;

    public static RuleSet Empty { get; } = new()
    {
        Rules = Array.Empty<RoutingRule>(),
        DefaultMode = RoutingMode.Direct,
        Operating = OperatingMode.Off,
    };
}

/// <summary>Ошибка в конфиге правил: неразбираемое значение, неизвестный режим и т.п.</summary>
public sealed class RuleConfigurationException : Exception
{
    public RuleConfigurationException(string message) : base(message) { }
    public RuleConfigurationException(string message, Exception inner) : base(message, inner) { }
}
