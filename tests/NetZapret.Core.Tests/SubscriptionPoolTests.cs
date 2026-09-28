using NetZapret.Subscriptions;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Пул серверов из нескольких подписок (0.9.0).
/// </summary>
/// <remarks>
/// Теги пула читают сборка конфига, замеры и выбор сервера. Разойдись они
/// хоть в одном месте — замеры ложились бы не на тот сервер, а «мёртвые»
/// выводились бы из автоподбора мимо цели.
/// </remarks>
public sealed class SubscriptionPoolTests
{
    private static ProxyServer Server(string tag, string host, string credential = "secret") => new()
    {
        Protocol = ProxyProtocol.Trojan,
        Tag = tag,
        Host = host,
        Port = 443,
        Credential = credential,
        Transport = "tcp",
        Security = "tls",
        Sni = host,
    };

    [Fact]
    public void One_subscription_keeps_its_tags()
    {
        // Кто не включал вторую подписку, не должен заметить пул вовсе:
        // прежние замеры и выбранный сервер привязаны к прежним тегам.
        var tags = SubscriptionPool.Tag([("SecureWay", [Server("🇩🇪 Германия", "a.example"), Server("🇫🇮 Финляндия", "b.example")])]);

        Assert.Equal(["🇩🇪 Германия", "🇫🇮 Финляндия"], tags[0]);
    }

    [Fact]
    public void Same_name_in_two_subscriptions_gets_a_label()
    {
        var tags = SubscriptionPool.Tag(
        [
            ("Основная", [Server("🇩🇪 Германия", "a.example"), Server("🇳🇱 Нидерланды", "n.example")]),
            ("SecureWay", [Server("🇩🇪 Германия", "b.example")]),
        ]);

        Assert.Equal("🇩🇪 Германия · Основная", tags[0][0]);
        Assert.Equal("🇳🇱 Нидерланды", tags[0][1]);
        Assert.Equal("🇩🇪 Германия · SecureWay", tags[1][0]);
    }

    [Fact]
    public void The_same_node_from_two_sellers_is_one_server()
    {
        // Один узел у двух продавцов — один и тот же сервер: один тег,
        // одна строка в конфиге, и метка ему не нужна, раз совпадения
        // имён между разными узлами нет.
        var tags = SubscriptionPool.Tag(
        [
            ("Основная", [Server("DE-1", "same.example")]),
            ("SecureWay", [Server("Германия", "same.example")]),
        ]);

        Assert.Equal("DE-1", tags[0][0]);
        Assert.Equal("DE-1", tags[1][0]);
    }

    [Fact]
    public void Different_key_on_the_same_address_is_a_different_server()
    {
        var tags = SubscriptionPool.Tag(
        [
            ("A", [Server("X", "same.example", "one")]),
            ("B", [Server("X", "same.example", "two")]),
        ]);

        Assert.Equal("X · A", tags[0][0]);
        Assert.Equal("X · B", tags[1][0]);
    }

    [Fact]
    public async Task A_dead_panel_falls_back_to_its_reserve_and_duplicates_are_merged()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nz-pool-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Порт 1 на петле — отказ сразу, без ожидания срока.
            var dead = new PoolSource("Лежит", "http://127.0.0.1:1/sub");
            SubscriptionPool.SaveReserve(folder, dead.Url, "trojan://secret@reserve.example:443#Запасной\n");

            var result = await SubscriptionPool.BuildAsync([dead], CancellationToken.None, folder);

            var part = Assert.Single(result.Parts);
            Assert.True(part.FromReserve);
            Assert.Null(part.Error);
            Assert.Equal("Запасной", Assert.Single(result.Servers).Tag);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_dead_panel_without_reserve_does_not_break_the_pool()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nz-pool-" + Guid.NewGuid().ToString("N"));

        var result = await SubscriptionPool.BuildAsync(
            [new PoolSource("Лежит", "http://127.0.0.1:1/sub")], CancellationToken.None, folder);

        var part = Assert.Single(result.Parts);
        Assert.False(part.FromReserve);
        Assert.NotNull(part.Error);
        Assert.Empty(result.Servers);
    }
}
