using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>
/// Поднялись ли движки на самом деле.
/// </summary>
/// <remarks>
/// <para>
/// Отдельно от <see cref="EngineControl"/> потому, что отвечает на другой
/// вопрос. Там — «запустить», и успехом там считается порождённый процесс
/// супервизора: дальше он живёт своей жизнью и может пять раз не поднять
/// sing-box, после чего сдаться. Здесь — «работает ли обход прямо сейчас».
/// </para>
/// <para>
/// Различие стоило жалобы 19.09: «трей запускается, программа запускается,
/// а движки нужно поднимать кнопкой». В журнале при этом семь раз подряд
/// стояло «автозапуск: движки подняты с попытки 1» — и стояло правдиво
/// по той мерке, какой мерили.
/// </para>
/// </remarks>
internal static class EngineHealth
{
    /// <summary>
    /// Работают ли все службы.
    /// </summary>
    /// <remarks>
    /// Пустой список служб здоровьем не считается: супервизор, которому
    /// нечего сторожить, — это не работающий обход, а недособранная настройка.
    /// Так выглядит запуск без пресета: sing-box поднят, winws2 не заведён,
    /// и десинка нет вовсе.
    /// </remarks>
    public static bool AllHealthy(SupervisorState? state) =>
        state is not null
        && state.IsSupervisorAlive()
        && state.Services.Count > 0
        && state.Services.All(s => s.Health == ServiceHealth.Healthy);

    /// <summary>
    /// Чем кончилось — словами самих служб.
    /// </summary>
    /// <remarks>
    /// Берётся у службы, а не сочиняется: «sing-box: не запустился — процесс
    /// завершился с кодом 1» говорит больше, чем «не получилось», и именно
    /// эта строка нужна тому, кто утром читает журнал.
    /// </remarks>
    public static string Complaint(SupervisorState? state)
    {
        if (state is null)
            return "супервизор не оставил состояния";

        if (!state.IsSupervisorAlive())
            return "супервизор не дожил — он сдаётся после пяти неудачных перезапусков";

        if (state.Services.Count == 0)
            return "супервизор не получил ни одной службы — проверьте пресет";

        var sick = state.Services
            .Where(s => s.Health != ServiceHealth.Healthy)
            .Select(Describe)
            .ToList();

        return sick.Count == 0
            ? "движки не поднялись, причина не записана"
            : string.Join("; ", sick);
    }

    private static string Describe(ServiceState service) =>
        service.LastError is { Length: > 0 } error
            ? $"{service.Name}: {error}"
            : $"{service.Name}: {Name(service.Health)}";

    private static string Name(ServiceHealth health) => health switch
    {
        ServiceHealth.Healthy => "работает",
        ServiceHealth.Degraded => "жив, но проверку не проходит",
        ServiceHealth.Dead => "процесс умер",
        ServiceHealth.Stopped => "не запускался",
        _ => health.ToString(),
    };
}
