namespace NetZapret.Core.Rules;

/// <summary>
/// По какому признаку правило сопоставляется с соединением.
/// </summary>
/// <remarks>
/// Приоритет задаётся не значением, а <see cref="MatchKindPriority.Of"/>:
/// <see cref="Ip"/> и <see cref="IpSet"/> — это одно и то же по сути,
/// различаются лишь способом записи, и приоритет у них общий.
/// </remarks>
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

    /// <summary>
    /// Список адресов из файла — например <c>lists/ipset-ru.txt</c> из Zapret.
    /// </summary>
    /// <remarks>
    /// Приоритет тот же, что у <see cref="Ip"/>: это он и есть, только заданный
    /// файлом. Списки Zapret уже собраны и обновляются вместе с ним, поэтому
    /// ссылаться на них надёжнее, чем копировать сотни подсетей в свой конфиг.
    /// </remarks>
    IpSet = 3,

    /// <summary>
    /// Список доменов из файла — например <c>lists/discord-media.txt</c>.
    /// </summary>
    /// <remarks>
    /// Приоритет тот же, что у <see cref="Domain"/>: это он и есть, только
    /// заданный файлом. Нужен, чтобы говорить о сервисе целиком, не переписывая
    /// его домены к себе: списки Zapret собраны и обновляются вместе с ним,
    /// а копия устареет молча. На нём же держится вид по сервисам — «голос
    /// Discord» это <c>lists/discord-media.txt</c>, а не горсть строк,
    /// выписанных однажды и с тех пор не проверявшихся.
    /// </remarks>
    HostList = 4,
}

/// <summary>
/// Приоритет класса правил: меньше — проверяется раньше.
/// </summary>
public static class MatchKindPriority
{
    public static int Of(MatchKind kind) => kind switch
    {
        MatchKind.Process => 0,
        MatchKind.Domain or MatchKind.HostList => 1,
        _ => 2,
    };
}
