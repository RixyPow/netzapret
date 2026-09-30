namespace NetZapret.Core.Rules;

/// <summary>
/// Что держать мимо туннеля, когда полный перехват включён ради правила по программе.
/// </summary>
/// <remarks>
/// <para>
/// Правило «программа → VPN» заводит в туннель весь трафик машины
/// (<see cref="RuleSet.RoutesProgramIntoTunnel"/>): на Windows sing-box узнаёт
/// процесс только внутри туннеля, и выбрать программу иначе нельзя. Всё без
/// правила идёт из туннеля в <c>direct</c> — наружу оно выходит, но уже через
/// движок туннеля, а не своим путём.
/// </para>
/// <para>
/// Для игр это не всё равно. Обсуждение №11 (30.09): Valorant при маршрутах
/// в VPN «идёт через VPN», пинг прыгает до 200. Было ли у того человека правило
/// по программе, не установлено, — но при таком правиле игровой UDP Valorant
/// шёл бы через движок туннеля, хотя сама Riot стоит на десинке.
/// </para>
/// <para>
/// Поэтому сети, у которых есть свой адресный список и маршрут мимо VPN, при
/// таком перехвате выводятся из туннеля: они идут ровно как без правила
/// по программе. Пока это одна Riot (AS6507, <c>config/lists/riot-network.txt</c>):
/// у прочих игр списка адресов у нас нет, а по именам из туннеля не вывести.
/// </para>
/// </remarks>
public static class ProgramCapture
{
    /// <summary>Сети Riot — игровые серверы Valorant и League.</summary>
    public const string RiotNetwork = "config/lists/riot-network.txt";

    /// <summary>Список имён Riot, по маршруту которого решается, выводить ли её сети.</summary>
    private const string RiotNames = "riot-valorant.txt";

    /// <summary>
    /// Адресные списки, которые при полном перехвате ради программы идут мимо туннеля.
    /// </summary>
    /// <remarks>
    /// Riot — если её маршрут не «через VPN»: отправленную в VPN выводить
    /// из туннеля нельзя. Маршрут — первого совпавшего правила по её списку
    /// имён, как решает и движок. Правила нет — Riot идёт мимо VPN, и сети выводятся.
    /// </remarks>
    public static IReadOnlyList<string> KeepOut(RuleSet ruleSet)
    {
        if (!ruleSet.RoutesProgramIntoTunnel)
            return [];

        var riot = ruleSet.Rules.FirstOrDefault(r =>
            r.Enabled
            && r.Match == MatchKind.HostList
            && r.Value.Replace('\\', '/').EndsWith("/" + RiotNames, StringComparison.OrdinalIgnoreCase));

        return riot?.Mode == RoutingMode.Proxy ? [] : [RiotNetwork];
    }
}
