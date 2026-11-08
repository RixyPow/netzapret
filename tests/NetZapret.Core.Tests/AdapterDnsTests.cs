using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// DNS адаптеров по выбору человека (08.11, обсуждение №55): не трогать,
/// автоматически или выбранный резолвер. Сами команды netsh здесь не
/// выполняются — нужны права администратора; проверяется, какие и кому.
/// </summary>
public sealed class AdapterDnsTests
{
    private static readonly string[] Quad9 = ["9.9.9.10", "149.112.112.10"];

    private static DnsAdapter Adapter(string[] v4, string[]? v6 = null) =>
        new(22, "Беспроводная сеть", v4, v6 ?? []);

    [Theory]
    [InlineData("9.9.9.10,149.112.112.10", new[] { "9.9.9.10", "149.112.112.10" })]
    [InlineData("8.8.8.8 208.67.222.222", new[] { "8.8.8.8", "208.67.222.222" })]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void TheRegistryValueIsReadAsAList(string? value, string[] expected) =>
        Assert.Equal(expected, AdapterDns.Split(value));

    [Fact]
    public void AutomaticMeansNothingWrittenByHand()
    {
        Assert.True(AdapterDns.Matches(Adapter([]), AdapterDnsMode.Automatic, []));
        Assert.False(AdapterDns.Matches(Adapter(["8.8.8.8"]), AdapterDnsMode.Automatic, []));
        Assert.False(AdapterDns.Matches(Adapter([], ["2001:4860:4860::8888"]), AdapterDnsMode.Automatic, []));
    }

    [Fact]
    public void TheResolverMatchesOnlyItsOwnAddressesInOrder()
    {
        Assert.True(AdapterDns.Matches(Adapter(Quad9), AdapterDnsMode.Resolver, Quad9));
        Assert.False(AdapterDns.Matches(Adapter(["149.112.112.10", "9.9.9.10"]), AdapterDnsMode.Resolver, Quad9));
        Assert.False(AdapterDns.Matches(Adapter(Quad9, ["2620:119:35::35"]), AdapterDnsMode.Resolver, Quad9));
    }

    [Fact]
    public void KeepTouchesNothing()
    {
        Assert.True(AdapterDns.Matches(Adapter(["8.8.8.8"]), AdapterDnsMode.Keep, []));
        Assert.Empty(AdapterDns.Commands(Adapter(["8.8.8.8"], ["2001:4860:4860::8888"]), AdapterDnsMode.Keep, []));
    }

    /// <summary>Как у владельца 08.11: IPv4 и IPv6 вписаны руками — оба уходят к сети, по номеру адаптера.</summary>
    [Fact]
    public void AutomaticClearsBothFamiliesByIndex()
    {
        var commands = AdapterDns.Commands(
            Adapter(["8.8.8.8", "208.67.222.222"], ["2001:4860:4860::8888"]), AdapterDnsMode.Automatic, []);

        Assert.Equal(
            [
                "interface ipv4 set dnsservers name=22 source=dhcp",
                "interface ipv6 set dnsservers name=22 source=dhcp",
            ],
            commands.Select(c => string.Join(' ', c)));
    }

    /// <summary>
    /// IPv6 не вписан — ipv6 не трогается: у кого он на адаптере выключен
    /// (у potato2236 из №55), netsh на ipv6 отвечает ошибкой.
    /// </summary>
    [Fact]
    public void Ipv6IsLeftAloneWhenNothingIsWrittenThere()
    {
        Assert.DoesNotContain(
            AdapterDns.Commands(Adapter(["8.8.8.8"]), AdapterDnsMode.Automatic, []),
            c => c.Contains("ipv6"));

        Assert.Empty(AdapterDns.Commands(Adapter([]), AdapterDnsMode.Automatic, []));
    }

    [Fact]
    public void TheResolverIsWrittenFirstThenAdded()
    {
        var commands = AdapterDns.Commands(Adapter(["8.8.8.8"], ["2620:119:35::35"]), AdapterDnsMode.Resolver, Quad9);

        Assert.Equal(
            [
                "interface ipv4 set dnsservers name=22 source=static address=9.9.9.10 register=primary validate=no",
                "interface ipv4 add dnsservers name=22 address=149.112.112.10 index=2 validate=no",
                "interface ipv6 set dnsservers name=22 source=dhcp",
            ],
            commands.Select(c => string.Join(' ', c)));
    }

    [Fact]
    public void TheResolverAlreadyInPlaceNeedsNoCommands() =>
        Assert.Empty(AdapterDns.Commands(Adapter(Quad9), AdapterDnsMode.Resolver, Quad9));

    [Fact]
    public void TheChosenResolverGivesItsPlainAddresses()
    {
        var addresses = AdapterDns.ResolverAddresses(new AppSettings { DnsServer = "9.9.9.10" }, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(Quad9, addresses);
    }

    /// <summary>У Zapret DNS только DoH за Cloudflare — адаптеру вписать нечего.</summary>
    [Fact]
    public void AResolverWithoutPlainDnsIsRefused()
    {
        var addresses = AdapterDns.ResolverAddresses(new AppSettings { DnsServer = "104.21.15.208" }, out var refusal);

        Assert.Empty(addresses);
        Assert.Contains("Zapret DNS", refusal);
    }

    [Fact]
    public void RouterDnsIsRefusedAsAlreadyAutomatic()
    {
        AdapterDns.ResolverAddresses(new AppSettings { UseRouterDns = true }, out var refusal);

        Assert.Contains("автоматически", refusal);
    }

    [Fact]
    public void TheChoiceSurvivesSaving()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nz-adapter-dns-{Guid.NewGuid():N}.json");

        try
        {
            Assert.Equal(AdapterDnsMode.Keep, new AppSettings().AdapterDns);

            (AppSettings.Fresh with { AdapterDns = AdapterDnsMode.Resolver }).Save(path);

            Assert.Equal(AdapterDnsMode.Resolver, AppSettings.Load(path).AdapterDns);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NothingChangedIsSaidPlainly() =>
        Assert.Equal("«автоматически» уже стоит.", AdapterDnsResult.Nothing.Describe(AdapterDnsMode.Automatic));
}
