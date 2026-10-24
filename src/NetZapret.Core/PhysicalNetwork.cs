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
/// <para>
/// Адаптеры виртуальных локалок (Radmin VPN, Hamachi) — только если другого
/// нет (<see cref="Pick"/>). Обсуждение №33, 22.10: у человека с Radmin «Обзор
/// резолверов» давал TIMEOUT всем четырём резолверам по DoH, DoT и UDP, а DNS
/// «авто» весь день считал прямой путь мёртвым и спрашивал имена через туннель.
/// Адаптер Radmin стоял в списке Windows раньше Ethernet, а брался первый
/// со шлюзом — Radmin даёт своему адаптеру шлюз в 26.0.0.0/8, и все «прямые»
/// проверки (их сокеты привязаны к этому адаптеру) уходили в его сеть.
/// Шлюза Radmin в отчёте человека нет — снят он не был; без такого шлюза
/// выбор остаётся прежним.
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
        var candidates = new List<Candidate>();

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

                if (props.GetIPv4Properties()?.Index is not { } index)
                    continue;

                candidates.Add(new Candidate(
                    index,
                    props.GatewayAddresses.Select(g => g.Address).ToList(),
                    props.UnicastAddresses.Select(u => u.Address).ToList()));
            }
        }
        catch (NetworkInformationException)
        {
            // Windows не отдала список адаптеров — считаем, что не нашли.
            return null;
        }

        return Pick(candidates);
    }

    /// <summary>Включённый адаптер-кандидат: индекс IPv4, шлюзы и свои адреса.</summary>
    internal sealed record Candidate(int Index, IReadOnlyList<IPAddress> Gateways, IReadOnlyList<IPAddress> Addresses);

    /// <summary>
    /// Первый адаптер со шлюзом IPv4, кроме виртуальных локалок; их — только
    /// если другого нет.
    /// </summary>
    internal static int? Pick(IReadOnlyList<Candidate> candidates)
    {
        static bool HasGateway(Candidate nic) => nic.Gateways.Any(g =>
            g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));

        static bool VirtualLan(Candidate nic) =>
            nic.Gateways.Concat(nic.Addresses).Any(a =>
                a.AddressFamily == AddressFamily.InterNetwork && Rules.LocalNetworks.IsVirtualLan(a));

        var withGateway = candidates.Where(HasGateway).ToList();

        return (withGateway.FirstOrDefault(nic => !VirtualLan(nic)) ?? withGateway.FirstOrDefault())?.Index;
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
