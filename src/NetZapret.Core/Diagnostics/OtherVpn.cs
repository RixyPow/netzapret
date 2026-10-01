using System.Diagnostics;
using System.Net.NetworkInformation;

namespace NetZapret.Core.Diagnostics;

/// <summary>Чужой VPN-клиент, запущенный рядом с нашим.</summary>
/// <param name="Name">Как его называют люди.</param>
/// <param name="Processes">Номера его процессов — чтобы можно было проверить.</param>
/// <param name="Service">
/// Служба, которая работает и при закрытом клиенте (<c>happd</c>). Сама по себе
/// не мешает: подключится — поднимет туннель, и его поймает <see cref="OtherVpnScan.Tunnels"/>.
/// </param>
public sealed record OtherVpnClient(string Name, IReadOnlyList<int> Processes, bool Service);

/// <summary>Поднятый туннель не нашего движка.</summary>
/// <param name="Name">Имя адаптера в Windows.</param>
/// <param name="Description">Чей драйвер: «sing-tun Tunnel», «Wintun…», «TAP-Windows…».</param>
public sealed record OtherTunnel(string Name, string Description);

/// <summary>Сетевой адаптер — ровно то, по чему судим о туннеле.</summary>
public sealed record AdapterInfo(string Name, string Description, NetworkInterfaceType Type, bool Up);

/// <summary>
/// Ищет VPN-клиенты и их туннели рядом с нашим.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 01.10 держал разом Happ, Zapret GUI, Zapret KVN и NetZapret.
/// Туннель KVN стоял рядом с нашим путём: Windows вела трафик то через
/// Wi-Fi, то в туннель, каждое новое имя разрешалось по 12 с, и замер
/// скорости не мог даже соединиться. До того такие клиенты были видны только
/// в отчёте (<c>network.txt</c>), а «Диагностика» знала лишь чужой десинк.
/// </para>
/// <para>
/// Поломкой считается поднятый чужой туннель, а не запущенный клиент: Happ
/// у владельца открыт почти всегда, и без подключения в режиме TUN он не
/// мешает никому. Запущенный клиент без туннеля — оговорка: подключится —
/// встанет рядом.
/// </para>
/// <para>
/// Только чтение, как и <see cref="OtherBypassScan"/>: закрывать чужое — решение человека.
/// </para>
/// </remarks>
public static class OtherVpnScan
{
    /// <summary>Имя исполняемого файла без расширения → как его назвать.</summary>
    /// <remarks>
    /// sing-box и xray здесь же: Zapret KVN, v2rayN и Throne держат их отдельными
    /// процессами. Свои отличаем по номеру процесса из состояния надзора.
    /// </remarks>
    internal static readonly (string Process, string Name)[] Known =
    [
        ("happ", "Happ"),
        (ServicePrefix + "happd", "Happ (служба)"),
        ("ZapretKVN", "Zapret KVN"),
        ("karing", "Karing"),
        ("Throne", "Throne"),
        ("v2rayN", "v2rayN"),
        ("v2ray", "V2Ray"),
        ("xray", "Xray"),
        ("hiddify", "Hiddify"),
        ("HiddifyNext", "Hiddify"),
        ("HiddifyCli", "Hiddify"),
        ("clash", "Clash"),
        ("clash-verge", "Clash Verge"),
        ("verge-mihomo", "Clash Verge (mihomo)"),
        ("mihomo", "mihomo"),
        ("nekoray", "NekoRay"),
        ("nekobox", "NekoBox"),
        ("AmneziaVPN", "AmneziaVPN"),
        ("Outline", "Outline"),
        ("wireguard", "WireGuard"),
        ("openvpn", "OpenVPN"),
        ("sing-box", "sing-box"),
    ];

    /// <summary>
    /// Пометка службы в <see cref="Known"/>: у владельца 01.10 служба Happ
    /// работала при закрытом Happ, и считать её запущенным клиентом значило
    /// держать в «Диагностике» вечную оговорку.
    /// </summary>
    private const string ServicePrefix = "service:";

    /// <param name="ours">Номера процессов нашего надзора — их не считаем.</param>
    /// <remarks>По одной записи на клиента: у Hiddify и Happ процессов по несколько.</remarks>
    public static IReadOnlyList<OtherVpnClient> Clients(IReadOnlySet<int> ours)
    {
        var found = new Dictionary<string, (List<int> Ids, bool Service)>(StringComparer.Ordinal);

        foreach (var (entry, name) in Known)
        {
            bool service = entry.StartsWith(ServicePrefix, StringComparison.Ordinal);
            var process = service ? entry[ServicePrefix.Length..] : entry;
            Process[] running;

            try
            {
                running = Process.GetProcessesByName(process);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var p in running)
            {
                using (p)
                {
                    if (ours.Contains(p.Id))
                        continue;

                    if (!found.TryGetValue(name, out var entryFound))
                        found[name] = entryFound = ([], service);

                    entryFound.Ids.Add(p.Id);
                }
            }
        }

        return found.Select(f => new OtherVpnClient(f.Key, f.Value.Ids, f.Value.Service)).ToList();
    }

    /// <summary>Поднятые туннели, кроме нашего.</summary>
    /// <param name="ourTunnel">Имя адаптера нашего движка (<c>interface_name</c> в конфиге sing-box).</param>
    public static IReadOnlyList<OtherTunnel> Tunnels(string ourTunnel)
    {
        NetworkInterface[] all;

        try
        {
            all = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return [];
        }

        return Foreign(
            all.Select(n => new AdapterInfo(n.Name, n.Description, n.NetworkInterfaceType,
                n.OperationalStatus == OperationalStatus.Up)),
            ourTunnel);
    }

    /// <summary>Чужие поднятые туннели из списка адаптеров.</summary>
    public static IReadOnlyList<OtherTunnel> Foreign(IEnumerable<AdapterInfo> adapters, string ourTunnel) =>
        adapters
            .Where(a => a.Up
                && IsTunnel(a.Description, a.Type)
                && !string.Equals(a.Name, ourTunnel, StringComparison.OrdinalIgnoreCase))
            .Select(a => new OtherTunnel(a.Name, a.Description))
            .ToList();

    /// <summary>
    /// Туннель ли это: Wintun, «sing-tun», TAP, WireGuard.
    /// </summary>
    /// <remarks>
    /// Служебные адаптеры Windows для IPv6 — Teredo, ISATAP, 6to4, IP-HTTPS —
    /// тоже значатся туннелями по типу, но к VPN отношения не имеют, и Teredo
    /// бывает поднят на обычной машине. Принять его за чужой VPN значило бы
    /// показывать красное там, где всё в порядке.
    /// </remarks>
    public static bool IsTunnel(string description, NetworkInterfaceType type)
    {
        foreach (var service in (string[])["Teredo", "ISATAP", "6to4", "IP-HTTPS", "IPHTTPS"])
        {
            if (description.Contains(service, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return description.Contains("tun", StringComparison.OrdinalIgnoreCase)
            || description.Contains("TAP", StringComparison.Ordinal)
            || description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
            || type == NetworkInterfaceType.Tunnel;
    }
}
