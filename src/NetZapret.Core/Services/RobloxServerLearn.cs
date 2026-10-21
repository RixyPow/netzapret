using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NetZapret.Core.Rules;

namespace NetZapret.Core.Services;

/// <summary>
/// Адреса игровых серверов Roblox — из журнала самого Roblox в список части
/// «Игровые серверы (адреса)», по образцу голоса Discord (<see cref="DiscordVoiceLearn"/>).
/// </summary>
/// <remarks>
/// <para>
/// Матч Roblox — UDP на голый адрес сервера плейса, без DNS, и в туннель он
/// попадает только адресом из списка (<see cref="ListPath"/>). В списке — сети
/// самой Roblox (AS22697), но часть плейсов Roblox поднимает на Amazon.
/// 21.10 у владельца (журнал Roblox, «Гибрид», Roblox «через VPN»): входы на
/// 18.159.131.236, 54.93.176.232 и 63.178.198.155 — Amazon во Франкфурте, вне
/// списка, — шли напрямую и через 17 с после «Connection accepted» молчали:
/// «AckTimeout: Last Packet received time: 20003 ms», ошибка 277. Те же
/// сутки на сетях Roblox через VPN — входили. Так же было в обсуждении №16:
/// ошибка 279, пока матч шёл мимо туннеля.
/// </para>
/// <para>
/// Адрес — из строк «Connection accepted from адрес|порт» (с кем на деле
/// пошёл матч) и «! Joining game … at адрес» (куда шли — успевает и тогда,
/// когда сервер не ответил вовсе). У серверов Roblox в «Joining» — внутренний
/// адрес RCC (10.x), он отбрасывается: в туннель вести нечего. Дописывается
/// сеть /20, как у голоса Discord (<see cref="DiscordVoiceLearn.LearnPrefix"/>):
/// машины плейсов на Amazon — разные адреса из больших сетей, и по одному
/// адресу почти каждый следующий вход был бы промахом. Лишнее в такой сети —
/// чужие сайты на Amazon — пойдёт через VPN и откроется: промах дороже.
/// Только IPv4.
/// </para>
/// </remarks>
public static class RobloxServerLearn
{
    /// <summary>Список части «Игровые серверы (адреса)» — тот же, что в каталоге.</summary>
    public const string ListPath = "config/lists/roblox-network.txt";

    /// <summary>Сколько дней журналов смотреть: старые — уже ушедшие серверы.</summary>
    public static readonly TimeSpan LogAge = TimeSpan.FromDays(3);

    /// <summary>
    /// Журналы игрока: <c>%LOCALAPPDATA%\Roblox\logs\*_Player_*.log</c> без журналов
    /// падений и Studio, моложе <see cref="LogAge"/>.
    /// </summary>
    /// <remarks>Новый файл на каждый запуск игры — список собирается заново на каждом круге.</remarks>
    public static IReadOnlyList<string> LogPaths(string? localAppData = null, DateTime? now = null)
    {
        var folder = Path.Combine(
            localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox", "logs");

        if (!Directory.Exists(folder))
            return [];

        var since = (now ?? DateTime.Now) - LogAge;

        try
        {
            return Directory.EnumerateFiles(folder, "*_Player_*.log")
                .Where(p => !Path.GetFileName(p).Contains("CrashHandler", StringComparison.OrdinalIgnoreCase))
                .Where(p => File.GetLastWriteTime(p) >= since)
                .OrderBy(File.GetLastWriteTime)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    // «… Info [DFLog::NetworkClient] Connection accepted from 18.159.131.236|54162»
    // и «… [FLog::Output] ! Joining game '…' place 139386728420706 at 18.159.131.236» —
    // журнал владельца 21.10.
    private static readonly Regex Server = new(
        @"(?:Connection accepted from|! Joining game '[^']*' place \d+ at) (\d{1,3}(?:\.\d{1,3}){3})\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Публичные адреса серверов из текста журнала, по порядку и без повторов.</summary>
    public static IReadOnlyList<IPAddress> Addresses(string log) =>
        Server.Matches(log)
            .Select(m => IPAddress.TryParse(m.Groups[1].Value, out var address) ? address : null)
            .OfType<IPAddress>()
            .Where(IsPublic)
            .Distinct()
            .ToList();

    /// <summary>Не частный, не служебный адрес IPv4 — такой можно вести в туннель.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();

        return !(b[0] is 0 or 10 or 127 or >= 224
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>
    /// Дописывает в список сети для адресов из журнала, которых в нём нет.
    /// </summary>
    /// <returns>Дописанные сети; пусто — всё и так покрыто.</returns>
    public static IReadOnlyList<string> Learn(string log, string? listPath = null, DateTime? when = null)
    {
        var path = listPath ?? ListPath;
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        var missing = DiscordVoiceLearn.Missing(Addresses(log), lines);

        LearnedNetworks.Append(path, missing, "из журнала Roblox", when);

        return missing;
    }

    /// <summary>
    /// Стоят ли игровые серверы «через VPN» — только тогда дописанное что-то меняет.
    /// </summary>
    /// <remarks>
    /// На десинке или «напрямую» список в туннель не ведёт, и молча расширять его
    /// незачем: это не та дорога, которую человек выбрал.
    /// </remarks>
    public static bool RoutedToVpn(IEnumerable<UserRuleEntry> rules) =>
        rules.FirstOrDefault(e => e.Enabled && e.Match == MatchKind.IpSet && e.Matches(ListPath))?.Mode
            == RoutingMode.Proxy;
}

/// <summary>Дописка выученных сетей в наш список — общая для сторожей журналов.</summary>
public static class LearnedNetworks
{
    /// <summary>
    /// Дописывает сети в конец списка под строкой «# откуда, дата»; пусто — ничего.
    /// </summary>
    /// <remarks>
    /// Файл не переписывается — только дописывается: строки человека и наши
    /// комментарии остаются как были, а дописанное переносится при обновлении,
    /// как любая дописка в наши списки (<see cref="Updates.ListCarry"/>).
    /// </remarks>
    public static void Append(string path, IReadOnlyList<string> networks, string source, DateTime? when = null)
    {
        if (networks.Count == 0)
            return;

        var text = new StringBuilder();

        if (File.Exists(path) && File.ReadAllText(path) is { Length: > 0 } existing && !existing.EndsWith('\n'))
            text.AppendLine();

        text.AppendLine($"# {source}, {(when ?? DateTime.Now):dd.MM.yyyy}");

        foreach (var network in networks)
            text.AppendLine(network);

        File.AppendAllText(path, text.ToString());
    }
}
