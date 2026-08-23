namespace NetZapret.Core.Rules;

/// <summary>
/// По какому признаку правило сопоставляется с соединением.
/// Порядок значений задаёт приоритет класса правил (меньше — раньше).
/// </summary>
public enum MatchKind
{
    /// <summary>
    /// Имя или путь исполняемого файла. Определяется из WFP appId,
    /// DNS-мост для этого не нужен.
    /// </summary>
    Process = 0,

    /// <summary>
    /// Доменное имя. Требует источника имён (fakeip-таблица sing-box,
    /// sniffing SNI или DNS-наблюдатель).
    /// </summary>
    Domain = 1,

    /// <summary>
    /// IP-адрес или CIDR-подсеть назначения.
    /// </summary>
    Ip = 2,
}
