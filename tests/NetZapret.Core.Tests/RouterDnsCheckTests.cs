using System.Net;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Приговор проверки роутера (<see cref="RouterDnsConnection.Judge"/>) — на ответах
/// роутера владельца 15.10: закрытые имена — одна заглушка 81.200.2.238.
/// </summary>
public sealed class RouterDnsCheckTests
{
    private static IReadOnlyList<IPAddress> At(params string[] addresses) => addresses.Select(IPAddress.Parse).ToArray();

    private static readonly Dictionary<string, IReadOnlyList<IPAddress>> Honest = new()
    {
        ["rutracker.org"] = At("172.67.182.196", "104.21.32.39"),
        ["nnmclub.to"] = At("172.67.144.20", "104.21.95.93"),
        ["rutor.info"] = At("193.46.255.29"),
        ["linkedin.com"] = At("130.211.32.14"),
    };

    [Fact]
    public void OneStubForEveryBlockedNameIsSpoofing()
    {
        var router = Honest.Keys.ToDictionary(n => n, _ => At("81.200.2.238"));

        var check = RouterDnsConnection.Judge("Беспроводная сеть", router, Honest);

        Assert.Equal(RouterDnsFit.Spoofs, check.Fit);
        Assert.False(check.Allows);
        Assert.Contains("4 из 4", check.Text);
        Assert.Contains("81.200.2.238", check.Text);
    }

    [Fact]
    public void HonestAnswersFit()
    {
        var check = RouterDnsConnection.Judge("Беспроводная сеть", Honest, Honest);

        Assert.Equal(RouterDnsFit.Fits, check.Fit);
        Assert.True(check.Allows);
    }

    [Fact]
    public void AnotherCdnNodeIsNotSpoofing()
    {
        // У сети доставки узлов много: свой адрес у каждого имени — не подмена.
        var router = new Dictionary<string, IReadOnlyList<IPAddress>>(Honest)
        {
            ["rutracker.org"] = At("104.21.32.40"),
        };

        Assert.Equal(RouterDnsFit.Fits, RouterDnsConnection.Judge("Ethernet", router, Honest).Fit);
    }

    [Fact]
    public void EmptyAnswerToABlockedNameIsSpoofing()
    {
        // NXDOMAIN вместо адреса — так отвечают резолверы оператора.
        var router = new Dictionary<string, IReadOnlyList<IPAddress>>(Honest) { ["rutor.info"] = At() };

        Assert.Equal(RouterDnsFit.Spoofs, RouterDnsConnection.Judge("Ethernet", router, Honest).Fit);
    }

    [Fact]
    public void NothingToCompareWithIsUnknownButAllowed()
    {
        var check = RouterDnsConnection.Judge("Ethernet", Honest, new Dictionary<string, IReadOnlyList<IPAddress>>());

        Assert.Equal(RouterDnsFit.Unknown, check.Fit);
        Assert.True(check.Allows);
    }
}
