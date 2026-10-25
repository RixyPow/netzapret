namespace NetZapret.Supervisor;

/// <summary>
/// Ступени резолвера движка: у кого спрашивать имена, пока выбранный не отвечает,
/// и что об этом сказать человеку.
/// </summary>
/// <remarks>
/// <para>
/// Порядок: выбранный резолвер напрямую → он же по TCP (только «авто», 20.10) →
/// запасной (<see cref="Proxy.DnsSpare"/>, 25.10) → туннель (только «авто», 19.10).
/// Запасной раньше туннеля: он отдаёт адреса рядом с человеком, а не у выхода
/// VPN (замер 06.10: TLS 65–73 мс от прямого DoH против 141–151 от DoH через туннель).
/// </para>
/// <para>
/// 25.10 у владельца при пути «напрямую» умер Comss DNS, и ступеней не было
/// вовсе: каждое имя ждало 11–12 с, а «Главная» писала «Работает».
/// </para>
/// </remarks>
public static class DnsLadder
{
    public enum Step
    {
        /// <summary>Выбранный резолвер отвечает.</summary>
        Direct,

        /// <summary>Выбранный по TCP, DoH к нему молчит.</summary>
        Tcp,

        /// <summary>Запасной резолвер.</summary>
        Spare,

        /// <summary>Выбранный через туннель.</summary>
        Tunnel,

        /// <summary>Никто не отвечает: имена не разрешаются.</summary>
        None,
    }

    /// <param name="direct">Выбранный отвечает напрямую (возврат — после двух ответов подряд).</param>
    /// <param name="tcp">Выбранный отвечает по TCP.</param>
    /// <param name="spare">Запасной отвечает.</param>
    /// <param name="tunnel">Туннель пропускает трафик и путь DNS «авто».</param>
    public static Step Choose(bool direct, bool tcp, bool spare, bool tunnel) =>
        direct ? Step.Direct
        : tcp ? Step.Tcp
        : spare ? Step.Spare
        : tunnel ? Step.Tunnel
        : Step.None;

    /// <summary>
    /// Что сказать на «Главной» и в «Диагностике»; <c>null</c> — всё в порядке.
    /// </summary>
    /// <param name="primary">Выбранный резолвер словами: «Comss.one (83.220.169.155)».</param>
    /// <param name="spare">Запасной словами; <c>null</c> — его нет в конфиге.</param>
    public static string? Notice(Step step, string primary, string? spare) => step switch
    {
        Step.Direct => null,
        Step.Tcp => $"резолвер {primary} не отвечает по DoH — имена спрашиваются у него же по TCP",
        Step.Spare => $"резолвер {primary} не отвечает — имена спрашиваются у запасного {spare}",
        Step.Tunnel => $"резолвер {primary} не отвечает напрямую — имена спрашиваются через туннель",
        _ => $"резолвер {primary} не отвечает" + (spare is null ? string.Empty : $", запасной {spare} тоже")
            + " — имена не разрешаются, сайты открываются с задержкой или не открываются",
    };
}
