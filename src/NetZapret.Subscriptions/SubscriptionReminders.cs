using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetZapret.Subscriptions;

/// <summary>
/// Напоминание о конце подписки — не чаще раза в сутки на подписку.
/// </summary>
/// <remarks>
/// <para>
/// Как в Happ: за три дня до конца срока и после него (владелец 15.10: «делай»).
/// Подписки читаются при каждом запуске движков и заходе во вкладку VPN —
/// напоминание на каждое чтение превратилось бы в шум, который перестают читать.
/// </para>
/// <para>
/// Подписка узнаётся по отпечатку ссылки, а не по самой ссылке: файл лежит
/// в <c>runtime\</c>, а ссылка — пароль.
/// </para>
/// </remarks>
public static class SubscriptionReminders
{
    public static string DefaultPath => Path.Combine("runtime", "subscription-reminders.json");

    /// <summary>Не чаще — с запасом на то, что программу открывают в разное время дня.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(20);

    /// <summary>
    /// Пора ли напомнить об этой подписке; если да — отмечает, что напомнили.
    /// </summary>
    public static bool Due(string url, DateTimeOffset now, string? path = null)
    {
        var target = path ?? DefaultPath;
        var key = Key(url);
        var seen = Load(target);

        if (seen.TryGetValue(key, out var last) && now - last < Interval)
            return false;

        seen[key] = now;

        // Записи старше месяца — подписки, которых уже нет.
        foreach (var stale in seen.Where(p => now - p.Value > TimeSpan.FromDays(30)).Select(p => p.Key).ToList())
            seen.Remove(stale);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
            File.WriteAllText(target, JsonSerializer.Serialize(seen), new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Не записалось — напомним ещё раз; это лучше, чем не напомнить.
        }

        return true;
    }

    private static Dictionary<string, DateTimeOffset> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string Key(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16];
}
