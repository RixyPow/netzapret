using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetZapret.Core;
namespace NetZapret.Proxy;

/// <summary>Первый адаптер со шлюзом может оказаться чужим: выбираем LAN по подсети DNS.</summary>
public static class RouterDnsConnection
{
    public sealed record Lan(string Name, int Index, IPAddress LocalAddress, int PrefixLength);

    public static bool SameSubnet(IPAddress local, IPAddress remote, IPAddress mask)
    {
        var a = local.GetAddressBytes(); var b = remote.GetAddressBytes(); var m = mask.GetAddressBytes();
        return a.Length == 4 && b.Length == 4 && m.Length == 4
            && Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (b[i] & m[i]));
    }

    public static Lan Find(string address)
    {
        if (!RouterDnsSettings.TryNormalize(address, out var normalized))
            throw new ArgumentException("Неверный IPv4 DNS роутера.");
        var target = IPAddress.Parse(normalized);
        var candidates = new List<Lan>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                || new[] { "netzapret", "wintun", "sing-tun", "wireguard", "tap-windows", "vpn" }
                    .Any(m => (nic.Name + " " + nic.Description).Contains(m, StringComparison.OrdinalIgnoreCase)))
                continue;
            var properties = nic.GetIPProperties();
            foreach (var local in properties.UnicastAddresses)
            {
                if (local.Address.AddressFamily != AddressFamily.InterNetwork || local.IPv4Mask is null
                    || !SameSubnet(local.Address, target, local.IPv4Mask)) continue;
                if (local.Address.Equals(target))
                    throw new ArgumentException("Указан IP этого компьютера, а не DNS роутера.");
                if (properties.GetIPv4Properties() is { } v4)
                    candidates.Add(new(nic.Name, v4.Index, local.Address, local.PrefixLength));
            }
        }
        return candidates.OrderByDescending(c => c.PrefixLength).ThenBy(c => c.Index).FirstOrDefault()
            ?? throw new InvalidOperationException("Не найден активный Ethernet/Wi-Fi адаптер в подсети DNS роутера. Проверьте IP, подключение и маску сети.");
    }

    public static bool IsAnswer(ReadOnlySpan<byte> query, ReadOnlySpan<byte> response) =>
        query.Length >= 2 && response.Length >= 12 && query[0] == response[0] && query[1] == response[1]
        && (response[2] & 0x80) != 0;

    public static async Task<string> CheckAsync(string address, CancellationToken ct)
    {
        var lan = Find(address);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        if (OperatingSystem.IsWindows())
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31, IPAddress.HostToNetworkOrder(lan.Index));
        socket.Bind(new IPEndPoint(lan.LocalAddress, 0));
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Parse(address), 53), limit.Token);
            var query = DnsWire.Query("example.com", DnsWire.TypeA);
            await socket.SendAsync(query, SocketFlags.None, limit.Token);
            var buffer = new byte[4096];
            int size = await socket.ReceiveAsync(buffer, SocketFlags.None, limit.Token);
            if (!IsAnswer(query, buffer.AsSpan(0, size))) return "Получен некорректный DNS-ответ.";
            var answer = DnsWire.Parse(buffer.AsSpan(0, size));
            return answer is { Code: 0 } && answer.Addresses.Count > 0
                ? $"DNS ответил через {lan.Name}: {string.Join(", ", answer.Addresses)}. Это проверка DNS, не проверка PBR/VPN."
                : $"DNS ответил, но example.com не разрешён: RCODE={answer?.Code.ToString() ?? "неизвестен"}.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return "Нет UDP-ответа за 4 секунды. Можно сохранить адрес, но DNS может не работать. Проверьте dnsmasq и UDP/53 на LAN."; }
        catch (SocketException ex)
        { return $"Ошибка UDP: {ex.SocketErrorCode}. Проверьте IP роутера, адаптер и доступ к порту 53."; }
    }
}
