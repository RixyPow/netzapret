using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>Один замер скорости — то, что от него остаётся.</summary>
/// <remarks>
/// Адреса, с которого нас видел сервер замера, здесь нет намеренно: он личный,
/// а файл лежит рядом с журналами и уходит с ними в отчёты.
/// </remarks>
public sealed record SpeedEntry
{
    public DateTimeOffset At { get; init; }

    /// <summary>Через туннель; иначе — напрямую.</summary>
    public bool Tunnel { get; init; }

    /// <summary>Через какой сервер шёл туннель в момент замера.</summary>
    public string? Exit { get; init; }

    public double? DownMbps { get; init; }

    public double? UpMbps { get; init; }

    public double? PingMs { get; init; }

    public double? JitterMs { get; init; }

    /// <summary>Страна, из которой нас видел сервер замера.</summary>
    public string? Country { get; init; }

    /// <summary>Узел сервера замера.</summary>
    public string? Node { get; init; }

    /// <summary>Сколько байт ушло на замер.</summary>
    public long Bytes { get; init; }

    public string? Problem { get; init; }

    public static SpeedEntry From(SpeedResult result, bool tunnel, string? exit, DateTimeOffset at) => new()
    {
        At = at,
        Tunnel = tunnel,
        Exit = tunnel ? exit : null,
        DownMbps = result.DownMbps,
        UpMbps = result.UpMbps,
        PingMs = result.PingMs,
        JitterMs = result.JitterMs,
        Country = result.Country,
        Node = result.Node,
        Bytes = result.DownBytes + result.UpBytes,
        Problem = result.Problem,
    };
}

/// <summary>
/// Прошлые замеры скорости — чтобы сегодняшний было с чем сравнить.
/// </summary>
/// <remarks>
/// Владелец 30.09, по макету раздела: таблица «Последние замеры». Раздел
/// создаётся заново при каждом заходе, а сравнивают обычно «как было утром» —
/// поэтому файл, а не память окна.
/// </remarks>
public static class SpeedHistory
{
    /// <summary>Сколько замеров помнить.</summary>
    public const int Keep = 30;

    public static string DefaultPath => Path.Combine("runtime", "speed-history.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Замеры, свежие первыми; файла нет или он испорчен — пусто.</summary>
    public static IReadOnlyList<SpeedEntry> Load(string? path = null)
    {
        try
        {
            var text = File.ReadAllText(path ?? DefaultPath);

            return (JsonSerializer.Deserialize<List<SpeedEntry>>(text, Json) ?? [])
                .OrderByDescending(e => e.At)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Дописывает замер; не записалось — замер всё равно показан, и только.</summary>
    public static void Add(SpeedEntry entry, string? path = null)
    {
        try
        {
            var target = path ?? DefaultPath;
            var entries = Load(target).Prepend(entry).Take(Keep).ToList();

            if (Path.GetDirectoryName(Path.GetFullPath(target)) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);

            File.WriteAllText(target, JsonSerializer.Serialize(entries, Json));
        }
        catch (Exception)
        {
            // История — удобство, а не результат.
        }
    }
}

/// <summary>
/// Вывод из замера: что он значит рядом с замером другого пути.
/// </summary>
/// <remarks>
/// <para>
/// «В норме» здесь не говорится: нормы программа не знает — ни тарифа, ни того,
/// что обещал продавец VPN. Она знает второй путь, и сравнение с ним — всё,
/// что можно сказать честно.
/// </para>
/// <para>
/// Сравнивается только со свежим замером другого пути: утренняя прямая скорость
/// вечернему туннелю не мерило.
/// </para>
/// </remarks>
public static class SpeedVerdict
{
    /// <summary>Старше этого замер другого пути в сравнение не идёт.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromHours(3);

    /// <summary>Замер другого пути, с которым можно сравнить; <c>null</c> — не с чем.</summary>
    public static SpeedEntry? Pair(SpeedEntry latest, IEnumerable<SpeedEntry> history) =>
        history
            .Where(e => e.Tunnel != latest.Tunnel && e.DownMbps is not null)
            .Where(e => (latest.At - e.At).Duration() <= Fresh)
            .OrderByDescending(e => e.At)
            .FirstOrDefault();

    public static (string Title, string Text) Describe(SpeedEntry latest, SpeedEntry? pair)
    {
        if (latest.DownMbps is null && latest.UpMbps is null)
        {
            return ("Замер не удался", Sentence(latest.Problem ?? "ничего не измерено"));
        }

        var own = latest.Tunnel ? "через туннель" : "напрямую";

        if (pair is null)
        {
            return (
                Speeds(latest),
                (latest.Problem is { Length: > 0 } problem ? Sentence(problem) + " " : string.Empty)
                + (latest.Tunnel
                    ? "Замерьте и напрямую — будет видно, сколько скорости стоит туннель."
                    : "Замерьте и через туннель — будет видно, сколько скорости он стоит."));
        }

        var tunnel = latest.Tunnel ? latest : pair;
        var direct = latest.Tunnel ? pair : latest;

        var lines = new List<string>();

        if (tunnel.DownMbps is { } down && direct.DownMbps is { } directDown and > 0)
            lines.Add($"Скачивание {Number(down)} из {Number(directDown)} Мбит/с.");

        if (tunnel.UpMbps is { } up && direct.UpMbps is { } directUp and > 0)
            lines.Add($"Отдача {Number(up)} из {Number(directUp)} Мбит/с — {Percent(up, directUp)}.");

        if (tunnel.PingMs is { } ping && direct.PingMs is { } directPing)
            lines.Add($"Задержка {ping:0} мс против {directPing:0}.");

        lines.Add($"Сравнение с замером {(pair.Tunnel ? "через туннель" : "напрямую")} в {pair.At.ToLocalTime():HH:mm}.");

        if (latest.Problem is { Length: > 0 } trouble)
            lines.Add(Sentence(trouble));

        var title = tunnel.DownMbps is { } t && direct.DownMbps is { } d and > 0
            ? $"Туннель даёт {Percent(t, d)} прямой скорости"
            : Speeds(latest) + " " + own;

        return (title, string.Join(" ", lines));
    }

    /// <summary>Итог текстом — для буфера обмена.</summary>
    public static string Copy(SpeedEntry latest, SpeedEntry? pair, string version)
    {
        var lines = new List<string> { $"NetZapret {version}, замер скорости" };

        foreach (var entry in new[] { latest, pair }.OfType<SpeedEntry>())
        {
            lines.Add($"{entry.At.ToLocalTime():dd.MM HH:mm} {(entry.Tunnel ? "через туннель" : "напрямую")}: "
                + Line(entry)
                + (entry.Tunnel && entry.Exit is { Length: > 0 } exit ? $"; сервер {exit}" : string.Empty)
                + (entry.Country is { Length: > 0 } country ? $"; страна выхода {country}" : string.Empty)
                + (entry.Node is { Length: > 0 } node ? $", узел Cloudflare {node}" : string.Empty));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string Line(SpeedEntry entry)
    {
        var parts = new List<string>();

        if (entry.DownMbps is { } down)
            parts.Add($"скачивание {Number(down)} Мбит/с");

        if (entry.UpMbps is { } up)
            parts.Add($"отдача {Number(up)} Мбит/с");

        if (entry.PingMs is { } ping)
            parts.Add($"задержка {ping:0} мс");

        if (entry.Problem is { Length: > 0 } problem)
            parts.Add(problem);

        return parts.Count == 0 ? "ничего не измерено" : string.Join(", ", parts);
    }

    private static string Speeds(SpeedEntry entry) =>
        (entry.DownMbps, entry.UpMbps) switch
        {
            ({ } down, { } up) => $"{Number(down)} вниз, {Number(up)} вверх",
            ({ } down, null) => $"{Number(down)} Мбит/с вниз",
            (null, { } up) => $"{Number(up)} Мбит/с вверх",
            _ => "Ничего не измерено",
        };

    /// <summary>Неровность не выше этой — скорость шла стабильно.</summary>
    public const double Steady = 0.2;

    /// <summary>
    /// Насколько неровно шла скорость: разброс показаний к их среднему.
    /// </summary>
    /// <remarks>
    /// Первая четверть показаний не в счёт — это разгон, он неровен всегда.
    /// Меньше четырёх показаний после него — судить не по чему.
    /// </remarks>
    public static double? Unevenness(IReadOnlyList<double> readings)
    {
        var steady = readings.Skip(readings.Count / 4).ToList();

        if (steady.Count < 4)
            return null;

        double mean = steady.Average();

        if (mean <= 0)
            return null;

        return Math.Sqrt(steady.Sum(v => (v - mean) * (v - mean)) / steady.Count) / mean;
    }

    /// <summary>До сотни — с десятой, дальше целым.</summary>
    public static string Number(double mbps) => mbps < 100 ? $"{mbps:0.0}" : $"{mbps:0}";

    private static string Percent(double part, double whole) => $"{part / whole * 100:0} %";

    private static string Sentence(string text) =>
        char.ToUpper(text[0]) + text[1..] + (text.EndsWith('.') ? string.Empty : ".");
}
