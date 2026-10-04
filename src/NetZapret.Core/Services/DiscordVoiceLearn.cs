using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using NetZapret.Core.Rules;

namespace NetZapret.Core.Services;

/// <summary>
/// Адреса голоса Discord — из журнала самого Discord в список части «Звук голоса (адреса)».
/// </summary>
/// <remarks>
/// <para>
/// Звук идёт UDP на голые адреса, которые Discord сообщает клиенту без DNS,
/// и в туннель он попадает только адресом из списка
/// (<see cref="ListPath"/>, ServicePart.ByAddress). 02.10 адрес нашёлся
/// в журнале Discord — строка «Creating connection to адрес:порт» в
/// <c>renderer_js.log</c>: до 30.09 звук шёл на Google Cloud 35.217.x,
/// с 01.10 — на Cloudflare 104.29.136–159.x. Discord меняет серверы, и
/// список отставал: у друга владельца 04.10 голос снова не шёл.
/// </para>
/// <para>
/// Дописывается сеть /20 (<see cref="LearnPrefix"/>), а не адрес и не /24.
/// Замер по журналу владельца 04.10: 91 адрес голоса, из них Google Cloud —
/// в 31 разной /24 от 35.217.6 до 35.217.63. По /24 почти каждый новый
/// звонок на Google был бы промахом — сорванный звонок и перезапуск; /20
/// закрывает тот же разброс четырьмя строками. Лишнее в такой сети — чужие
/// сайты — пойдёт через VPN и откроется: промах дороже.
/// Дописанное переносится при обновлении, как любая дописка в наши списки
/// (<see cref="Updates.ListCarry"/>). Только IPv4: адресов IPv6 в журнале
/// не встречалось.
/// </para>
/// </remarks>
public static class DiscordVoiceLearn
{
    /// <summary>Список части «Звук голоса (адреса)» — тот же, что в каталоге.</summary>
    public const string ListPath = "config/lists/discord-voice-net.txt";

    /// <summary>Журналы клиента: обычный Discord, PTB и Canary.</summary>
    public static IReadOnlyList<string> LogPaths(string? appData = null)
    {
        var root = appData ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        return new[] { "discord", "discordptb", "discordcanary" }
            .Select(client => Path.Combine(root, client, "logs", "renderer_js.log"))
            .ToList();
    }

    private static readonly Regex Connection = new(
        @"Creating connection to (\d{1,3}(?:\.\d{1,3}){3}):\d+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Адреса голосовых серверов из текста журнала, по порядку и без повторов.</summary>
    public static IReadOnlyList<IPAddress> Addresses(string log) =>
        Connection.Matches(log)
            .Select(m => IPAddress.TryParse(m.Groups[1].Value, out var address) ? address : null)
            .OfType<IPAddress>()
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToList();

    /// <summary>Длина префикса дописываемой сети.</summary>
    public const int LearnPrefix = 20;

    /// <summary>Сети /<see cref="LearnPrefix"/> для адресов, которых список не покрывает.</summary>
    public static IReadOnlyList<string> Missing(IEnumerable<IPAddress> addresses, IEnumerable<string> listLines)
    {
        var ranges = new List<IpCidrRange>();

        foreach (var line in listLines)
        {
            var text = line.Split('#')[0].Trim();

            if (text.Length == 0)
                continue;

            try
            {
                ranges.Add(IpCidrRange.Parse(text));
            }
            catch (RuleConfigurationException)
            {
                // Чужая строка в списке — не наша забота; её и движок пропустит.
            }
        }

        var missing = new List<string>();

        foreach (var address in addresses)
        {
            if (ranges.Any(r => r.Contains(address)))
                continue;

            var bytes = address.GetAddressBytes();
            uint value = (uint)bytes[0] << 24 | (uint)bytes[1] << 16 | (uint)bytes[2] << 8 | bytes[3];
            uint start = value & ~(uint.MaxValue >> LearnPrefix);
            var network = $"{start >> 24}.{(start >> 16) & 255}.{(start >> 8) & 255}.{start & 255}/{LearnPrefix}";

            // Сразу в покрытое: два адреса одной сети — одна строка.
            ranges.Add(IpCidrRange.Parse(network));
            missing.Add(network);
        }

        return missing;
    }

    /// <summary>
    /// Дописывает в список сети для адресов из журнала, которых в нём нет.
    /// </summary>
    /// <returns>Дописанные сети; пусто — всё и так покрыто.</returns>
    public static IReadOnlyList<string> Learn(string log, string? listPath = null, DateTime? when = null)
    {
        var path = listPath ?? ListPath;
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        var missing = Missing(Addresses(log), lines);

        if (missing.Count == 0)
            return [];

        var text = new StringBuilder();

        if (lines.Length > 0 && File.ReadAllText(path) is { Length: > 0 } existing && !existing.EndsWith('\n'))
            text.AppendLine();

        text.AppendLine($"# из журнала Discord, {(when ?? DateTime.Now):dd.MM.yyyy}");

        foreach (var network in missing)
            text.AppendLine(network);

        File.AppendAllText(path, text.ToString());

        return missing;
    }

    /// <summary>
    /// Стоит ли голос «через VPN» — только тогда дописанное что-то меняет.
    /// </summary>
    /// <remarks>
    /// На десинке или «напрямую» список голоса в туннель не ведёт, и молча
    /// расширять его незачем: это не та дорога, которую человек выбрал.
    /// </remarks>
    public static bool RoutedToVpn(IEnumerable<UserRuleEntry> rules) =>
        rules.FirstOrDefault(e => e.Enabled && e.Match == MatchKind.IpSet && e.Matches(ListPath))?.Mode
            == RoutingMode.Proxy;
}

/// <summary>
/// Новые строки журналов — с того места, где остановились в прошлый раз.
/// </summary>
/// <remarks>
/// Журнал Discord — мегабайты (у владельца 04.10 — 5,8 МБ), и перечитывать
/// его целиком раз в полминуты незачем. В первый раз берётся хвост: в нём
/// последние звонки, а старые адреса — уже ушедшие серверы. Файл стал короче —
/// Discord начал журнал заново, читаем с начала.
/// </remarks>
public sealed class LogTail
{
    /// <summary>Сколько брать в первый раз.</summary>
    public const int FirstTail = 512 * 1024;

    private readonly Dictionary<string, long> _read = new(StringComparer.OrdinalIgnoreCase);

    public string ReadNew(IEnumerable<string> paths)
    {
        var text = new StringBuilder();

        foreach (var path in paths)
        {
            try
            {
                if (!File.Exists(path))
                    continue;

                // Discord держит файл открытым на запись — читаем, не мешая ему.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                long length = stream.Length;
                long from = _read.TryGetValue(path, out var seen) && seen <= length
                    ? seen
                    : Math.Max(0, length - FirstTail);

                stream.Seek(from, SeekOrigin.Begin);

                var bytes = new byte[length - from];
                int got = 0;

                while (got < bytes.Length && stream.Read(bytes, got, bytes.Length - got) is > 0 and var n)
                    got += n;

                // До последнего перевода строки: строку, которую Discord дописывает
                // прямо сейчас, разрезало бы пополам, и адрес в ней пропал бы.
                int end = Array.LastIndexOf(bytes, (byte)'\n', Math.Max(0, got - 1));

                if (end < 0)
                    continue;

                text.Append(Encoding.UTF8.GetString(bytes, 0, end + 1));
                _read[path] = from + end + 1;
            }
            catch (IOException)
            {
                // Занят или пропал посреди чтения — заглянем в следующий раз.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return text.ToString();
    }
}
