using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetZapret.Core;

/// <summary>
/// Есть ли у машины выход в сеть мимо туннелей: физический адаптер с маршрутом по умолчанию.
/// </summary>
/// <remarks>
/// <para>
/// Без него отказы панелей подписок и выходов туннеля ничего не говорят о панелях
/// и выходах. 23.10 (выход из сна) и 24.10 (включение) Wi-Fi у владельца подключался
/// через 3–4 минуты (журнал WLAN: 10:27:37 и 11:30:40), а программа всё это время писала
/// «панель недоступна — соединение не установилось» и «похоже, мёртв выход подписки»;
/// владелец четырежды перезапускал движки и пришёл с «проблемой чтения подписок».
/// Первое же чтение после подключения прочло обе панели.
/// </para>
/// <para>
/// Туннель sing-box — тоже адаптер с адресом и шлюзом, и отличить его
/// можно только по имени: он наш, и назван нами.
/// </para>
/// </remarks>
public static class PhysicalNetwork
{
    /// <summary>Что сказать вместо отказа панели или выхода, когда сети нет.</summary>
    public const string Offline = "нет сети — компьютер не подключён к Wi-Fi или кабелю";

    /// <summary>
    /// Индекс физического адаптера с маршрутом по умолчанию; <c>null</c> — не нашли.
    /// </summary>
    public static int? Interface()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || nic.Name.StartsWith("netzapret", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var props = nic.GetIPProperties();

                if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                        && !g.Address.Equals(IPAddress.Any)))
                {
                    continue;
                }

                return props.GetIPv4Properties()?.Index;
            }
        }
        catch (NetworkInformationException)
        {
            // Windows не отдала список адаптеров — считаем, что не нашли.
        }

        return null;
    }

    /// <summary>Подключена ли машина к сети.</summary>
    public static bool Up() => Interface() is not null;

    /// <summary>
    /// Ждёт сети не дольше <paramref name="limit"/>; <c>true</c> — появилась.
    /// </summary>
    /// <remarks>
    /// Опросом раз в 2 с, а не событием <see cref="NetworkChange"/>: событие
    /// приходит и на подъём нашего же TUN, и проверять адаптеры пришлось бы всё равно.
    /// </remarks>
    public static async Task<bool> WaitAsync(TimeSpan limit, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + limit;

        while (!Up())
        {
            if (DateTime.UtcNow >= deadline)
                return false;

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return true;
    }
}
