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
    public void Same_names_inside_one_subscription_are_numbered_like_the_engine_does()
    {
        // Сборка конфига разводит одинаковые теги суффиксом « #2»; пул делает
        // это сам, чтобы окно и движок звали сервер одинаково.
        var tags = SubscriptionPool.Tag([("Trust", [Server("proxy", "a.example"), Server("proxy", "b.example"), Server("proxy", "c.example")])]);

        Assert.Equal(["proxy", "proxy #2", "proxy #3"], tags[0]);
    }

    [Fact]
    public void Xray_configs_are_named_by_their_remarks()
    {
        var body = """
            [
              { "remarks": "🇩🇪 Германия 🚀", "outbounds": [
                  { "tag": "proxy", "protocol": "trojan", "settings": { "servers": [ { "address": "a.example", "port": 443, "password": "x" } ] } },
                  { "tag": "direct", "protocol": "freedom" } ] },
              { "remarks": "⬇️ Резерв ⬇️", "outbounds": [ { "tag": "direct", "protocol": "freedom" } ] },
              { "remarks": "🇪🇺 Авто", "outbounds": [
                  { "tag": "proxy", "protocol": "trojan", "settings": { "servers": [ { "address": "b.example", "port": 443, "password": "y" } ] } },
                  { "tag": "proxy-2", "protocol": "trojan", "settings": { "servers": [ { "address": "c.example", "port": 443, "password": "z" } ] } } ] }
            ]
            """;

        var (servers, _) = SubscriptionParser.ParseBody(body);

        // Один конфиг — один сервер: запасной proxy-2 балансира не показывается.
        Assert.Equal(["🇩🇪 Германия 🚀", "🇪🇺 Авто"], servers.Select(s => s.Tag));
    }

    [Fact]
    public void Different_sni_on_the_same_gateway_is_a_different_server()
    {
        // Trust: все страны на одном входе с одним ключом, различает SNI.
        var germany = Server("Германия", "gw.example") with { Sni = "de.example" };
        var france = Server("Франция", "gw.example") with { Sni = "fr.example" };

        var tags = SubscriptionPool.Tag([("Trust", [germany, france])]);

        Assert.Equal(["Германия", "Франция"], tags[0]);
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
            Age(folder, TimeSpan.FromHours(2));

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

    /// <summary>
    /// Запас убранной подписки стирается, запасы оставшихся — нет.
    /// </summary>
    /// <remarks>
    /// Владелец 30.09: в запасе — ключи серверов, и запас WOW VPN лежал
    /// после того, как подписку убрали. Метаданные (квота, срок) — вместе с телом.
    /// </remarks>
    [Fact]
    public async Task Only_reserves_of_remaining_subscriptions_are_kept()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nz-pool-" + Guid.NewGuid().ToString("N"));

        try
        {
            const string kept = "http://127.0.0.1:1/kept";
            const string gone = "http://127.0.0.1:1/gone";

            SubscriptionPool.SaveReserve(folder, kept, "trojan://secret@a.example:443#Остаётся\n",
                new SubscriptionInfo { Servers = [], TotalBytes = 100 });
            SubscriptionPool.SaveReserve(folder, gone, "trojan://secret@b.example:443#Уходит\n",
                new SubscriptionInfo { Servers = [], TotalBytes = 100 });

            Assert.Equal(1, SubscriptionPool.KeepReserves([kept, null, "  "], folder));

            // Оставшийся читается из запаса по-прежнему, убранного нет вовсе.
            var left = await SubscriptionPool.ReadOneAsync(kept, force: false, CancellationToken.None, folder);
            Assert.Equal("Остаётся", Assert.Single(left.Info!.Servers).Tag);

            Assert.Equal(2, Directory.GetFiles(folder).Length);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_fresh_reserve_is_read_without_going_to_the_panel()
    {
        // Владелец 28.09: «почему так часто проводится чтение подписок».
        // Свежий запас — ответ; к панели идут только силой (⟳, «Обновить»).
        var folder = Path.Combine(Path.GetTempPath(), "nz-pool-" + Guid.NewGuid().ToString("N"));

        try
        {
            const string url = "http://127.0.0.1:1/sub";
            var expires = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

            SubscriptionPool.SaveReserve(folder, url, "trojan://secret@a.example:443#Германия\n",
                new SubscriptionInfo { Servers = [], TotalBytes = 100, DownloadBytes = 40, ExpiresAt = expires });

            var read = await SubscriptionPool.ReadOneAsync(url, force: false, CancellationToken.None, folder);

            Assert.Equal(SubscriptionReadSource.Fresh, read.Source);
            Assert.Equal("Германия", Assert.Single(read.Info!.Servers).Tag);

            // Квота и срок приходят заголовками — запас хранит их рядом.
            Assert.Equal(60, read.Info.RemainingBytes);
            Assert.Equal(expires, read.Info.ExpiresAt);

            // Силой — к панели; она лежит, и тогда тот же запас, но с причиной.
            var forced = await SubscriptionPool.ReadOneAsync(url, force: true, CancellationToken.None, folder);

            Assert.Equal(SubscriptionReadSource.Reserve, forced.Source);
            Assert.NotNull(forced.Error);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Состаривает запас: свежий пул к панели не ходит.</summary>
    private static void Age(string folder, TimeSpan age)
    {
        foreach (var file in Directory.EnumerateFiles(folder))
            File.SetLastWriteTime(file, DateTime.Now - age);
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
