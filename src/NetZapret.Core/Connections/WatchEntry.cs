using System.Globalization;
using System.Text;
using NetZapret.Core.Rules;

namespace NetZapret.Core.Connections;

/// <summary>
/// Одно замеченное соединение и решение правил по нему — строкой журнала
/// наблюдения и строкой таблицы.
/// </summary>
/// <remarks>
/// <para>
/// Одна запись на окно, <c>nz watch</c> и отчёт: прежде строку собирал
/// раздел окна сам, и журнала не было вовсе — закрыл окно, и всё пропало
/// (владелец 10.10: «запись наблюдений в логи есть?»).
/// </para>
/// <para>
/// «Куда» — это то, что сказали бы правила, а не то, что сделал движок:
/// наблюдение читает события ядра, а не sing-box и не winws2.
/// </para>
/// </remarks>
/// <param name="Endpoint">Имя с портом, если имя узнано, иначе адрес с портом.</param>
/// <param name="Address">Адрес с портом всегда — имя на адресе CDN бывает чужим.</param>
public sealed record WatchEntry(
    DateTimeOffset Time,
    RoutingMode Mode,
    string Protocol,
    string Process,
    string Endpoint,
    string Address,
    string Rule,
    bool Dropped)
{
    /// <summary>Неизвестная программа: процесс завершился раньше, чем его спросили, или защищён.</summary>
    public const string UnknownProcess = "?";

    /// <summary>Начало строки сеанса в журнале.</summary>
    public const string SessionMark = "# ";

    /// <summary>Слова строки начала сеанса — по ним сводка считает сеансы.</summary>
    public const string Started = "наблюдение начато";

    public static WatchEntry From(ConnectionEvent connection, RuleDecision decision)
    {
        var address = connection.DescribeEndpoint();

        var rule = decision.Rule is null
            ? decision.Reason ?? "по умолчанию"
            : $"#{decision.Rule.Ordinal} {decision.Reason}";

        return new WatchEntry(
            connection.Timestamp,
            decision.Mode,
            connection.Protocol == ProtocolKind.Udp ? "udp" : "tcp",
            connection.ExecutableName ?? UnknownProcess,
            connection.Hostname is { } host ? $"{host}:{connection.RemotePort}" : address,
            address,
            rule,
            connection.Verdict == ObservedVerdict.Dropped);
    }

    /// <summary>«Куда» словом — как в таблице окна.</summary>
    public string ModeWord => Word(Mode);

    public static string Word(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "туннель",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };

    /// <summary>Правило для показа: с пометкой, если система соединение отбросила.</summary>
    public string RuleShown => Dropped ? "система отбросила · " + Rule : Rule;

    /// <summary>
    /// Строка журнала: поля через табуляцию — читается глазом и разбирается
    /// обратно (<see cref="Parse"/>) для сводки в отчёте.
    /// </summary>
    public string ToLine() => string.Join('\t',
        Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
        ModeWord,
        Protocol,
        Process,
        Endpoint,
        Address,
        Clean(RuleShown));

    /// <summary>Табуляция и перевод строки в правиле порвали бы строку журнала.</summary>
    private static string Clean(string text) => text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>Разбирает строку журнала; <c>null</c> — не запись (сеанс, мусор, обрыв).</summary>
    public static WatchEntry? Parse(string line)
    {
        if (line.StartsWith(SessionMark, StringComparison.Ordinal))
            return null;

        var parts = line.Split('\t');

        if (parts.Length < 7
            || !DateTime.TryParseExact(parts[0], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var time))
        {
            return null;
        }

        var mode = parts[1] switch
        {
            "туннель" => RoutingMode.Proxy,
            "десинк" => RoutingMode.Desync,
            _ => RoutingMode.Direct,
        };

        const string dropped = "система отбросила · ";
        var rule = string.Join('\t', parts[6..]);
        bool wasDropped = rule.StartsWith(dropped, StringComparison.Ordinal);

        return new WatchEntry(
            new DateTimeOffset(time),
            mode,
            parts[2],
            parts[3],
            parts[4],
            parts[5],
            wasDropped ? rule[dropped.Length..] : rule,
            wasDropped);
    }

    /// <summary>Строка начала или конца сеанса наблюдения.</summary>
    public static string Session(DateTimeOffset when, string text) =>
        SessionMark + when.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + Clean(text);

    /// <summary>Сколько назначений называть у программы в сводке.</summary>
    private const int TopEndpoints = 8;

    /// <summary>
    /// Сводка журнала по программам: сколько соединений и куда, и главные
    /// назначения каждой.
    /// </summary>
    /// <remarks>
    /// Для отчёта (владелец 10.10: «и отдельно по программам, и отдельно
    /// полностью журнал»): по сводке видно, у какой программы что уходит
    /// не туда, а журнал целиком — чтобы разобрать конкретное соединение.
    /// Назначения считаются по имени, где оно узнано: так видно сайт,
    /// а не адрес CDN.
    /// </remarks>
    public static string Summarize(IEnumerable<string> lines)
    {
        var entries = new List<WatchEntry>();
        int sessions = 0;

        foreach (var line in lines)
        {
            if (line.StartsWith(SessionMark, StringComparison.Ordinal) && line.Contains(Started, StringComparison.Ordinal))
                sessions++;

            if (Parse(line) is { } entry)
                entries.Add(entry);
        }

        var text = new StringBuilder();

        if (entries.Count == 0)
        {
            text.AppendLine(sessions == 0
                ? "Журнала наблюдения нет: «Наблюдение» в окне и nz watch не включали."
                : $"Сеансов наблюдения: {sessions}, соединений в журнале нет.");

            return text.ToString();
        }

        var first = entries.Min(e => e.Time).ToLocalTime();
        var last = entries.Max(e => e.Time).ToLocalTime();

        text.AppendLine($"Наблюдение: сеансов {sessions}, {first:dd.MM HH:mm:ss} — {last:dd.MM HH:mm:ss}, соединений {entries.Count}.");
        text.AppendLine("«Куда» — что сказали бы правила, а не что сделал движок; имя на адресе CDN бывает чужим.");
        text.AppendLine();
        text.AppendLine($"{"программа",-32} {"всего",7} {"туннель",8} {"десинк",8} {"напрямую",9} {"udp",6}");

        var programs = entries
            .GroupBy(e => e.Process, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var g in programs)
        {
            text.AppendLine(
                $"{Cut(g.Key, 32),-32} {g.Count(),7} {g.Count(e => e.Mode == RoutingMode.Proxy),8} "
                + $"{g.Count(e => e.Mode == RoutingMode.Desync),8} {g.Count(e => e.Mode == RoutingMode.Direct),9} "
                + $"{g.Count(e => e.Protocol == "udp"),6}");
        }

        foreach (var g in programs)
        {
            text.AppendLine();
            text.AppendLine($"{g.Key} — {g.Count()}:");

            foreach (var target in g
                .GroupBy(e => (e.Endpoint, e.Mode))
                .OrderByDescending(t => t.Count())
                .Take(TopEndpoints))
            {
                text.AppendLine($"  {target.Count(),5}  {Word(target.Key.Mode),-8}  {target.Key.Endpoint}");
            }
        }

        return text.ToString();
    }

    private static string Cut(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";
}
