namespace NetZapret.Core.Rules;

/// <summary>
/// «Всё через VPN» одним нажатием — и обратно к тому, что было.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №17 (04.10): чтобы на минуту пустить всё через VPN, приходилось
/// открыть окно, выключить десинк, перезапустить движки, а потом всё то же
/// в обратную сторону. Просьба — переключатель в трее.
/// </para>
/// <para>
/// «Всё через VPN» — это туннель без десинка: такой туннель забирает весь
/// трафик, кроме поставленного «напрямую» (<see cref="EngineChoice.TunnelTakesAll"/>,
/// «как обычный VPN» — слова владельца). Отдельного режима не заводится:
/// то же сочетание собирается выключателями на «Главной», и переключатель
/// показывает его, как бы оно ни было набрано.
/// </para>
/// <para>
/// Возврат — к выключателям, какими они были до нажатия (<see cref="AppSettings.DesyncBeforeVpnOnly"/>,
/// <see cref="AppSettings.TunnelBeforeVpnOnly"/>).
/// Без запомненного — десинк включается обратно: им программа и работает
/// по умолчанию.
/// </para>
/// </remarks>
public static class VpnOnly
{
    /// <summary>Сейчас всё идёт через VPN: туннель есть, десинка нет.</summary>
    public static bool IsOn(AppSettings settings) =>
        settings.Engines is { Tunnel: true, Desync: false };

    /// <summary>Можно ли включить: туннелю есть куда вести.</summary>
    public static bool CanTurnOn(AppSettings settings) => settings.HasTunnelExit;

    /// <summary>Настройки с переключённым состоянием.</summary>
    public static AppSettings Toggle(AppSettings settings)
    {
        var engines = settings.Engines;

        if (IsOn(settings))
        {
            var back = engines with
            {
                Desync = settings.DesyncBeforeVpnOnly ?? true,
                Tunnel = settings.TunnelBeforeVpnOnly ?? true,
            };

            // Запомненное — то же «всё через VPN» (так и стояло до нажатия)
            // или его нет вовсе: возвращаем десинк, им программа работает.
            if (back is { Desync: false, Tunnel: true })
                back = back with { Desync = true };

            return settings.With(back) with { DesyncBeforeVpnOnly = null, TunnelBeforeVpnOnly = null };
        }

        return settings.With(engines with { Desync = false, Tunnel = true }) with
        {
            DesyncBeforeVpnOnly = engines.Desync,
            TunnelBeforeVpnOnly = engines.Tunnel,
        };
    }
}
