using NetZapret.Core.Rules;

namespace NetZapret.Proxy;

/// <summary>Что в hosts сейчас стоит по особому маршруту.</summary>
/// <param name="Pinned">Сколько записанных имён стоит в hosts на записанных адресах.</param>
/// <param name="Total">Сколько имён записано.</param>
/// <param name="Moved">Имена, стоящие в hosts на других адресах: имя — что там сейчас.</param>
/// <param name="Missing">Записанные имена, которых в hosts нет вовсе.</param>
public sealed record RoutePinState(
    int Pinned,
    int Total,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Moved,
    IReadOnlyList<string> Missing)
{
    public bool Whole => Total > 0 && Pinned == Total;
}

/// <summary>
/// Пины особых маршрутов: запись в <c>rules.user.yaml</c> ставит и снимает их в hosts.
/// </summary>
/// <remarks>
/// <para>
/// Пины стоят в hosts и только там (решение владельца 13.11: файл маршрутов,
/// собиравший hosts, когда-то уже пробовали — и он всё ломал). Файл маршрутов
/// не собирает hosts заново при каждом запуске — он лишь говорит, какие строки
/// дописать или сменить, когда маршрут выбран, подобран или загружен из чужого
/// файла. Строки чужого блока (Zapret GUI) не трогаются: за ними следит их хозяин.
/// </para>
/// <para>
/// Пин сервиса пишется с пометкой десинка, пин посредника — без неё и с профилем
/// посредника (<see cref="HostsEditor.ViaMark"/>), чтобы его вела автозамена.
/// </para>
/// </remarks>
public static class RoutePins
{
    /// <summary>Посредник по записи <c>pin:</c> — по имени («Zapret DNS») или ключу («zapret»).</summary>
    public static Intermediary? Relay(string? pin) =>
        pin is null
            ? null
            : IntermediaryDns.ByKey(pin)
              ?? IntermediaryDns.Servers.FirstOrDefault(s => string.Equals(s.Name, pin.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Решение «пина сервиса» по записи <c>pin:</c>.</summary>
    public static PinSolution? Solution(string? pin, IReadOnlyList<PinSolution>? solutions = null) =>
        pin is null
            ? null
            : (solutions ?? PinSolutions.Load()).FirstOrDefault(s => string.Equals(s.Id, pin.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Имена и адреса, которые запись ставит в hosts; пусто — ставить нечего.
    /// </summary>
    /// <remarks>Без IPv6 в сети адреса IPv6 не пишутся: Windows пробует их первыми и ждёт.</remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Wanted(
        UserRuleEntry entry,
        bool haveIpV6,
        IReadOnlyList<PinSolution>? solutions = null)
    {
        if (entry.Off || !entry.Enabled)
            return UserRuleEntry.NoHosts;

        return entry.Special switch
        {
            RouteSpecial.ServicePin when Solution(entry.Pin, solutions) is { } solution
                => PinSolutions.Entries(solution, haveIpV6),
            RouteSpecial.RelayPin => entry.PinHosts
                .Select(p => (p.Key, Value: (IReadOnlyList<string>)p.Value.Where(a => haveIpV6 || !a.Contains(':')).ToList()))
                .Where(p => p.Value.Count > 0)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase),
            _ => UserRuleEntry.NoHosts,
        };
    }

    /// <summary>
    /// Ставит пин записи в наш блок hosts; <c>null</c> — записи ставить нечего.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Нет прав на hosts.</exception>
    public static PinResult? Apply(
        UserRuleEntry entry,
        bool haveIpV6,
        string? hostsPath = null,
        IReadOnlyList<PinSolution>? solutions = null)
    {
        var wanted = Wanted(entry, haveIpV6, solutions);

        if (wanted.Count == 0)
            return null;

        var note = RouteSpecials.Describe(entry.Special, entry.Pin);

        if (entry.Special == RouteSpecial.ServicePin)
            return HostsEditor.PinWithDesync(wanted, hostsPath, note);

        // Профиль посредника — чтобы автозамена спрашивала о пине того, кто его отдал.
        var via = Relay(entry.Pin) is { } relay
            ? wanted.Keys.ToDictionary(n => n, _ => relay.Key, StringComparer.OrdinalIgnoreCase)
            : null;

        return HostsEditor.Repin(wanted, hostsPath, note, via);
    }

    /// <summary>
    /// Снимает пин записи — только имена, стоящие в нашем блоке на её же адресах.
    /// </summary>
    /// <remarks>
    /// Имя, которое человек после записи перебил своим адресом, — уже его выбор,
    /// и снимать его вместе с маршрутом нельзя (так же снимает решение
    /// <see cref="PinSolutions.Disable"/>).
    /// </remarks>
    /// <returns>Снятые имена; пусто — снимать было нечего.</returns>
    public static IReadOnlyList<string> Remove(
        UserRuleEntry entry,
        string? hostsPath = null,
        IReadOnlyList<PinSolution>? solutions = null)
    {
        var recorded = Wanted(entry with { Enabled = true }, haveIpV6: true, solutions);

        if (recorded.Count == 0)
            return [];

        var ours = HostsEditor.PinsAll(hostsPath);

        var drop = recorded
            .Where(p => ours.TryGetValue(p.Key, out var now) && now.Count > 0
                && now.All(a => p.Value.Contains(a, StringComparer.OrdinalIgnoreCase)))
            .Select(p => p.Key)
            .ToList();

        if (drop.Count > 0)
            HostsEditor.Unpin(drop, hostsPath);

        return drop;
    }

    /// <summary>
    /// Ставит пины всех особых записей — за загрузкой чужого файла маршрутов.
    /// </summary>
    /// <returns>Строки итога: что поставлено и что нет — для окна и журнала.</returns>
    public static IReadOnlyList<string> ApplyAll(
        IEnumerable<UserRuleEntry> entries,
        bool haveIpV6,
        string? hostsPath = null)
    {
        var solutions = PinSolutions.Load();
        var lines = new List<string>();

        foreach (var entry in entries.Where(e => e.Special is RouteSpecial.ServicePin or RouteSpecial.RelayPin))
        {
            var name = RulesShare.NameOf(entry);

            try
            {
                var result = Apply(entry, haveIpV6, hostsPath, solutions);

                lines.Add(result is null
                    ? $"{name}: пин не поставлен — {(entry.Special == RouteSpecial.ServicePin && Solution(entry.Pin, solutions) is null ? $"решения «{entry.Pin}» в этой версии нет" : "в записи нет адресов")}"
                    : result.Reverted is { } reverted
                        ? $"{name}: hosts откатили — {reverted}"
                        : $"{name}: {RouteSpecials.Describe(entry.Special, entry.Pin)} — поставлен");
            }
            catch (UnauthorizedAccessException)
            {
                lines.Add($"{name}: пин не поставлен — нужны права администратора на hosts");
            }
            catch (IOException ex)
            {
                lines.Add($"{name}: пин не поставлен — {ex.Message}");
            }
        }

        return lines;
    }

    /// <summary>
    /// Пины при замене всех маршрутов чужим файлом: прежние особые маршруты,
    /// которых в новом нет, снимают свои пины, новые — ставят.
    /// </summary>
    /// <returns>Строки итога для окна и журнала; пусто — пинов не касалось.</returns>
    public static IReadOnlyList<string> SwapBook(
        IReadOnlyList<UserRuleEntry> current,
        IReadOnlyList<UserRuleEntry> incoming,
        bool haveIpV6,
        string? hostsPath = null)
    {
        var lines = new List<string>();
        var solutions = PinSolutions.Load();

        foreach (var gone in current.Where(e => e.Special is RouteSpecial.ServicePin or RouteSpecial.RelayPin
                     && !incoming.Any(n => SamePin(n, e))))
        {
            try
            {
                var removed = Remove(gone, hostsPath, solutions);

                if (removed.Count > 0)
                    lines.Add($"{RulesShare.NameOf(gone)}: снят прежний {RouteSpecials.Describe(gone.Special, gone.Pin)} — имён {removed.Count}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                lines.Add($"{RulesShare.NameOf(gone)}: прежний пин не снят — {ex.Message}");
            }
        }

        lines.AddRange(ApplyAll(incoming, haveIpV6, hostsPath));
        return lines;
    }

    /// <summary>Та же запись с тем же пином: тип, значение, род и чей пин.</summary>
    private static bool SamePin(UserRuleEntry one, UserRuleEntry other) =>
        one.Match == other.Match
        && string.Equals(one.Value.Replace('\\', '/'), other.Value.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
        && one.Special == other.Special
        && string.Equals(one.Pin, other.Pin, StringComparison.OrdinalIgnoreCase);

    /// <summary>Сколько особых записей с пином в наборе — для вопроса перед загрузкой.</summary>
    public static int PinCount(IEnumerable<UserRuleEntry> entries) =>
        entries.Count(e => e.Special is RouteSpecial.ServicePin or RouteSpecial.RelayPin && !e.Off && e.Enabled);

    /// <summary>
    /// Что стоит в hosts по записи — любым блоком, нашим или чужим.
    /// </summary>
    /// <remarks>
    /// Чужой блок в счёт: у владельца все пины — в блоке Zapret GUI (52 на 05.11),
    /// и запись, совпавшая с ним, — правда, а не расхождение.
    /// </remarks>
    public static RoutePinState StateOf(
        UserRuleEntry entry,
        string? hostsPath = null,
        IReadOnlyList<PinSolution>? solutions = null)
    {
        var recorded = Wanted(entry with { Enabled = true }, haveIpV6: true, solutions);
        var now = Active(hostsPath);

        int pinned = 0;
        var moved = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var (name, addresses) in recorded)
        {
            if (!now.TryGetValue(name, out var there) || there.Count == 0)
                missing.Add(name);
            else if (there.Any(a => addresses.Contains(a, StringComparer.OrdinalIgnoreCase)))
                pinned++;
            else
                moved[name] = there;
        }

        return new RoutePinState(pinned, recorded.Count, moved, missing);
    }

    /// <summary>
    /// Особые маршруты для отчёта: что записано и что из этого стоит в hosts.
    /// </summary>
    /// <remarks>
    /// Расхождение — первым делом при «пин не работает»: запись могла пережить
    /// пин (hosts откатил защитник, имя перебито чужим блоком), и без этой
    /// сверки в отчёте видны только две половины — файл маршрутов и hosts.
    /// </remarks>
    public static string Report(IEnumerable<UserRuleEntry> entries, string? hostsPath = null)
    {
        var special = entries.Where(e => e.Special != RouteSpecial.None && !e.Off).ToList();
        var builder = new System.Text.StringBuilder();

        builder.AppendLine("Особые маршруты — из rules.user.yaml, сверены с hosts.");
        builder.AppendLine();

        if (special.Count == 0)
        {
            builder.AppendLine("Особых маршрутов нет.");
            return builder.ToString();
        }

        var solutions = PinSolutions.Load();

        foreach (var entry in special)
        {
            builder.AppendLine($"{RulesShare.NameOf(entry)} ({entry.Value}) — {RouteSpecials.Describe(entry.Special, entry.Pin)}"
                + $", режим {entry.Mode.ToString().ToLowerInvariant()}"
                + (entry.Recipe is { } recipe ? $", рецепт {recipe}" : string.Empty)
                + (entry.Enabled ? string.Empty : ", запись отключена"));

            if (entry.Special == RouteSpecial.TgProxy)
            {
                builder.AppendLine("  пинов нет: путь — прокси Telegram (tg-ws-proxy, см. summary.txt).");
                builder.AppendLine();
                continue;
            }

            if (entry.Special == RouteSpecial.ServicePin && Solution(entry.Pin, solutions) is null)
            {
                builder.AppendLine($"  решения «{entry.Pin}» в pin-solutions.yaml нет — пин не ставится.");
                builder.AppendLine();
                continue;
            }

            var state = StateOf(entry, hostsPath, solutions);
            builder.AppendLine($"  в hosts на записанных адресах: {state.Pinned} из {state.Total}");

            foreach (var (name, now) in state.Moved.OrderBy(p => p.Key, StringComparer.Ordinal))
                builder.AppendLine($"  {name}: в hosts {string.Join(", ", now)}, записано {string.Join(", ", Wanted(entry with { Enabled = true }, true, solutions)[name])}");

            if (state.Missing.Count > 0)
                builder.AppendLine($"  нет в hosts: {string.Join(", ", state.Missing.Take(20))}{(state.Missing.Count > 20 ? $" и ещё {state.Missing.Count - 20}" : string.Empty)}");

            if (entry.Special == RouteSpecial.RelayPin)
            {
                foreach (var (name, addresses) in entry.PinHosts.OrderBy(p => p.Key, StringComparer.Ordinal).Take(40))
                    builder.AppendLine($"  {name} → {string.Join(", ", addresses)}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>Действующие строки hosts: имя — адреса, первым — тот, что Windows возьмёт.</summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Active(string? hostsPath)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var entry in HostsEditor.Parse(hostsPath).Where(e => e.Enabled))
            {
                foreach (var name in entry.Names)
                {
                    if (!result.TryGetValue(name, out var list))
                        result[name] = list = [];

                    if (!list.Contains(entry.Address, StringComparer.OrdinalIgnoreCase))
                        list.Add(entry.Address);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase);
    }
}
