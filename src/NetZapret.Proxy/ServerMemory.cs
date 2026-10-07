using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>
/// Очистка памяти замеров серверов — кнопкой и по сроку.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 07.10: «добавь возможность очистить память работы серверов (то что
/// показывает через раз и т.д.), а так же настраиваемое автоочищение …
/// с умолчанием раз в неделю». Память — <see cref="ServerHealthCache"/>:
/// задержки и исходы последних десяти проверок. По ней сервер становится
/// «мигающим» и уходит из автоподбора, «мёртвым» — тоже. Сервер, который
/// однажды мигал, а потом починился, сам из этого не выберется, пока не наберёт
/// удачных проверок, — а проверяют его редко, раз он мимо автоподбора.
/// </para>
/// <para>
/// Настройки пишутся, только если файл прочитался: не прочитался — значит,
/// в руках значения по умолчанию, и запись поверх стёрла бы настройки целиком.
/// </para>
/// </remarks>
public static class ServerMemory
{
    /// <summary>Срок по умолчанию — неделя.</summary>
    public const int DefaultDays = 7;

    /// <summary>Пора ли чистить: срок задан, отсчёт начат и прошёл.</summary>
    public static bool IsDue(AppSettings settings, DateTimeOffset now) =>
        settings.ServerMemoryDays > 0
        && settings.ServerMemoryClearedAt is { } at
        && now - at >= TimeSpan.FromDays(settings.ServerMemoryDays);

    /// <summary>
    /// Чистит память, если подошёл срок; <c>true</c> — почистила.
    /// </summary>
    /// <remarks>
    /// Отсчёта ещё не было (настройка новая) — он начинается сейчас, а память
    /// не трогается: иначе первая же сборка с этой правкой стёрла бы её у всех
    /// разом, без всякого срока.
    /// </remarks>
    public static bool ClearIfDue(DateTimeOffset now, string? settingsPath = null, string? healthPath = null)
    {
        var path = settingsPath ?? AppSettings.DefaultPath;
        var settings = AppSettings.TryLoad(path, out var read);

        if (read != AppSettings.ReadResult.Read || settings.ServerMemoryDays <= 0)
            return false;

        if (settings.ServerMemoryClearedAt is null)
        {
            (settings with { ServerMemoryClearedAt = now }).Save(path);
            return false;
        }

        if (!IsDue(settings, now))
            return false;

        ClearNow(now, path, healthPath);
        return true;
    }

    /// <summary>Чистит память сейчас и начинает отсчёт срока заново.</summary>
    public static void ClearNow(DateTimeOffset now, string? settingsPath = null, string? healthPath = null)
    {
        ServerHealthCache.Clear(healthPath);

        var path = settingsPath ?? AppSettings.DefaultPath;
        var settings = AppSettings.TryLoad(path, out var read);

        if (read == AppSettings.ReadResult.Read)
            (settings with { ServerMemoryClearedAt = now }).Save(path);
    }
}
