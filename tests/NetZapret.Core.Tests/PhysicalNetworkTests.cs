using System.Net;
using NetZapret.Core.Rules;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Выбор адаптера для «прямых» проверок: Radmin и Hamachi — только если
/// другого нет (обсуждение №33, 22.10).
/// </summary>
public sealed class PhysicalNetworkTests
{
    private static PhysicalNetwork.Candidate Nic(int index, string? gateway, params string[] addresses) => new(
        index,
        gateway is null ? [] : [IPAddress.Parse(gateway)],
        addresses.Select(IPAddress.Parse).ToList());

    [Fact]
    public void Radmin_listed_first_does_not_win_over_ethernet()
    {
        var radmin = Nic(7, "26.0.0.1", "26.12.34.56", "fe80::1");
        var ethernet = Nic(12, "192.168.1.1", "192.168.1.50");

        Assert.Equal(12, PhysicalNetwork.Pick([radmin, ethernet]));
    }

    [Fact]
    public void Hamachi_is_skipped_by_its_own_address_too()
    {
        // Шлюз у виртуальной локалки бывает и чужим — узнаём и по своему адресу.
        var hamachi = Nic(3, "10.0.0.1", "25.1.2.3");
        var wifi = Nic(9, "192.168.0.1", "192.168.0.20");

        Assert.Equal(9, PhysicalNetwork.Pick([hamachi, wifi]));
    }

    [Fact]
    public void A_virtual_lan_is_used_when_nothing_else_has_a_gateway()
    {
        var radmin = Nic(7, "26.0.0.1", "26.12.34.56");
        var cableOut = Nic(12, null, "169.254.10.10");

        Assert.Equal(7, PhysicalNetwork.Pick([radmin, cableOut]));
    }

    [Fact]
    public void Without_virtual_lans_the_first_with_a_gateway_wins_as_before()
    {
        Assert.Equal(5, PhysicalNetwork.Pick([Nic(4, null, "192.168.56.1"), Nic(5, "192.168.1.1", "192.168.1.2"), Nic(6, "10.0.0.1", "10.0.0.2")]));
        Assert.Null(PhysicalNetwork.Pick([Nic(4, null, "192.168.56.1"), Nic(5, "0.0.0.0", "192.168.1.2")]));
    }

    [Theory]
    [InlineData("26.0.0.1", true)]
    [InlineData("25.255.1.1", true)]
    [InlineData("2620:9b::1", true)]
    [InlineData("27.0.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("::ffff:26.1.2.3", true)]
    public void Virtual_lan_addresses_are_recognised(string address, bool expected)
    {
        Assert.Equal(expected, LocalNetworks.IsVirtualLan(IPAddress.Parse(address)));
    }
}
