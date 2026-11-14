using NetZapret.Core;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>
/// Журнал автонастройки YouTube — для разбора по отчёту: каждый замер с таблицей узлов,
/// решения с причинами, что записано, «было → стало», откаты.
/// </summary>
/// <remarks>
/// До 14.11 от автонастройки в отчёте оставался только итог в <c>settings.json</c>
/// и одна строка замера. На жалобу «после автонастройки стало хуже» по отчёту было
/// не видно ни того, что она решила, ни что показал перезамер. Журнал пишут окно
/// и <c>nz</c> одним кодом; в архив отчёта он идёт целиком.
/// </remarks>
public static class YoutubeTuneLog
{
    /// <summary>Файл журнала относительно корня установки.</summary>
    public static string DefaultPath => Path.Combine("runtime", "youtube-tune.log");

    private static readonly object Gate = new();

    /// <summary>Замер и решение по нему.</summary>
    public static void Measured(AppSettings settings, VideoNodesReport report, YoutubeDecision? decision, string who)
    {
        var lines = new List<string> { $"ЗАМЕР ({who}); пресет: {settings.PresetName ?? "не выбран"}; стоит: {Describe(settings.Youtube)}" };
        lines.AddRange(report.Details.Select(l => "  " + l));
        lines.Add("  итог замера: " + (report.Summary ?? report.Totals));

        if (decision is not null)
        {
            lines.Add("  решения:");
            lines.AddRange(decision.Reasons.Select(r => "    " + r));

            if (decision.Tune is { } tune)
                lines.Add($"  предложено: {Describe(tune)}");
        }

        Write(lines);
    }

    /// <summary>Записана настройка (или снята — <c>null</c>).</summary>
    public static void Saved(YoutubeTune? previous, YoutubeTune? tune, bool restart, string who) => Write(
    [
        $"ЗАПИСАНО ({who}): было — {Describe(previous)}; стало — {Describe(tune)}"
            + (restart ? "; движки перезапускаются" : "; строка winws2 та же — без перезапуска"),
    ]);

    /// <summary>Перезамер после применения.</summary>
    public static void Compared(VideoNodesReport after, string said, bool worse, string who)
    {
        var lines = new List<string> { $"ПЕРЕЗАМЕР ({who}): {said}; {(worse ? "хуже — откат" : "не хуже — настройка остаётся")}" };
        lines.AddRange(after.Details.Select(l => "  " + l));
        Write(lines);
    }

    /// <summary>Откат или снятие кнопкой.</summary>
    public static void RolledBack(YoutubeTune? to, string who) =>
        Write([$"ОТКАТ ({who}): теперь — {Describe(to)}"]);

    /// <summary>Что-то пошло не так: движки не перезапустились, замер не вышел.</summary>
    public static void Failed(string what, string who) => Write([$"СБОЙ ({who}): {what}"]);

    private static string Describe(YoutubeTune? tune) => tune switch
    {
        { Active: true } t => Zapret.YoutubeAddon.Describe(t) + (t.Measured is { } at ? $" (замер {at:dd.MM HH:mm})" : string.Empty),
        { } => "ничего не меняется",
        null => "не настроено",
    };

    private static void Write(IReadOnlyList<string> lines)
    {
        try
        {
            lock (Gate)
            {
                using var log = new RollingLog(DefaultPath, maxBytes: 1024 * 1024, generations: 2);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

                log.AppendLine($"{stamp} {lines[0]}");

                foreach (var line in lines.Skip(1))
                    log.AppendLine(line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал для разбора: не записался — автонастройка работает и без него.
        }
    }
}
