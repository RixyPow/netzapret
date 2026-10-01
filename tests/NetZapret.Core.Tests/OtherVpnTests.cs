using System.Net.NetworkInformation;
using NetZapret.Core.Diagnostics;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>Чужой туннель рядом с нашим — и только чужой.</summary>
public sealed class OtherVpnTests
{
    [Theory]
    [InlineData("sing-tun Tunnel")]                 // Zapret KVN, Happ на sing-box
    [InlineData("Wintun Userspace Tunnel")]
    [InlineData("WireGuard Tunnel")]
    [InlineData("TAP-Windows Adapter V9")]           // OpenVPN
    public void Vpn_adapters_are_tunnels(string description)
    {
        Assert.True(OtherVpnScan.IsTunnel(description, NetworkInterfaceType.Unknown));
    }

    /// <summary>
    /// Служебные адаптеры Windows для IPv6 — туннели по типу, но не VPN:
    /// Teredo бывает поднят на обычной машине, и красное было бы ложным.
    /// </summary>
    [Theory]
    [InlineData("Teredo Tunneling Pseudo-Interface")]
    [InlineData("Microsoft ISATAP Adapter")]
    [InlineData("Microsoft 6to4 Adapter")]
    [InlineData("Microsoft IP-HTTPS Platform Adapter")]
    public void Windows_ipv6_transition_adapters_are_not(string description)
    {
        Assert.False(OtherVpnScan.IsTunnel(description, NetworkInterfaceType.Tunnel));
    }

    [Fact]
    public void A_plain_network_card_is_not_a_tunnel()
    {
        Assert.False(OtherVpnScan.IsTunnel("Intel(R) Wi-Fi 6 AX200 160MHz", NetworkInterfaceType.Wireless80211));
    }

    /// <summary>
    /// Сборка у владельца 01.10: наш туннель и туннель Zapret KVN рядом.
    /// Свой не в счёт, выключенный — тоже: спорят за маршруты только поднятые.
    /// </summary>
    [Fact]
    public void Only_up_foreign_tunnels_count()
    {
        var adapters = new[]
        {
            new AdapterInfo("netzapret0", "sing-tun Tunnel", NetworkInterfaceType.Unknown, Up: true),
            new AdapterInfo("xftunb379af", "sing-tun Tunnel", NetworkInterfaceType.Unknown, Up: true),
            new AdapterInfo("OpenVPN TAP", "TAP-Windows Adapter V9", NetworkInterfaceType.Ethernet, Up: false),
            new AdapterInfo("Беспроводная сеть", "Intel(R) Wi-Fi 6 AX200 160MHz", NetworkInterfaceType.Wireless80211, Up: true),
        };

        var foreign = OtherVpnScan.Foreign(adapters, "netzapret0");

        Assert.Equal("xftunb379af", Assert.Single(foreign).Name);
    }

    /// <summary>Клиенты, с которыми владелец и пользователи сидели рядом на 01.10.</summary>
    [Theory]
    [InlineData("happ")]
    [InlineData("ZapretKVN")]
    [InlineData("karing")]
    [InlineData("Throne")]
    public void Known_clients_are_listed(string process)
    {
        Assert.Contains(OtherVpnScan.Known, k => k.Process == process);
    }
}
