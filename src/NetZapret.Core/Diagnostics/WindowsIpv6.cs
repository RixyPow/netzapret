using Microsoft.Win32;

namespace NetZapret.Core.Diagnostics;

/// <summary>
/// Выключен ли IPv6 в Windows целиком — так, что новому адаптеру его не дать.
/// </summary>
/// <remarks>
/// <para>
/// Issue №34 (22.10): у человека туннель не поднимался ни в «Гибриде», ни
/// в «Туннеле» — sing-box падал при запуске «configure tun interface: set ipv6
/// address: Element not found». В реестре стояло <c>DisabledComponents</c>
/// = 0xffffffff (так IPv6 выключают «оптимизаторы»); галочка IPv6 на своём
/// адаптере не помогла — TUN новый адаптер, на него действует системный
/// запрет. Поставил 0, перезагрузился — туннель заработал.
/// </para>
/// <para>
/// Бит 0x10 — «IPv6 выключен на всех адаптерах, кроме туннельных». Туннельные
/// у Windows — ISATAP, 6to4, Teredo (тип 131); Wintun объявляет себя
/// виртуальным (тип 53) и под этот бит попадает. Один бит 0x01 (выключены
/// только Teredo и прочие туннели — частая настройка) нам не мешает и
/// выключенным не считается. Значение читается при загрузке Windows: после
/// правки без перезагрузки реестр и действительность расходятся.
/// </para>
/// <para>Только чтение: системные настройки мы не меняем.</para>
/// </remarks>
public static class WindowsIpv6
{
    private const string Key = @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters";

    /// <summary>Бит «выключить IPv6 на всех нетуннельных адаптерах».</summary>
    private const uint NonTunnel = 0x10;

    /// <summary>Выключен ли IPv6 для адаптеров вроде нашего TUN.</summary>
    public static bool Disabled()
    {
        // Вне Windows такого запрета нет — как «не прочли».
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Key);
            return Disables(key?.GetValue("DisabledComponents"));
        }
        catch (Exception)
        {
            // Не прочли — считаем, что IPv6 есть: так было всегда.
            return false;
        }
    }

    /// <summary>Значение <c>DisabledComponents</c> выключает IPv6 нашему адаптеру.</summary>
    /// <remarks>REG_DWORD приходит как <see cref="int"/>: 0xffffffff — это −1.</remarks>
    public static bool Disables(object? value) => value switch
    {
        int number => (unchecked((uint)number) & NonTunnel) != 0,
        long number => (unchecked((uint)number) & NonTunnel) != 0,
        _ => false,
    };
}
