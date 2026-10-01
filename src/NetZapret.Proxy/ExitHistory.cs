using System.Text;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>Выход, замеченный работающим: тег и когда.</summary>
public sealed record ExitSeen(string Tag, DateTimeOffset At);

/// <summary>
/// Через какие выходы недавно шёл трафик — для «Недавних серверов» на вкладке VPN.
/// </summary>
/// <remarks>
/// <para>
/// Пишется там, где выход и так спрашивают у движка (<see cref="TunnelStatus.CurrentExitAsync"/>):
/// «Главная» спрашивает раз в несколько секунд, вкладка VPN — раз в 15, <c>nz status</c> —
/// по просьбе. Отдельный опрос ради журнала был бы лишним вопросом движку.
/// Значит, журнал знает только выходы, замеченные при открытой программе, —
/// смену при закрытом окне он увидит со следующего вопроса.
/// </para>
/// <para>
/// Запись — только при смене выхода, и сравнение сперва в памяти: опрос
/// частый, а файл меняется редко.
/// </para>
/// </remarks>
public static class ExitHistory
{
    public static string DefaultPath => Path.Combine("runtime", "exit-history.json");

    /// <summary>Сколько смен хранить.</summary>
    public const int Keep = 40;

    private static readonly object Gate = new();
    private static string? _last;

    /// <summary>Отмечает выход; тот же, что в прошлый раз, — ничего не пишет.</summary>
    public static void Note(string tag, DateTimeOffset at, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return;

        var target = path ?? DefaultPath;

        lock (Gate)
        {
            if (path is null && tag == _last)
                return;

            var seen = Read(target).ToList();

            if (seen.Count == 0 || seen[^1].Tag != tag)
            {
                seen.Add(new ExitSeen(tag, at));

                if (seen.Count > Keep)
                    seen.RemoveRange(0, seen.Count - Keep);

                Write(target, seen);
            }

            if (path is null)
                _last = tag;
        }
    }

    public static IReadOnlyList<ExitSeen> Read(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            return File.Exists(target)
                ? JsonSerializer.Deserialize<List<ExitSeen>>(File.ReadAllText(target)) ?? []
                : [];
        }
        catch (Exception)
        {
            // Журнал — удобство: испорченный начинается заново.
            return [];
        }
    }

    /// <summary>Последние разные выходы, свежие первыми.</summary>
    public static IReadOnlyList<ExitSeen> Recent(int count, string? path = null) =>
        Read(path).Reverse().DistinctBy(e => e.Tag).Take(count).ToList();

    private static void Write(string target, IReadOnlyList<ExitSeen> seen)
    {
        try
        {
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Через временный файл: окно и nz могут писать почти разом.
            var temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(seen), new UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception)
        {
            // Не записалось — покажем то, что было.
        }
    }
}
