using NetZapret.Core;
using NetZapret.Subscriptions;

namespace NetZapret.Proxy;

/// <summary>
/// Замер всех серверов — «Замерить все» на вкладке VPN и тот же замер
/// при запуске программы.
/// </summary>
/// <remarks>
/// <para>
/// До 07.10 жил в окне вкладки, и замерить серверы мог только человек,
/// открывший её. Владелец 07.10: «добавь возможность включить это
/// в настройках впн» — замер при запуске (<c>AppSettings.MeasureOnStart</c>);
/// звать его надо и без вкладки, поэтому он здесь.
/// </para>
/// <para>
/// Продавцов не бить залпом (CLAUDE.md, «Туннель и серверы»): у Trust все
/// страны — один вход с одним ключом, и пачка из 16–26 проверок разом
/// закрывала его на минуту. Работающий движок меряет свои выходы сам —
/// на вход по одному, всего не больше восьми (<see cref="ClashApi.MeasureGentlyAsync"/>).
/// Пробнику, которому раньше отдавалось до восьми серверов разом без оглядки
/// на вход, теперь серверы идут волнами — в волне не больше одного с входа.
/// </para>
/// </remarks>
public static class ServerSweep
{
    /// <summary>
    /// Меряет серверы и пишет замеры в <paramref name="health"/>; сохраняет в конце.
    /// </summary>
    /// <param name="servers">Замеряемые пробником (<see cref="ProxyServer.IsMeasurable"/>), с тегами пула.</param>
    /// <param name="singBox">Путь к sing-box для пробника.</param>
    /// <param name="engineRunning">Работает ли движок туннеля — тогда свои выходы меряет он.</param>
    /// <param name="onMeasured">Тег замеренного — по мере готовности, из любого потока.</param>
    /// <param name="settings">
    /// Адрес проверки, «лучший из двух» и фрагментация (<see cref="Ping"/>);
    /// <c>null</c> — значения по умолчанию.
    /// </param>
    /// <returns>Сколько серверов ответило.</returns>
    /// <exception cref="OperationCanceledException">Замер прерван; замеренное не сохраняется.</exception>
    public static async Task<int> RunAsync(
        IReadOnlyList<ProxyServer> servers,
        string singBox,
        bool engineRunning,
        ServerHealthCache health,
        Action<string>? onMeasured,
        CancellationToken cancellationToken,
        AppSettings? settings = null,
        string selectorGroup = "auto")
    {
        settings ??= new AppSettings();

        var url = Ping.UrlOf(settings);
        int attempts = Ping.AttemptsOf(settings);

        var sync = new object();

        void Record(string tag, bool success, double? latencyMs)
        {
            // Прерванный замер — не «не отвечает».
            if (cancellationToken.IsCancellationRequested)
                return;

            lock (sync)
            {
                health.Set(new ServerHealth
                {
                    Tag = tag,
                    Success = success,
                    LatencyMs = latencyMs,
                    CheckedAt = DateTimeOffset.Now,
                });
            }

            onMeasured?.Invoke(tag);
        }

        var rest = servers;

        // Движок поднят — всё, что лежит в конфиге, меряет сам движок: без
        // процесса на сервер, как у пробника (владелец 28.09: «ускорь проверку
        // ключей»). Пробнику остаётся то, чего в движке нет.
        if (engineRunning)
        {
            using var api = new ClashApi();

            if (await api.MembersAsync(selectorGroup, cancellationToken) is { } members)
            {
                var inEngine = members.ToHashSet(StringComparer.Ordinal);

                rest = servers.Where(s => !inEngine.Contains(s.Tag)).ToList();

                await api.MeasureGentlyAsync(
                    servers.Where(s => inEngine.Contains(s.Tag)).Select(s => (s.Tag, Entry(s))).ToList(),
                    url,
                    TimeSpan.FromSeconds(5),
                    (tag, delay) => Record(tag, delay is not null, delay?.TotalMilliseconds),
                    cancellationToken,
                    attempts: attempts);
            }
        }

        var probe = new ProxyProbe(singBox);

        // Внешний адрес здесь не нужен, а его поиск стоит секунд на каждом сервере.
        // Выбранный адрес проверки — первым, прочие — запасом: отказ одного
        // адреса не должен выглядеть отказом сервера.
        var defaults = new ProbeOptions();

        var options = defaults with
        {
            LookupExternalIp = false,
            LogLevel = "warn",
            ConnectivityUrls = [url, .. defaults.ConnectivityUrls.Where(u => !string.Equals(u, url, StringComparison.OrdinalIgnoreCase))],
            Attempts = attempts,
            Fragment = settings.TlsFragment,
        };

        foreach (var wave in Waves(rest))
        {
            await probe.RunManyAsync(
                wave,
                options,
                result => Record(result.ServerTag, result.Success, result.Latency?.TotalMilliseconds),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
        }

        health.Save();

        return servers.Count(s => health.Find(s.Tag) is { Success: true });
    }

    /// <summary>
    /// Меряет выходы руками работающего движка — тех, кого пробник не берёт.
    /// </summary>
    /// <remarks>
    /// У MASQUE (WARP) учётная запись лежит в кэше движка, а файл занят им же,
    /// и отдельный экземпляр обязан регистрироваться заново — дозвониться ему
    /// для этого не через что. Движок же меряет свой выход сам, и меряет именно
    /// тот, через который пойдёт трафик. Движок не работает — мерить нечем.
    /// </remarks>
    public static async Task ThroughEngineAsync(
        IEnumerable<ProxyServer> servers,
        ServerHealthCache health,
        CancellationToken cancellationToken,
        AppSettings? settings = null)
    {
        var list = servers.ToList();

        if (list.Count == 0)
            return;

        settings ??= new AppSettings();

        using var api = new ClashApi();

        if (!await api.AliveAsync(cancellationToken))
            return;

        foreach (var server in list)
        {
            TimeSpan? delay = null;

            for (int attempt = 0; attempt < Ping.AttemptsOf(settings); attempt++)
                delay = Ping.Best([delay, await api.MeasureAsync(server.Tag, Ping.UrlOf(settings), TimeSpan.FromSeconds(15), cancellationToken)]);

            health.Set(new ServerHealth
            {
                Tag = server.Tag,
                Success = delay is not null,
                LatencyMs = delay?.TotalMilliseconds,
                CheckedAt = DateTimeOffset.Now,
            });
        }

        health.Save();
    }

    /// <summary>
    /// Серверы волнами: в каждой не больше одного с одного входа.
    /// </summary>
    /// <remarks>
    /// Вход — адрес и порт: у Trust все страны на одном, у других продавцов
    /// у каждой страны свой. Первая волна — первые серверы всех входов, вторая —
    /// вторые, и так далее; порядок внутри входа — как в подписке.
    /// </remarks>
    public static IReadOnlyList<IReadOnlyList<ProxyServer>> Waves(IReadOnlyList<ProxyServer> servers)
    {
        var byEntry = servers
            .GroupBy(Entry, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.ToList())
            .ToList();

        int depth = byEntry.Count == 0 ? 0 : byEntry.Max(g => g.Count);

        return Enumerable.Range(0, depth)
            .Select(i => (IReadOnlyList<ProxyServer>)byEntry.Where(g => g.Count > i).Select(g => g[i]).ToList())
            .ToList();
    }

    private static string Entry(ProxyServer server) => $"{server.Host}:{server.Port}";
}
