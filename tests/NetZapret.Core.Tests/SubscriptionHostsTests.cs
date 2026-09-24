using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Имена панелей для обхода туннеля: только имя узла, без ключа из пути.
/// </summary>
public sealed class SubscriptionHostsTests
{
    [Fact]
    public void OnlyTheHostnameIsTaken()
    {
        // Ключ подписки живёт в пути — он в конфиг попасть не должен.
        var hosts = SubscriptionHosts.From(["https://Panel.Example.NET/sub/SECRET-TOKEN?x=1"]);

        Assert.Equal(["panel.example.net"], hosts);
    }

    [Fact]
    public void RepeatsAndEmptiesAreDropped()
    {
        var hosts = SubscriptionHosts.From(
        [
            null,
            "",
            "   ",
            "не ссылка",
            "https://a.example/one",
            "https://A.example/two",
            "https://b.example/",
        ]);

        Assert.Equal(["a.example", "b.example"], hosts);
    }

    [Fact]
    public void BareAddressesNeedNoResolving()
    {
        var hosts = SubscriptionHosts.From(
        [
            "http://203.0.113.5:2096/sub/x",
            "https://[2001:db8::1]/sub/x",
            "vless://uuid@host.example:443",
        ]);

        Assert.Empty(hosts);
    }
}
