using NetZapret.Core.Rules;

namespace NetZapret.Core.Services;

/// <summary>Игра, чьи серверы дописываются в список своей части по «Наблюдению».</summary>
/// <param name="Name">Название — для журнала.</param>
/// <param name="Program">Имя файла процесса игры.</param>
/// <param name="List">Список части «Игровые серверы (адреса)».</param>
/// <param name="On">Выключатель в настройках.</param>
public sealed record LearnedGame(string Name, string Program, string List, Func<AppSettings, bool> On);

/// <summary>
/// Серверы игр, которых не называет журнал игры, — из соединений её процесса (владелец 21.10).
/// </summary>
/// <remarks>
/// <para>
/// Roblox и голос Discord пишут адреса серверов в свои журналы
/// (<see cref="RobloxServerLearn"/>, <see cref="DiscordVoiceLearn"/>). У ARC Raiders
/// такого журнала мы не знаем — игры нет ни у кого из нас, — а матч у неё тоже UDP
/// на голые адреса в Google Cloud, и в туннель он попадает лишь адресом из списка
/// (обсуждение №32). Зато «Наблюдение» видит соединения процесса: дописывает их
/// <see cref="Connections.ProgramRouteLearner"/> тем же порядком, что маршруты
/// программ, — адрес без имени сетью /24. Соединения с именем — мимо: имена игры
/// лежат в части по именам, а на адресе CDN имя бывает чужим.
/// </para>
/// <para>
/// Только пока часть стоит «через VPN»: в остальных путях список адресов игры
/// ничего не меняет, а «Наблюдение» ради него шло бы впустую. Наблюдение
/// начинается само, пока игра запущена (<c>AutoFillWatch</c> окна).
/// </para>
/// </remarks>
public static class GameServerLearn
{
    /// <summary>Список части «Игровые серверы (адреса)» ARC Raiders.</summary>
    public const string ArcRaidersList = "config/lists/arc-raiders-network.txt";

    public static IReadOnlyList<LearnedGame> All { get; } =
    [
        // PioneerGame — рабочее имя игры у Embark (бэкенд es-pio.net), процесс
        // лежит в PioneerGame\Binaries\Win64.
        new("ARC Raiders", "PioneerGame.exe", ArcRaidersList, s => s.LearnArcRaidersServers),
    ];

    /// <summary>Игры, чьи серверы дописываются сейчас: выключатель включён, часть «через VPN».</summary>
    public static IReadOnlyList<LearnedGame> Active(AppSettings settings, IReadOnlyList<UserRuleEntry> rules) =>
        All.Where(g => g.On(settings) && RoutedToVpn(rules, g.List)).ToList();

    /// <summary>Стоит ли список адресов «через VPN» — первым совпавшим правилом.</summary>
    public static bool RoutedToVpn(IEnumerable<UserRuleEntry> rules, string list) =>
        rules.FirstOrDefault(e => e.Enabled && e.Match == MatchKind.IpSet && e.Matches(list))?.Mode
            == RoutingMode.Proxy;
}
