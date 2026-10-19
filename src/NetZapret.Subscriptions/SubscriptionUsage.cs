using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetZapret.Subscriptions;

/// <summary>Расход подписки в одно мгновение — из заголовка <c>subscription-userinfo</c>.</summary>
public sealed record UsageSample(DateTimeOffset At, long Used, long Total);

/// <summary>Что показать полоской подписки.</summary>
public sealed record UsageForecast
{
    /// <summary>«233 ГБ / ∞», «120 ГБ / 500 ГБ».</summary>
    public required string Label { get; init; }

    /// <summary>Израсходованная доля лимита 0–1; <c>null</c> — лимита нет.</summary>
    public double? Fraction { get; init; }

    /// <summary>«Истекает: 27.12.2026», «Срок истёк 01.10.2026», «Без срока».</summary>
    public required string Expires { get; init; }

    /// <summary>Расход в сутки по истории; <c>null</c> — истории мало.</summary>
    public long? PerDay { get; init; }

    /// <summary>Темп слишком быстрый или лимит кончился — словами; <c>null</c> — всё в порядке.</summary>
    public string? Warning { get; init; }
}

/// <summary>
/// История расхода подписки и прогноз «при таком темпе кончится через N дней».
/// </summary>
/// <remarks>
/// <para>
/// Идея №5, владелец 19.10: «такую же полосочку, как в Happ, и восклицательный
/// знак при слишком быстрых темпах». Лимит трафика кончается разом: все маршруты
/// «через VPN» ложатся вместе, и со стороны это неотличимо от блокировки всего.
/// </para>
/// <para>
/// Пишется при каждом ответе панели (<see cref="SubscriptionPool.ReadOneAsync"/>):
/// его зовут и окно, и надзор, так что история копится и без захода на вкладку.
/// Ключ — отпечаток ссылки, а не она сама: ссылка подписки — пароль.
/// </para>
/// </remarks>
public static class SubscriptionUsage
{
    public static string DefaultPath => Path.Combine("runtime", "subscription-usage.json");

    /// <summary>Сколько хранить: месяц хватает и на месячный сброс квоты.</summary>
    public static readonly TimeSpan Keep = TimeSpan.FromDays(30);

    /// <summary>По какому отрезку считать темп: неделя сглаживает выходные.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>Короче — не темп, а случай: один вечер с фильмом дал бы «кончится через два дня».</summary>
    public static readonly TimeSpan MinSpan = TimeSpan.FromHours(12);

    /// <summary>Чаще одной записи в час не нужно: замеры ближе час заменяют друг друга.</summary>
    public static readonly TimeSpan Step = TimeSpan.FromHours(1);

    /// <summary>
    /// Насколько вперёд предупреждать.
    /// </summary>
    /// <remarks>
    /// Две недели, а не «до срока подписки»: панели часто сбрасывают квоту
    /// раз в месяц, а даты сброса заголовок не несёт. Без горизонта подписка
    /// на три месяца с месячным лимитом получала бы восклицательный знак
    /// при совершенно обычном расходе.
    /// </remarks>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static readonly object Gate = new();

    /// <summary>Записывает расход из ответа панели. Без квоты и расхода в заголовке писать нечего.</summary>
    public static void Record(string url, SubscriptionInfo info, DateTimeOffset at, string? path = null)
    {
        if (info.UsedBytes <= 0 && info.TotalBytes <= 0)
            return;

        var target = path ?? DefaultPath;

        lock (Gate)
        {
            try
            {
                var all = Load(target);
                var key = Key(url);

                all[key] = [.. Add(all.TryGetValue(key, out var history) ? history : [], new UsageSample(at, info.UsedBytes, info.TotalBytes))];

                if (Path.GetDirectoryName(target) is { Length: > 0 } directory)
                    Directory.CreateDirectory(directory);

                // Через временный файл: окно и надзор пишут каждый своё,
                // и полузаписанный файл не должен достаться читающему.
                var temporary = target + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all, Options), new UTF8Encoding(false));
                File.Move(temporary, target, overwrite: true);
            }
            catch (Exception)
            {
                // История — подсказка, а не работа: не записалась — прогноза
                // просто не будет, подписка читается как прежде.
            }
        }
    }

    /// <summary>История одной подписки, старые первыми.</summary>
    public static IReadOnlyList<UsageSample> Samples(string url, string? path = null)
    {
        lock (Gate)
        {
            return Load(path ?? DefaultPath).TryGetValue(Key(url), out var history) ? history : [];
        }
    }

    /// <summary>
    /// История с новым замером: старше месяца — прочь, замер ближе часа
    /// к прежнему его заменяет, расход меньше прежнего — сброс квоты, и
    /// история начинается заново.
    /// </summary>
    public static IReadOnlyList<UsageSample> Add(IReadOnlyList<UsageSample> history, UsageSample sample)
    {
        var kept = history.Where(s => sample.At - s.At < Keep && s.At <= sample.At).ToList();

        if (kept.Count > 0 && (sample.Used < kept[^1].Used || sample.Total != kept[^1].Total))
            kept.Clear();

        if (kept.Count > 0 && sample.At - kept[^1].At < Step)
            kept.RemoveAt(kept.Count - 1);

        kept.Add(sample);
        return kept;
    }

    /// <summary>Расход в сутки за последнюю неделю; <c>null</c> — истории меньше полусуток.</summary>
    public static long? PerDay(IReadOnlyList<UsageSample> history, DateTimeOffset now)
    {
        var recent = history.Where(s => now - s.At <= Window).ToList();

        if (recent.Count < 2)
            return null;

        var span = recent[^1].At - recent[0].At;

        if (span < MinSpan)
            return null;

        return Math.Max(0, (long)((recent[^1].Used - recent[0].Used) / span.TotalDays));
    }

    /// <summary>Полоска, срок и предупреждение для строки подписки.</summary>
    public static UsageForecast Forecast(SubscriptionInfo info, IReadOnlyList<UsageSample> history, DateTimeOffset now)
    {
        var perDay = PerDay(history, now);
        bool limited = info.TotalBytes > 0;

        string expires = info.ExpiresAt is not { } until
            ? "Без срока"
            : until > now
                ? $"Истекает: {until.LocalDateTime:dd.MM.yyyy}"
                : $"Срок истёк {until.LocalDateTime:dd.MM.yyyy}";

        string? warning = null;

        if (limited && info.RemainingBytes is 0)
        {
            warning = "Трафик подписки кончился: всё, что идёт через VPN, стоит, пока поставщик не продлит.";
        }
        else if (limited && perDay is { } pace && pace > 0 && info.RemainingBytes is { } left)
        {
            var runsOut = TimeSpan.FromDays(left / (double)pace);
            bool beforeExpiry = info.ExpiresAt is not { } end || now + runsOut < end;

            if (beforeExpiry && runsOut <= Horizon)
            {
                warning = $"При нынешнем темпе — {Size(pace)} в сутки — трафик кончится примерно через "
                    + $"{Days(runsOut)}"
                    + (info.ExpiresAt is { } date ? $", раньше срока подписки ({date.LocalDateTime:dd.MM.yyyy})" : string.Empty)
                    + ".";
            }
        }

        return new UsageForecast
        {
            Label = $"{Size(info.UsedBytes)} / {(limited ? Size(info.TotalBytes) : "∞")}",
            Fraction = limited ? Math.Clamp(info.UsedBytes / (double)info.TotalBytes, 0, 1) : null,
            Expires = expires,
            PerDay = perDay,
            Warning = warning,
        };
    }

    /// <summary>Размер словами — как на вкладке VPN.</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 40):0.#} ТБ"),
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} ГБ"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.#} МБ"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} КБ"),
    };

    private static string Days(TimeSpan span) => span.TotalDays < 1
        ? $"{Math.Max(1, (int)Math.Ceiling(span.TotalHours))} ч"
        : $"{(int)Math.Ceiling(span.TotalDays)} дн";

    private static Dictionary<string, List<UsageSample>> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, List<UsageSample>>>(File.ReadAllText(path), Options) ?? []
                : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string Key(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("NetZapret usage:" + url)))[..16];
}
