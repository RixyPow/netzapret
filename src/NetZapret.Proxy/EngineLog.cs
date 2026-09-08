using System.Text.RegularExpressions;

namespace NetZapret.Proxy;

/// <summary>Что движок сказал про попытку соединения.</summary>
public sealed record EngineComplaint
{
    /// <summary>Имя, к которому шли.</summary>
    public required string Host { get; init; }

    /// <summary>Через какой выход шли — тег из группы.</summary>
    public required string Outbound { get; init; }

    /// <summary>Чем кончилось, словами движка.</summary>
    public required string Error { get; init; }
}

/// <summary>
/// Читает журнал sing-box и достаёт оттуда причину недоставки.
/// </summary>
/// <remarks>
/// <para>
/// Самый прямой источник, который у нас есть, и до сих пор мы в него
/// не смотрели. Проба снаружи видит, что соединение не встало; движок изнутри
/// знает, к какому имени шёл, через какой выход и что ответила труба.
/// Разница между «туннель не доставил» и «через 🇺🇸 США: timeout: no recent
/// network activity» — это разница между догадкой и починкой.
/// </para>
/// <para>
/// Строка вида
/// <c>ERROR ... connection: open connection to web.whatsapp.com:443 using
/// outbound/hysteria2[🇺🇸 Hysteria2 | США]: timeout: no recent network
/// activity</c> отвечает сразу на два вопроса, которые мы гадали неделю:
/// имя из рукопожатия движок разобрал верно — значит десинк его не портил, —
/// а выход, через который шло, оказался не тем, который назван в шапке.
/// </para>
/// <para>
/// Читается хвост, а не весь файл: журнал вырастает до мегабайтов за сутки,
/// а нас занимает только что было сейчас.
/// </para>
/// </remarks>
public static class EngineLog
{
    /// <summary>Где движок обычно ведёт журнал.</summary>
    public static string DefaultPath => Path.Combine("runtime", "sing-box.log");

    /// <summary>Сколько последних строк смотреть.</summary>
    private const int Tail = 4000;

    /// <summary>
    /// Строка неудачи: имя с портом, выход в скобках, причина после двоеточия.
    /// </summary>
    /// <remarks>
    /// Тег выхода берётся из квадратных скобок целиком — в нём бывают
    /// и пробелы, и флаги, и скобки внутри. Ленивый захват остановился бы
    /// на первой закрывающей, а она может стоять внутри имени сервера.
    /// </remarks>
    private static readonly Regex Failure = new(
        @"open connection to (?<host>[^\s:]+):(?<port>\d+) using outbound/[^\[]*\[(?<out>.+)\]: (?<err>.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Ищет в журнале жалобы про названные имена.
    /// </summary>
    /// <param name="hosts">Имена, о которых проверка сказала «не доставил».</param>
    /// <param name="path">Журнал; по умолчанию — движка под супервизором.</param>
    /// <remarks>
    /// Совпадение по зоне, а не по точному имени. Проверка стучится
    /// в <c>whatsapp.com</c>, а приложение и браузер ходят на
    /// <c>web.whatsapp.com</c> и <c>static.whatsapp.net</c>; жалоба про них
    /// относится к тому же сервису и объясняет ровно то, что мы измеряли.
    /// </remarks>
    public static IReadOnlyList<EngineComplaint> Complaints(
        IReadOnlyCollection<string> hosts,
        string? path = null)
    {
        var target = path ?? DefaultPath;

        if (hosts.Count == 0 || !File.Exists(target))
            return [];

        string[] lines;

        try
        {
            // Открываем с общим доступом: движок держит файл и пишет в него
            // прямо сейчас. Без FileShare.ReadWrite чтение отказало бы всегда,
            // то есть ровно тогда, когда журнал и нужен.
            using var stream = new FileStream(
                target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            using var reader = new StreamReader(stream);
            lines = ReadTail(reader, Tail);
        }
        catch (IOException)
        {
            return [];
        }

        var zones = hosts.Select(h => h.TrimStart('*', '.')).ToList();
        var found = new Dictionary<string, EngineComplaint>(StringComparer.OrdinalIgnoreCase);

        // Снизу вверх: интересна последняя жалоба про имя, а не первая.
        // Сервер могли сменить посреди прогона, и старая запись рассказала бы
        // про выход, которого уже нет.
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var match = Failure.Match(Strip(lines[i]));

            if (!match.Success)
                continue;

            var host = match.Groups["host"].Value.TrimEnd('.');
            var zone = zones.FirstOrDefault(z => Covers(z, host));

            if (zone is null || found.ContainsKey(zone))
                continue;

            found[zone] = new EngineComplaint
            {
                Host = host,
                Outbound = match.Groups["out"].Value.Trim(),
                Error = match.Groups["err"].Value.Trim(),
            };
        }

        return found.Values.ToList();
    }

    /// <summary>Покрывает ли зона имя — сама себя или как суффикс.</summary>
    private static bool Covers(string zone, string host) =>
        host.Equals(zone, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase)

        // И наоборот: проверяли web.whatsapp.com, а жалоба про whatsapp.com.
        || zone.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Убирает управляющие последовательности цвета.
    /// </summary>
    /// <remarks>
    /// Движок красит вывод даже при записи в файл, и разбирать строку
    /// с <c>ESC[31m</c> посреди слова — верный способ не найти ничего.
    /// </remarks>
    private static string Strip(string line) =>
        Regex.Replace(line, @"\x1B\[[0-9;]*m", string.Empty);

    /// <summary>Последние строки файла без чтения его целиком в память.</summary>
    private static string[] ReadTail(StreamReader reader, int count)
    {
        var ring = new string[count];
        int written = 0;

        while (reader.ReadLine() is { } line)
            ring[written++ % count] = line;

        if (written <= count)
            return ring.Take(written).ToArray();

        // Кольцо переполнилось: восстанавливаем порядок от самой старой.
        return Enumerable.Range(0, count)
            .Select(i => ring[(written + i) % count])
            .ToArray();
    }
}
