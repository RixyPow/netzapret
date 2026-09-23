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
public sealed record PinProbe(
    PinCandidate Candidate,
    PinVerdict Verdict,
    int? Status,
    string? Colo,
    TimeSpan Elapsed,
    string Detail)
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
    public static async Task<IReadOnlyList<PinPick>> PickAsync(
        IReadOnlyList<string> hosts,
        Func<string, IReadOnlyList<PinCandidate>> known,
        IReadOnlyList<PinCandidate> pool,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var names = new SemaphoreSlim(4);

        // Посредник, выручивший одно имя, первым пробуется для следующих:
        // имена одного сервиса обычно ходят через одного и того же.
        var proven = new List<PinCandidate>();
        int done = 0;

        var work = hosts.Select(async host =>
        {
            await names.WaitAsync(cancellationToken);

            try
            {
                return await PickOneAsync(host, known(host), pool, proven, http, cancellationToken);
            }
            finally
            {
                names.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        });

        return await Task.WhenAll(work);
    }

    private static async Task<PinPick> PickOneAsync(
        string host,
        IReadOnlyList<PinCandidate> known,
        IReadOnlyList<PinCandidate> pool,
        List<PinCandidate> proven,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var tried = new List<PinProbe>();

        var honest = (await DohResolver.CandidatesAsync(host, http, cancellationToken))
            .Select(a => new PinCandidate(a, PinSource.Honest, "честный резолвер"));

        // Сначала то, что про это имя известно, — дёшево и без посредника,
        // если сайт закрыт не по стране. Сайт ответил сам — дальше не ищем.
        tried.AddRange(await ProbeAllAsync(host, Distinct(honest.Concat(known)), cancellationToken));

        if (!tried.Any(p => p.Verdict == PinVerdict.Works))
        {
            PinCandidate[] helpers;

            lock (proven)
                helpers = [.. proven];

            var fresh = Distinct(helpers.Concat(pool))
                .Where(c => tried.All(t => t.Candidate.Address != c.Address))
                .ToList();

            tried.AddRange(await ProbeAllAsync(host, fresh, cancellationToken));
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

        // Отвергнутых — по источнику и причине, а не по адресу: четыре адреса
        // честного резолвера с одним и тем же отказом — одна строка, не четыре.
        var rejected = main.Rejected
            .GroupBy(r => (r.Candidate.Label, Reason: Reason(r)))
            .Select(g => g.Count() > 1 ? $"{g.Key.Label} — {g.Key.Reason} ({g.Count()} адр.)" : $"{g.Key.Label} — {g.Key.Reason}")
            .Take(6)
            .ToList();

        if (rejected.Count > 0)
            lines.Add("Отвергнуты: " + string.Join("; ", rejected) + ".");

        var missing = picks.Where(p => p.Chosen is null).Select(p => p.Host).ToList();

        lines.Add(missing.Count == 0
            ? $"Подобрано для всех имён: {found.Count}."
            : $"Подобрано {found.Count} из {picks.Count}; без адреса и не прибиты: "
              + string.Join(", ", missing.Take(4)) + (missing.Count > 4 ? " и ещё…" : "."));

        return string.Join("\n", lines);

        static string Reason(PinProbe probe) => probe.Verdict switch
        {
            PinVerdict.Refused => $"отказ {probe.Status}",
            PinVerdict.Challenge => probe.Candidate.Source == PinSource.Honest || probe.ExitsInRussia
                ? "проверка на робота с российского адреса"
                : "проверка на робота, выбран другой",
            PinVerdict.Works => "работает, выбран другой",
            _ => probe.Detail,
        };
    }

    private static IReadOnlyList<PinCandidate> Distinct(IEnumerable<PinCandidate> candidates) =>
        candidates.GroupBy(c => c.Address).Select(g => g.OrderBy(c => c.Source).First()).ToList();

    private static async Task<PinProbe[]> ProbeAllAsync(
        string host,
        IReadOnlyList<PinCandidate> candidates,
        CancellationToken cancellationToken)
    {
        using var slots = new SemaphoreSlim(8);

        return await Task.WhenAll(candidates.Select(async candidate =>
        {
            await slots.WaitAsync(cancellationToken);

            try
            {
                return await ProbeAsync(candidate, host, cancellationToken);
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

            var detail = verdict switch
            {
                PinVerdict.Refused => $"ответ {status} — сайт отказывает",
                PinVerdict.Challenge => $"проверка на робота, {status}",
                _ => $"ответ {status}",
            } + (colo is null ? string.Empty : $", узел {colo}");

            return new PinProbe(candidate, verdict, status, colo, clock.Elapsed, detail);
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
