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

    /// <summary>
    /// Остаток трафика — из запаса, без панели: он нужен предупреждению
    /// перед замером скорости (30.09), и ходить ради него в сеть незачем.
    /// </summary>
    [Fact]
    public void The_kept_answer_gives_the_quota_without_the_panel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nz-pool-" + Guid.NewGuid().ToString("N"));

        try
        {
            // Адрес, по которому никто не слушает: обратись мы к панели — был бы отказ.
            const string url = "http://127.0.0.1:1/sub";

            Assert.Null(SubscriptionPool.Kept(url, folder));

            SubscriptionPool.SaveReserve(folder, url, "trojan://secret@a.example:443#Германия\n",
                new SubscriptionInfo { Servers = [], TotalBytes = 100, DownloadBytes = 40 });

            // Старый запас годится тоже: свежесть здесь никто не спрашивает.
            Age(folder, TimeSpan.FromDays(3));

            var kept = SubscriptionPool.Kept(url, folder);

            Assert.NotNull(kept);
            Assert.Equal(60, kept.Value.Info.RemainingBytes);
            Assert.Equal("Германия", Assert.Single(kept.Value.Info.Servers).Tag);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// Запуск из запаса моложе суток не ждёт панели (TRUST 16.10: 502 через 10 с).
    /// </summary>
    [Fact]
    public async Task Start_takes_a_day_old_reserve_without_waiting_for_the_panel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nz-pool-" + Guid.NewGuid().ToString("N"));

        // Панель, которая принимает соединение и молчит: без отсрочки пул
        // ждал бы её весь срок клиента.
        using var silent = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        silent.Start();
        var url = $"http://127.0.0.1:{((System.Net.IPEndPoint)silent.LocalEndpoint).Port}/sub";

        try
        {
            SubscriptionPool.SaveReserve(folder, url, "trojan://secret@a.example:443#Германия\n",
                new SubscriptionInfo { Servers = [], TotalBytes = 100 });
            Age(folder, TimeSpan.FromHours(5));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await SubscriptionPool.BuildAsync(
                [new PoolSource("Молчит", url)], CancellationToken.None, folder, deferPanels: true);

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2));

            var part = Assert.Single(result.Parts);
            Assert.True(part.Deferred);
            Assert.False(part.FromReserve);
            Assert.Equal("Германия", Assert.Single(result.Servers).Tag);
            Assert.Equal(100, part.Info!.TotalBytes);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Запас старше суток (после выходных) — панель ждут, как прежде.</summary>
    [Fact]
    public async Task An_old_reserve_still_waits_for_the_panel()
    {
        var folder = Path.Combine(Path.GetTempPath(), "nz-pool-" + Guid.NewGuid().ToString("N"));

        try
        {
            var dead = new PoolSource("Лежит", "http://127.0.0.1:1/sub");
            SubscriptionPool.SaveReserve(folder, dead.Url, "trojan://secret@a.example:443#Германия\n");
            Age(folder, TimeSpan.FromDays(2));

            var part = Assert.Single((await SubscriptionPool.BuildAsync([dead], CancellationToken.None, folder, deferPanels: true)).Parts);

            Assert.False(part.Deferred);
            Assert.True(part.FromReserve);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Servers_are_the_same_regardless_of_order_and_differ_by_key()
    {
        var a = Server("A", "a.example");
        var b = Server("B", "b.example");

        Assert.True(SubscriptionPool.SameServers([a, b], [b, a]));
        Assert.False(SubscriptionPool.SameServers([a, b], [a, b with { Credential = "other" }]));
        Assert.False(SubscriptionPool.SameServers([a, b], [a]));
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

    /// <summary>
    /// Молчащая панель не держит чужую (22.10: Secureway молчала 12 с,
    /// и TRUST на вкладке VPN ждала её, хотя сама отвечает за полсекунды).
    /// </summary>
    [Fact]
    public async Task A_silent_panel_does_not_hold_up_another()
    {
        var silent = new TaskCompletionSource();
        var read = new List<string>();

        var all = SubscriptionPool.ByPanel(
            ["https://slow.example/sub/1", "https://fast.example/sub/2"],
            url => url,
            async url =>
            {
                if (url.Contains("slow"))
                    await silent.Task;

                lock (read)
                    read.Add(url);
            });

        Assert.Equal(["https://fast.example/sub/2"], read);
        Assert.False(all.IsCompleted);

        silent.SetResult();
        await all;

        Assert.Equal(2, read.Count);
    }

    /// <summary>Подписки одной панели — по очереди: залпа с одного адреса панель не любит.</summary>
    [Fact]
    public async Task One_panel_is_read_in_turn()
    {
        var first = new TaskCompletionSource();
        var started = new List<string>();

        var all = SubscriptionPool.ByPanel(
            ["https://panel.example/sub/1", "https://panel.example/sub/2"],
            url => url,
            async url =>
            {
                started.Add(url);

                if (url.EndsWith('1'))
                    await first.Task;
            });

        Assert.Equal(["https://panel.example/sub/1"], started);

        first.SetResult();
        await all;

        Assert.Equal(["https://panel.example/sub/1", "https://panel.example/sub/2"], started);
    }
}
