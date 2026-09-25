namespace NetZapret.Supervisor;

/// <summary>
/// Поднялись ли движки на самом деле.
/// </summary>
/// <remarks>
/// <para>
/// Отдельно от запуска движков потому, что отвечает на другой
/// вопрос. Тот — «запустить», и успехом там считается порождённый процесс
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
public static class EngineHealth
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
    /// Движки подняты — пусть даже наружу не доходит.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отличается от <see cref="AllHealthy"/> тем, что <c>Degraded</c> здесь
    /// считается успехом. Разница решающая для автозапуска: его дело —
    /// поднять движки, а не починить чужую подписку. Служба, чей процесс
    /// жив и которую супервизор сторожит, поднята, и снимать её незачем.
    /// </para>
    /// <para>
    /// Исправление регрессии, внесённой мной же в 0.6.2. Автозапуск ждал
    /// именно <see cref="AllHealthy"/>, а у владельца из девяти серверов
    /// подписки отвечали двое — sing-box оставался вечно «жив, но проверку
    /// не проходит». Автозапуск ждал семьдесят пять секунд, снимал движки,
    /// поднимал заново, и так трижды: около четырёх минут, в течение
    /// которых туннель рвался и вставал. До 0.6.2 запуск был один.
    /// </para>
    /// </remarks>
    public static bool Running(SupervisorState? state) =>
        state is not null
        && state.IsSupervisorAlive()
        && state.Services.Count > 0
        && state.Services.All(s => s.Health is ServiceHealth.Healthy or ServiceHealth.Degraded);

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

    /// <summary>
    /// Состояние службы словами — для окна, трея и nz status одинаково.
    /// </summary>
    /// <remarks>
    /// Причина — словами надзора, а не «запущен, но не отвечает»: у sing-box
    /// это почти всегда мёртвые выходы подписки, и сказать это надо прямо.
    /// Время начала — у всего, что не «работает» (26.09): «с 23:30» отличает
    /// затяжную беду от минутной.
    /// </remarks>
    public static string Status(ServiceState service)
    {
        if (service.Health == ServiceHealth.Healthy)
            return service.ProcessId is { } pid ? $"работает, процесс {pid}" : "работает";

        var text = service.Health switch
        {
            ServiceHealth.Degraded => service.LastError is { Length: > 0 } error ? error : "запущен, но не отвечает",
            ServiceHealth.Dead => service.LastError is { Length: > 0 } died ? $"процесс умер: {died}" : "процесс умер",
            ServiceHealth.Faulted => service.LastError is { Length: > 0 } last
                ? $"не поднимается, попытки исчерпаны: {last}"
                : "не поднимается: попытки перезапуска исчерпаны",
            _ => "остановлен",
        };

        if (service.Health != ServiceHealth.Stopped && service.HealthSince is { } since)
            text += $" · с {since.ToLocalTime():HH:mm}";

        return text;
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
