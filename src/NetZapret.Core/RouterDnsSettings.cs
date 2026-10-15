using System.Net;
using System.Net.Sockets;
namespace NetZapret.Core;

public static class RouterDnsSettings
{
    /// <summary>Этот режим предназначен для LAN IPv4, а не публичного DNS, имени или петлевого адреса.</summary>
    public static bool TryNormalize(string? input, out string address)
    {
        address = string.Empty;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Split('.').Length != 4
            || text.Any(c => c != '.' && (c < '0' || c > '9'))
            || !IPAddress.TryParse(text, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return false;
        var b = ip.GetAddressBytes();
        bool lan = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168);
        if (!lan) return false;
        address = ip.ToString();
        return true;
    }

    public static AppSettings Select(AppSettings settings, string address)
    {
        if (!TryNormalize(address, out var normalized))
            throw new ArgumentException("Нужен IPv4 DNS роутера из локальной сети, например 192.168.1.1.");
        return settings with { UseRouterDns = true, RouterDnsAddress = normalized,
            DnsAuto = false, DnsVia = DnsRoute.Direct };
    }
}
