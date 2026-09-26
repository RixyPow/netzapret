using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Чем кончилась проверка кандидата.</summary>
public enum PinVerdict
{
    /// <summary>Сайт ответил сам и не отказал.</summary>
    Works,

    /// <summary>
    /// Cloudflare проверяет на робота. Браузер пройдёт, но что за проверкой —
    /// сайт или отказ по стране, — отсюда не видно.
    /// </summary>
    Challenge,

    /// <summary>Сайт ответил отказом: 403 или 451.</summary>
    Refused,

    /// <summary>Не соединилось, не то сертификат, не HTTP, тишина.</summary>
    Dead,
}

/// <summary>Исход проверки одного кандидата.</summary>
/// <param name="Redirect">Куда сайт отправил, если ответил переадресацией: имя из Location.</param>
public sealed record PinProbe(
    PinCandidate Candidate,
    PinVerdict Verdict,
    int? Status,
    string? Colo,
    TimeSpan Elapsed,
    string Detail,
    string? Redirect = null)
{
    /// <summary>
    /// Выход в России — по узлу Cloudflare, ответившему на запрос.
    /// </summary>
    /// <remarks>
    /// Узел виден в хвосте <c>CF-RAY</c>. Проверка на робота с московского
    /// узла почти наверняка упрётся в отказ по стране, а с франкфуртского —
    /// нет: 23.09 chatgpt.com по настоящему адресу отвечал 403 с DME,
    /// а через посредника — 403 с FRA, и в браузере работал только второй.
    /// </remarks>
    public bool ExitsInRussia => Colo is { } colo && RussianColos.Contains(colo);

    private static readonly HashSet<string> RussianColos = new(StringComparer.OrdinalIgnoreCase)
    {
        "DME", "SVO", "VKO", "LED", "KJA", "SVX", "OVB", "KZN", "ROV", "AER", "KRR", "UFA", "VVO", "KHV",
    };

    /// <summary>Чем меньше, тем лучше: сначала исход, потом источник, потом скорость.</summary>
    /// <remarks>
    /// Проверка на робота по настоящему адресу — всегда с российского адреса,
    /// какой бы узел ни ответил: сайт судит по нам, а не по узлу. Замер 23.09:
    /// настоящий адрес crunchyroll отвечал отказом и с DME, и с ARN.
    /// </remarks>
    internal (int, int, double) Rank => (
        Verdict switch
        {
            PinVerdict.Works => 0,
            PinVerdict.Challenge when !ExitsInRussia && Candidate.Source != PinSource.Honest => 1,
            PinVerdict.Challenge => 2,
            _ => 9,
        },
        (int)Candidate.Source,
        Elapsed.TotalMilliseconds);

    public bool Usable => Verdict is PinVerdict.Works or PinVerdict.Challenge;
}

/// <summary>Что выбрано для одного имени и что отвергнуто.</summary>
public sealed record PinPick(string Host, PinProbe? Chosen, IReadOnlyList<PinProbe> Rejected);

/// <summary>
/// Подбирает адрес для пина: собирает кандидатов и проверяет каждого.
/// </summary>
/// <remarks>
/// <para>
/// Прежде источник выбирал человек — набор каталога, честный резолвер или
/// свой каталог, — и какой из них сработает, заранее не знал никто:
/// посредник живёт, пока живёт узел за ним. Замер 23.09 на crunchyroll.com:
/// пять наборов из семи молчали, один отвечал на chatgpt.com и молчал
/// на crunchyroll, и только один отдавал его из Стокгольма; настоящий адрес
/// отвечал 1009 — «из России нельзя». Окно предлагало лишь его.
/// </para>
/// <para>
/// Проверка строже прежней. «Отвечает ли адрес» мало: настоящий адрес
/// отвечает всегда, но отказом по стране. Поэтому сертификат обязан быть
/// этого сайта — посредник, подменяющий его, нам не годится, — а ответ
/// не должен быть 403 или 451.
/// </para>
/// </remarks>
public static class PinPicker
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Подбирает адрес для каждого имени.
    /// </summary>
    /// <param name="hosts">Имена, которые будут прибиты.</param>
    /// <param name="known">Кандидаты, известные для имени заранее: свой каталог, ответы наборов.</param>
    /// <param name="pool">Посредники, пробуемые для любого имени, в порядке доверия.</param>
    /// <param name="progress">Сколько имён готово.</param>
    /// <param name="probes">Каждая проверка по мере готовности — для живой таблицы в окне.</param>
    /// <remarks>
    /// Имена, на которые сайт переадресует, подбираются тоже. Замер 23.09:
    /// crunchyroll.com прибили к посреднику, он ответил 301 на
    /// www.crunchyroll.com — а тот прибит не был, браузер пошёл туда
    /// по обычному адресу и получил 1009. Список сервиса называл одну зону,
    /// а hosts зон не знает: каждое имя прибивается отдельно.
    /// </remarks>
    public static async Task<IReadOnlyList<PinPick>> PickAsync(
        IReadOnlyList<string> hosts,
        Func<string, IReadOnlyList<PinCandidate>> known,
        IReadOnlyList<PinCandidate> pool,
        IProgress<int>? progress,
        CancellationToken cancellationToken,
        IProgress<(string Host, PinProbe Probe)>? probes = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var names = new SemaphoreSlim(4);

        // Посредник, выручивший одно имя, первым пробуется для следующих:
        // имена одного сервиса обычно ходят через одного и того же.
        var proven = new List<PinCandidate>();
        int done = 0;

        var seen = new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase);
        var picks = new List<PinPick>();
        IReadOnlyList<string> round = hosts;

        // Два круга переадресаций хватает: голое имя → www, изредка ещё
        // региональное. Больше — уже не тот сайт, а чужая цепочка.
        for (int depth = 0; depth < 3 && round.Count > 0; depth++)
        {
            var work = round.Select(async host =>
            {
                await names.WaitAsync(cancellationToken);

                try
                {
                    return await PickOneAsync(host, known(host), pool, proven, http, probes, cancellationToken);
                }
                finally
                {
                    names.Release();
                    progress?.Report(Interlocked.Increment(ref done));
                }
            });

            var found = await Task.WhenAll(work);
            picks.AddRange(found);

            round = found
                .Where(p => p.Chosen?.Redirect is { } next && SameSite(p.Host, next))
                .Select(p => p.Chosen!.Redirect!)
                .Where(seen.Add)
                .ToList();
        }

        return picks;
    }

    /// <summary>
    /// Один ли это сайт: совпадают два последних уровня имени.
    /// </summary>
    /// <remarks>
    /// Грубо — у co.uk ошибётся, — но цена ошибки мала: лишнее имя будет
    /// проверено и прибито, только если отвечает сертификатом своего сайта.
    /// </remarks>
    internal static bool SameSite(string a, string b)
    {
        static string Tail(string host) => string.Join('.', host.ToLowerInvariant().Split('.').TakeLast(2));

        return Tail(a) == Tail(b);
    }

    private static async Task<PinPick> PickOneAsync(
        string host,
        IReadOnlyList<PinCandidate> known,
        IReadOnlyList<PinCandidate> pool,
        List<PinCandidate> proven,
        HttpClient http,
        IProgress<(string Host, PinProbe Probe)>? probes,
        CancellationToken cancellationToken)
    {
        var tried = new List<PinProbe>();

        var honest = (await DohResolver.CandidatesAsync(host, http, cancellationToken))
            .Select(a => new PinCandidate(a, PinSource.Honest, "честный резолвер"));

        // Сначала то, что про это имя известно, — дёшево и без посредника,
        // если сайт закрыт не по стране. Сайт ответил сам — дальше не ищем.
        tried.AddRange(await ProbeAllAsync(host, Distinct(honest.Concat(known)), probes, cancellationToken));

        if (!tried.Any(p => p.Verdict == PinVerdict.Works))
        {
            PinCandidate[] helpers;

            lock (proven)
                helpers = [.. proven];

            var fresh = Distinct(helpers.Concat(pool))
                .Where(c => tried.All(t => t.Candidate.Address != c.Address))
                .ToList();

            tried.AddRange(await ProbeAllAsync(host, fresh, probes, cancellationToken));
        }

        var chosen = tried.Where(p => p.Usable).OrderBy(p => p.Rank).FirstOrDefault();

        if (chosen?.Candidate.Source is PinSource.Pool or PinSource.Catalog)
        {
            lock (proven)
            {
                if (proven.All(c => c.Address != chosen.Candidate.Address))
                    proven.Insert(0, chosen.Candidate);
            }
        }

        return new PinPick(host, chosen, tried.Where(p => p != chosen).OrderBy(p => p.Rank).ToList());
    }

    /// <summary>
    /// Кого выбрали и почему отвергли остальных — словами, для окна.
    /// </summary>
    /// <param name="focus">Имя, по которому рассказывать подробно: главное имя сервиса.</param>
    public static string Summarize(IReadOnlyList<PinPick> picks, string? focus)
    {
        var found = picks.Where(p => p.Chosen is not null).ToList();

        if (found.Count == 0)
        {
            return "Ни один адрес не подошёл: настоящие отказывают или молчат, посредники "
                + "каталога тоже. Пин тут не поможет — нужен туннель.";
        }

        var main = picks.FirstOrDefault(p => p.Chosen is not null
                && string.Equals(p.Host, focus, StringComparison.OrdinalIgnoreCase))
            ?? found[0];

        var chosen = main.Chosen!;
        var lines = new List<string>
        {
            $"{main.Host} → {chosen.Candidate.Address} ({chosen.Candidate.Label}, {chosen.Detail})."
                + (chosen.Verdict == PinVerdict.Challenge
                    ? " Сайт проверяет на робота — браузер проверку пройдёт."
                    : string.Empty),
        };

        var rejected = Grouped(main.Rejected).Take(6).ToList();

        if (rejected.Count > 0)
            lines.Add("Отвергнуты: " + string.Join("; ", rejected) + ".");

        var missing = picks.Where(p => p.Chosen is null).Select(p => p.Host).ToList();

        // Совет — по замеру, а не по имени сайта. 23.09 я сам объявил, что
        // crunchyroll пином не взять, по ответу 502 на корень API, — а API
        // через посредника отвечал; не хватало лишь прибитых имён.
        lines.Add(missing.Count == 0
            ? $"Подобрано для всех имён: {found.Count}."
            : $"Подобрано {found.Count} из {picks.Count}; без адреса и не прибиты: "
              + string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? " и ещё…" : ".")
              + " Если сайту нужны именно они, пином он целиком не заработает — надёжнее туннель.");

        lines.Add("Если сайт откроется пустым или без картинок — откройте его ещё раз и подберите "
            + "снова: имена, которые он запросил, подтянутся из кэша DNS.");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Почему отвергнуты кандидаты — строкой для журнала.
    /// </summary>
    /// <remarks>
    /// Прежде журнал писал «адрес не подобран, проверено 12» — и только.
    /// 26.09 так дважды подряд не нашёлся адрес Crunchyroll, который
    /// минутой раньше и десятью минутами позже отвечал исправно, и понять
    /// задним числом, кто и чем отказал, было нечем.
    /// </remarks>
    public static string Rejections(PinPick pick) =>
        pick.Rejected.Count == 0 ? "кандидатов не было" : string.Join("; ", Grouped(pick.Rejected));

    /// <summary>Отвергнутые — по источнику и причине, а не по адресу.</summary>
    /// <remarks>
    /// Четыре адреса честного резолвера с одним и тем же отказом — одна
    /// строка, не четыре.
    /// </remarks>
    private static IEnumerable<string> Grouped(IEnumerable<PinProbe> rejected) =>
        rejected
            .GroupBy(r => (r.Candidate.Label, Reason: Reason(r)))
            .Select(g => g.Count() > 1 ? $"{g.Key.Label} — {g.Key.Reason} ({g.Count()} адр.)" : $"{g.Key.Label} — {g.Key.Reason}");

    private static string Reason(PinProbe probe) => probe.Verdict switch
    {
        PinVerdict.Refused => $"отказ {probe.Status}",
        PinVerdict.Challenge => probe.Candidate.Source == PinSource.Honest || probe.ExitsInRussia
            ? "проверка на робота с российского адреса"
            : "проверка на робота, выбран другой",
        PinVerdict.Works => "работает, выбран другой",
        _ => probe.Detail,
    };

    private static IReadOnlyList<PinCandidate> Distinct(IEnumerable<PinCandidate> candidates) =>
        candidates.GroupBy(c => c.Address).Select(g => g.OrderBy(c => c.Source).First()).ToList();

    private static async Task<PinProbe[]> ProbeAllAsync(
        string host,
        IReadOnlyList<PinCandidate> candidates,
        IProgress<(string Host, PinProbe Probe)>? probes,
        CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(8);

        return await Task.WhenAll(candidates.Select(async candidate =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                var probe = await ProbeAsync(candidate, host, cancellationToken);
                probes?.Report((host, probe));
                return probe;
            }
            finally
            {
                slots.Release();
            }
        }));
    }

    /// <summary>Спрашивает у адреса главную страницу имени.</summary>
    public static async Task<PinProbe> ProbeAsync(
        PinCandidate candidate,
        string host,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Budget);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(candidate.Address, 443, limit.Token);

            // Сертификат проверяется по-настоящему. Посредник обязан пропускать
            // TLS насквозь; подсунувший свой сертификат читает трафик,
            // и такой нам не нужен при любом ответе.
            using var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);

            try
            {
                await ssl.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = host },
                    limit.Token);
            }
            catch (System.Security.Authentication.AuthenticationException)
            {
                return new PinProbe(candidate, PinVerdict.Dead, null, null, clock.Elapsed,
                    "сертификат не этого сайта");
            }

            var request = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {host}\r\n"
                + "User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                + "(KHTML, like Gecko) Chrome/140.0 Safari/537.36\r\n"
                + "Accept: text/html\r\nConnection: close\r\n\r\n");

            await ssl.WriteAsync(request, limit.Token);

            var buffer = new byte[16 * 1024];
            int read = await ssl.ReadAsync(buffer, limit.Token);

            var status = BlockCheck.ParseStatus(buffer, read);

            if (status is null)
                return new PinProbe(candidate, PinVerdict.Dead, null, null, clock.Elapsed, "ответ не HTTP");

            var ray = BlockCheck.HeaderValue(buffer, read, "CF-RAY");
            var colo = ray is not null && ray.LastIndexOf('-') is var dash and > 0 ? ray[(dash + 1)..] : null;

            var verdict = status is 403 or 451
                ? BlockCheck.IsBotChallenge(buffer, read) ? PinVerdict.Challenge : PinVerdict.Refused
                : PinVerdict.Works;

            var redirect = status is >= 300 and < 400
                && Uri.TryCreate(BlockCheck.HeaderValue(buffer, read, "Location"), UriKind.Absolute, out var location)
                    ? location.Host.ToLowerInvariant()
                    : null;

            var detail = verdict switch
            {
                PinVerdict.Refused => $"ответ {status} — сайт отказывает",
                PinVerdict.Challenge => $"проверка на робота, {status}",
                _ when redirect is not null && redirect != host => $"ответ {status} → {redirect}",
                _ => $"ответ {status}",
            } + (colo is null ? string.Empty : $", узел {colo}");

            return new PinProbe(candidate, verdict, status, colo, clock.Elapsed, detail, redirect);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PinProbe(candidate, PinVerdict.Dead, null, null, clock.Elapsed, "нет ответа");
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return new PinProbe(candidate, PinVerdict.Dead, null, null, clock.Elapsed, "соединение сорвалось");
        }
    }
}
