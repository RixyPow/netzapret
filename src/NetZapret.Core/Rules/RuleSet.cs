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

    /// <summary>
    /// Что дополнительно заводить в туннель при выборочном перехвате.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Нужно приложениям, которые ходят по голым адресам без DNS — например
    /// Telegram к своим дата-центрам. Им fakeip не выдаётся, значит и в туннель
    /// они не попадают, и правило <c>match: process</c> с <c>mode: proxy</c>
    /// никогда не сработает: sing-box узнаёт процесс уже внутри туннеля,
    /// а туда ещё надо попасть.
    /// </para>
    /// <para>
    /// Записи — либо CIDR, либо путь к списку адресов Zapret
    /// (<c>lists/ipset-telegram.txt</c>). Второе удобнее: списки уже собраны
    /// и обновляются вместе с Zapret.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> CaptureEntries { get; init; } = Array.Empty<string>();

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
