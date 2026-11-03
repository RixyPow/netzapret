using NetZapret.Core;

namespace NetZapret.Proxy;

/// <summary>Что сделало обновление пинов.</summary>
/// <param name="Changes">По строке на заменённый адрес: «87.228.47.204 → 87.228.47.201 (23 имени)».</param>
/// <param name="Dead">Мёртвые адреса, замены которым не нашлось.</param>
public sealed record PinRefreshResult(
    IReadOnlyList<string> Changes,
    IReadOnlyList<string> Dead,
    string? Backup,
    string? Error,
    int Checked = 0)
{
    /// <summary>Посредники, не ответившие по DoH ни по одному адресу.</summary>
    public IReadOnlyList<string> Silent { get; init; } = [];
}

/// <summary>
/// Сам меняет умерший адрес пина на живой, который посредник отдаёт сейчас.
/// </summary>
/// <remarks>
/// <para>
/// Просьба владельца 28.09: «если они меняются, то можно сделать чтоб пины
/// автоматически менялись». В тот день у XBOX DNS умер 87.228.47.204, и Claude
/// с ChatGPT легли до тех пор, пока владелец не нашёл новый адрес руками.
/// </para>
/// <para>
/// Осторожно, по трём правилам. Работающий пин не трогается вовсе. Живость
/// проверяется по одному имени на адрес, а не по каждому: 28.09 подбор пина
/// бил по посредникам сотнями соединений, и XBOX, возможно, закрыл за это
/// адрес владельца. Замена пишется, только если новый адрес сам проверен
/// тем же именем — иначе мёртвое сменилось бы мёртвым.
/// </para>
/// <para>
/// Проверять стоит при поднятых движках: часть посредников напрямую отвечает
/// только с десинком (.195 28.09). Без движков живой адрес выглядел бы мёртвым.
/// </para>
/// <para>
/// С 30.09 по 03.11 — только кнопкой на вкладке DNS (владелец: «даже не всегда
/// при запуске нужен»). С 03.11 ещё и сама, раз в сутки при поднятых движках
/// (владелец: «их банят только так… стоит внедрить автозамену», «каждый час —
/// перебор, лучше раз в сутки», «добавь возможность вкл выкл автозамену»):
/// выключатель <see cref="AppSettings.AutoRepin"/>, срок — <see cref="IsDue"/>.
/// В тот день у владельца 25 имён Google AI стояли на мёртвом GeoHide
/// 193.233.112.67 — с ноября его прокси не отвечают.
/// </para>
/// </remarks>
public static class PinRefresh
{
    /// <summary>Как часто проверять самой.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);

    /// <summary>Когда проверка прошла в последний раз — кнопкой или сама.</summary>
    public static string StampPath => Path.Combine("runtime", "pin-refresh.txt");

    /// <summary>Кнопка и суточная проверка не идут разом: обе пишут hosts.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Пора ли проверять самой: с прошлой проверки прошли сутки или её не было.</summary>
    public static bool IsDue(DateTimeOffset now, string? stampPath = null)
    {
        try
        {
            var text = File.ReadAllText(stampPath ?? StampPath).Trim();

            return !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                       System.Globalization.DateTimeStyles.AssumeUniversal, out var last)
                   || now - last >= Every
                   || last > now;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>Отметить проверку: следующая сама — через сутки.</summary>
    public static void Mark(DateTimeOffset now, string? stampPath = null)
    {
        var target = stampPath ?? StampPath;

        if (Path.GetDirectoryName(target) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        File.WriteAllText(target, now.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    public static async Task<PinRefreshResult> RunAsync(CancellationToken cancellationToken, string? path = null)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            return await CheckAsync(cancellationToken, path);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<PinRefreshResult> CheckAsync(CancellationToken cancellationToken, string? path)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>> pins;

        try
        {
            // Пины на адреса самого сервиса (пометка nz:desync, решения
            // «Напрямую») не обновляются: посредники отдали бы им свой прокси,
            // а живость такого адреса без десинка не видна — 06.10 у владельца
            // TCP к адресам Meta из каталога без десинка не соединялся вовсе.
            var own = HostsEditor.DesyncPins(path);

            pins = HostsEditor.PinsAll(path)
                .Where(p => !own.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            return new([], [], null, "hosts не прочитался: " + ex.GetBaseException().Message);
        }

        if (pins.Count == 0)
            return new([], [], null, null);

        // Живость адреса — по одному его имени: для SNI-прокси имя нужно,
        // а одного хватает, чтобы понять, отвечает ли адрес вообще.
        var byAddress = pins
            .SelectMany(p => p.Value.Select(a => (Address: a, Name: p.Key)))
            .Where(x => System.Net.IPAddress.TryParse(x.Address, out var ip) && !BlockCheck.IsStub(ip))
            .GroupBy(x => x.Address, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Name).First(), StringComparer.Ordinal);

        var alive = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var (address, name) in byAddress)
            alive[address] = await AnswersAsync(address, name, cancellationToken);

        // Не отвечает больше половины адресов разом — это сбой сети, а не смерть
        // адресов. 29.09 в 14:58 проверка сочла мёртвыми 68 имён сразу, среди
        // них заведомо живые, и «заменила» настоящие адреса Spotify
        // (35.186.224.x) на прокси XBOX. При таком раскладе не меняем ничего.
        int deadCount = alive.Values.Count(ok => !ok);

        if (alive.Count >= 4 && deadCount * 2 > alive.Count)
        {
            return new([], [], null,
                $"не ответили {deadCount} адресов из {alive.Count} — похоже на сбой сети, пины не тронуты", alive.Count);
        }

        var plan = Plan(pins, alive);

        if (plan.Count == 0)
            return new([], [], null, null, alive.Count);

        // Для каждого имени без живого адреса — что посредники отдают сейчас.
        var reached = await IntermediaryDns.ReachAsync(plan, cancellationToken);
        var live = reached.Answers;
        var silent = reached.Silent;

        // Новый адрес проверяется один раз — тем именем, которому он нужен первым.
        var checkedNew = new Dictionary<string, bool>(StringComparer.Ordinal);
        var entries = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in plan)
        {
            var fresh = new List<string>();

            foreach (var candidate in live.TryGetValue(name, out var c) ? c : [])
            {
                if (alive.TryGetValue(candidate.Address, out var known) && !known)
                    continue;

                if (!checkedNew.TryGetValue(candidate.Address, out var ok))
                    checkedNew[candidate.Address] = ok = await AnswersAsync(candidate.Address, name, cancellationToken);

                if (ok)
                    fresh.Add(candidate.Address);

                // Два адреса, как у посредника: Windows возьмёт второй, если первый молчит.
                if (fresh.Count == 2)
                    break;
            }

            if (fresh.Count > 0)
                entries[name] = fresh;
        }

        var dead = plan.Where(n => !entries.ContainsKey(n)).ToList();

        if (entries.Count == 0)
            return new([], dead, null, null, alive.Count) { Silent = silent };

        var changes = entries
            .GroupBy(e => $"{string.Join(", ", pins[e.Key].Order(StringComparer.Ordinal))} → {string.Join(", ", e.Value.Order(StringComparer.Ordinal))}")
            .Select(g => $"{g.Key} ({g.Count()} {Names(g.Count())})")
            .ToList();

        try
        {
            var result = HostsEditor.Repin(entries, path);

            if (path is null)
                HostsEditor.FlushDns();

            return result.RevertedBy is { } by
                ? new([], dead, result.Backup, $"hosts вернул к своему {by}", alive.Count) { Silent = silent }
                : new(changes, dead, result.Backup, null, alive.Count) { Silent = silent };
        }
        catch (Exception ex)
        {
            return new([], dead, null, "hosts не записался: " + ex.GetBaseException().Message, alive.Count) { Silent = silent };
        }
    }

    /// <summary>Имена, у которых не осталось ни одного живого адреса.</summary>
    /// <remarks>Имя, у которого жив хоть один адрес из двух, не трогаем: Windows до него дойдёт.</remarks>
    internal static IReadOnlyList<string> Plan(
        IReadOnlyDictionary<string, IReadOnlyList<string>> pins,
        IReadOnlyDictionary<string, bool> alive) =>
        pins
            .Where(p => p.Value.Count > 0 && p.Value.All(a => alive.TryGetValue(a, out var ok) && !ok))
            .Select(p => p.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    /// <summary>Отвечает ли адрес этим именем: соединение и хоть какой-то ответ сайта.</summary>
    /// <remarks>
    /// Отказ сайта (403) — тоже ответ: соединение прошло, посредник жив.
    /// Мёртвый — только тот, где не прошло ничего.
    /// </remarks>
    private static async Task<bool> AnswersAsync(string address, string name, CancellationToken cancellationToken)
    {
        var probe = await PinPicker.ProbeAsync(new PinCandidate(address, PinSource.Pool, "пин"), name, cancellationToken);
        return probe.Verdict != PinVerdict.Dead;
    }

    private static string Names(int count) => (count % 10, count % 100) switch
    {
        (1, not 11) => "имя",
        (2 or 3 or 4, not (12 or 13 or 14)) => "имени",
        _ => "имён",
    };
}
