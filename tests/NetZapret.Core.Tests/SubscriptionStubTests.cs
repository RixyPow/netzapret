using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Заглушка панели для нелюбимого клиента (подписка Trust, 28.09).
/// </summary>
public sealed class SubscriptionStubTests
{
    private static ProxyServer Server(string host, string security, ushort port = 443) => new()
    {
        Protocol = ProxyProtocol.Vless,
        Tag = host,
        Host = host,
        Port = port,
        Credential = "PLACEHOLDER",
        Transport = "tcp",
        Security = security,
    };

    private static SubscriptionInfo Info(params ProxyServer[] servers) => new() { Servers = servers };

    [Fact]
    public void Many_servers_on_one_address_without_encryption_is_a_stub()
    {
        Assert.True(SubscriptionClient.LooksLikeStub(Info(
            Server("convert-flow.net", "none"), Server("convert-flow.net", "none"), Server("convert-flow.net", ""))));
    }

    [Fact]
    public void Real_subscriptions_are_not()
    {
        // Разные адреса.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("a.example", "none"), Server("b.example", "none"))));

        // Один адрес, но с Reality — так бывает у настоящих: один вход, много выходов.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("gw.example", "reality"), Server("gw.example", "reality"))));

        // Подписка из одного сервера.
        Assert.False(SubscriptionClient.LooksLikeStub(Info(Server("one.example", "none"))));
    }
}
