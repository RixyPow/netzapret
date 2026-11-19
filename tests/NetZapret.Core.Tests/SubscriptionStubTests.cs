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

    /// <summary>
    /// Обсуждение №69: при лимите устройств панель отдаёт записи на 0.0.0.0:1 с текстом
    /// причины в имени — StubNotice собирает этот текст, а UnsupportedCount не считает их
    /// неподдерживаемыми протоколами.
    /// </summary>
    [Fact]
    public void ZeroAddressStubsBecomeAPanelNoticeInsteadOfUnsupported()
    {
        var stubs = Info(
            Server("0.0.0.0", "none", 1) with { Tag = "Вы достигли лимита устройств" },
            Server("0.0.0.0", "none", 1) with { Tag = "У нас есть тарифы" },
            Server("0.0.0.0", "none", 1) with { Tag = "на 5, 10, 50 устройств." });

        Assert.Equal(0, stubs.UnsupportedCount);
        Assert.Equal("Вы достигли лимита устройств · У нас есть тарифы · на 5, 10, 50 устройств.", stubs.StubNotice());

        // Когда рядом есть рабочий сервер, заглушка-разделитель предупреждением не становится.
        var mixed = Info(
            Server("0.0.0.0", "none", 1) with { Tag = "── Европа ──" },
            Server("de.example", "tls", 443) with { Tag = "Германия" });

        Assert.Equal(0, mixed.UnsupportedCount);
        Assert.Null(mixed.StubNotice());
    }
}
