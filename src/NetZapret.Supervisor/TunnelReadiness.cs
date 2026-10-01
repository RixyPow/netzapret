namespace NetZapret.Supervisor;

/// <summary>
/// Готов ли туннель пропускать трафик — по тому, что знает надзор.
/// </summary>
/// <remarks>
/// <para>
/// Нужен замеру скорости и прочему, что идёт через туннель: отказ сам по себе
/// не говорит, туннель ли не готов или сервер не отвечает. 01.10 у владельца
/// замер через WARP нажат через 24 с после запуска и получил «сервер замера
/// недоступен», а надзор за семь секунд до того записал «трафик не идёт».
/// </para>
/// <para>
/// После запуска с WARP первая проверка прохода трафика (через 15 с) не проходит
/// каждый раз, и здоров он становится примерно через полминуты (замеры 01.10:
/// 00:27, 00:39, 00:47, 00:51). Отсюда срок <see cref="WarmUp"/>.
/// </para>
/// </remarks>
public static class TunnelReadiness
{
    /// <summary>Сколько туннелю дать после запуска, прежде чем винить что-то ещё.</summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(45);

    private const string Tunnel = "sing-box";

    /// <summary>
    /// Почему через туннель сейчас может не пройти; <c>null</c> — надзор причин не видит.
    /// </summary>
    public static string? Why(SupervisorState? state, DateTimeOffset now)
    {
        if (state is null || !state.IsSupervisorAlive())
            return "Движки не запущены.";

        var tunnel = state.Services.FirstOrDefault(s => s.Name == Tunnel);

        if (tunnel is null)
            return "Туннель выключен.";

        if (tunnel.Health == ServiceHealth.Degraded)
        {
            var since = tunnel.HealthSince is { } at ? $" с {at.ToLocalTime():HH:mm:ss}" : string.Empty;

            return $"Надзор видит то же: трафик через туннель не идёт{since}. "
                + "Сразу после запуска так бывает около полуминуты — с WARP каждый раз.";
        }

        if (tunnel.Health != ServiceHealth.Healthy)
            return "Туннель сейчас не работает.";

        if (tunnel.StartedAt is { } started && now - started < WarmUp)
        {
            return $"Туннель поднят {(int)(now - started).TotalSeconds} с назад — ему нужно около полуминуты. "
                + "Повторите замер чуть позже.";
        }

        return null;
    }
}
