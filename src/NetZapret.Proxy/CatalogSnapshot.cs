using System.Net.Sockets;
using NetZapret.Core;
using NetZapret.Core.Rules;

namespace NetZapret.Proxy;

/// <summary>Что вышло из снимка.</summary>
public sealed record CatalogSnapshotResult(
    IReadOnlyList<OwnCatalogEntry> Services,
    IReadOnlyList<PinCandidate> Intermediaries,
    int Records,
    int Addresses,
    int Reachable,
    int Hosts,
    int Working);

/// <summary>
/// Снимок рабочих записей каталога Zapret для каталога NetZapret.
/// </summary>
/// <remarks>
/// <para>
/// Решение владельца 23.09: «скопируй все рабочие записи с каталога запрета
/// в каталог нетзапрет». Прежде каталог только читался там, где стоит
/// Zapret, и без него автоподбору пина оставались настоящий адрес и свой
/// каталог.
/// </para>
/// <para>
/// «Рабочие» — проверенные тем же запросом, что у автоподбора (PinPicker):
/// сертификат этого сайта, ответ не отказ. Копировать всё подряд нельзя:
/// замер 23.09 — из одиннадцати посредников каталога пять молчали вовсе,
/// двое подсовывали чужой сертификат. Мёртвая запись в каталоге хуже
/// отсутствующей: окно предложит её как источник, и пин укажет в никуда.
/// </para>
/// <para>
/// Снимок стареет, как и сам каталог: адрес живёт, пока живёт узел за ним.
/// Поэтому он пересобирается перед выпуском, а в программе остаётся
/// автоподбор, проверяющий записи заново в момент закрепления.
/// </para>
/// </remarks>
public static class CatalogSnapshot
{
    /// <summary>Сколько имён за адресом делают его посредником, а не сайтом.</summary>
    public const int IntermediaryNames = 20;

    public static async Task<CatalogSnapshotResult> BuildAsync(
        IReadOnlyList<CatalogRecord> records,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        // Сперва адреса целиком: у мёртвого посредника восемьсот имён,
        // и проверять каждое — восемьсот таймаутов по шесть секунд.
        var addresses = records.Select(r => r.Address).Distinct().ToList();
        var reachable = new HashSet<string>();

        using (var slots = new SemaphoreSlim(64))
        {
            await Task.WhenAll(addresses.Select(async address =>
            {
                await slots.WaitAsync(cancellationToken);

                try
                {
                    if (await AcceptsAsync(address, cancellationToken))
                    {
                        lock (reachable)
                            reachable.Add(address);
                    }
                }
                finally
                {
                    slots.Release();
                }
            }));
        }

        progress?.Report($"адресов {addresses.Count}, принимают соединение {reachable.Count}");

        var pairs = records
            .Where(r => reachable.Contains(r.Address))
            .GroupBy(r => (Host: r.Host.ToLowerInvariant(), r.Address))
            .Select(g => g.First())
            .ToList();

        var probes = new List<(CatalogRecord Record, PinProbe Probe)>();
        int done = 0;

        using (var slots = new SemaphoreSlim(48))
        {
            await Task.WhenAll(pairs.Select(async record =>
            {
                await slots.WaitAsync(cancellationToken);

                try
                {
                    var probe = await PinPicker.ProbeAsync(
                        new PinCandidate(record.Address, PinSource.Catalog, record.Source),
                        record.Host,
                        cancellationToken);

                    lock (probes)
                        probes.Add((record, probe));

                    int now = Interlocked.Increment(ref done);

                    if (now % 100 == 0)
                        progress?.Report($"проверено {now} из {pairs.Count}");
                }
                finally
                {
                    slots.Release();
                }
            }));
        }

        // Лучший адрес каждому имени — тем же порядком, что у автоподбора.
        // Проверка на робота с российского узла в снимок не идёт: пин с ней
        // скорее всего упрётся в отказ по стране.
        var best = probes
            .Where(p => p.Probe.Verdict == PinVerdict.Works
                || p.Probe.Verdict == PinVerdict.Challenge && !p.Probe.ExitsInRussia)
            .GroupBy(p => p.Record.Host.ToLowerInvariant())
            .Select(g => g.OrderBy(p => p.Probe.Rank).First())
            .ToList();

        var services = best
            .GroupBy(p => (p.Record.Service, p.Record.Source, p.Record.Address))
            .OrderBy(g => g.Key.Service, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new OwnCatalogEntry
            {
                Name = $"{g.Key.Service} — {g.Key.Source}",
                Names = g.Select(p => p.Record.Host.ToLowerInvariant()).OrderBy(h => h).ToList(),
                Addresses = [g.Key.Address],
            })
            .ToList();

        // Посредник — адрес за многими именами, выручивший хоть одно.
        var intermediaries = records
            .Where(r => r.Names >= IntermediaryNames)
            .GroupBy(r => r.Address)
            .Where(g => probes.Any(p => p.Record.Address == g.Key && p.Probe.Usable))
            .OrderByDescending(g => g.First().Names)
            .Select(g => new PinCandidate(g.Key, PinSource.Pool, g.First().Source))
            .ToList();

        return new CatalogSnapshotResult(
            services,
            intermediaries,
            records.Count,
            addresses.Count,
            reachable.Count,
            records.Select(r => r.Host.ToLowerInvariant()).Distinct().Count(),
            best.Count);
    }

    /// <summary>Шапка файла снимка: что это, откуда и насколько свежо.</summary>
    public static IReadOnlyList<string> Header(CatalogSnapshotResult result, DateTime taken) =>
    [
        "Снимок рабочих записей каталога Zapret для каталога NetZapret.",
        "",
        "Собран командой «nz catalog» — руками не правится, следующая сборка",
        "перепишет. Свои записи — в catalog.yaml: он при обновлении сохраняется,",
        "а этот файл заменяется новым.",
        "",
        $"Снят {taken:dd.MM.yyyy HH:mm} с машины, где стоит Zapret: записей {result.Records},",
        $"адресов {result.Addresses}, принимали соединение {result.Reachable};",
        $"имён {result.Hosts}, рабочий адрес нашёлся у {result.Working}.",
        "",
        "Рабочая — значит сертификат этого сайта и ответ не отказ, как у автоподбора",
        "пина. Адреса стареют: узел за посредником живёт сколько живёт, поэтому",
        "автоподбор проверяет запись заново в момент закрепления.",
        "",
        "intermediaries — посредники: адреса, стоящие в каталоге за двадцатью",
        "и больше именами. Автоподбор пробует их для любого имени — они пропускают",
        "и то, чего в каталоге нет.",
    ];

    private static async Task<bool> AcceptsAsync(string address, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(4));

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, 443, limit.Token);
            return true;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
