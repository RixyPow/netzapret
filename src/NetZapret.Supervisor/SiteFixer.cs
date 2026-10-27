using System.Net.Http;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Supervisor;

/// <summary>Один опробованный путь и чем он кончился.</summary>
/// <param name="Try">Что пробовали (<see cref="RouteTry"/>); <c>null</c> — проверка «как есть».</param>
/// <param name="Recipe">Рецепт каталога — у шага с рецептом.</param>
public sealed record SiteFixStep(string Path, bool Works, string Detail, RouteTry? Try = null, string? Recipe = null);

/// <summary>Итог «Сайт не открывается».</summary>
/// <param name="Applied">Какой маршрут записан; <c>null</c> — ничего не менялось.</param>
/// <param name="NeedsRestart">Маршрут вступит в силу с перезапуском движков (VPN: подменные адреса раздаёт туннель; рецепт: свой профиль winws2).</param>
/// <param name="Recipe">Записанный рецепт десинка — название из каталога; <c>null</c> — без своего рецепта.</param>
/// <param name="Advice">Совет из таблицы «симптом — маршрут» (<see cref="RoutePlan.Advice"/>); <c>null</c> — нечего.</param>
/// <param name="Found">Какой путь открыл имя — и при подборе без записи (<see cref="SiteFixer.PickAsync"/>).</param>
/// <param name="RecipeId">Рецепт открывшего пути — идентификатор каталога, как его пишет правило.</param>
public sealed record SiteFixResult(
    string? Host,
    IReadOnlyList<SiteFixStep> Steps,
    RoutingMode? Applied,
    bool NeedsRestart,
    string Summary,
    string? Recipe = null,
    string? Advice = null,
    RoutingMode? Found = null,
    string? RecipeId = null);

/// <summary>
/// «Сайт не открывается»: подбирает маршрут по симптому и сам записывает сработавший.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 29.09: людей бесит ставить «напрямую» на каждый сервис руками.
/// Сайты ломает не блокировка, а десинк: списки пресета держат целые сети
/// Google и Cloudflare (часть — под чужими именами: Google /16 в списке
/// Discord, Cloudflare /16 в списке AnyDesk), и рецепт достаётся сайтам,
/// которые у провайдера и не закрыты, — aternos.org, translate.google.com.
/// </para>
/// <para>
/// С 27.10 — автоподбор (владелец: «прогоняем блокчек на конкретный домен и по
/// симптомам выбираем оптимальный маршрут»): проверка блокировок называет
/// симптом, <see cref="RoutePlan"/> по таблице «симптом — маршрут» — что пробовать
/// и в каком порядке, и каждый шаг пробуется вживую; берётся первый открывший.
/// </para>
/// <para>
/// Путь «напрямую» меряется честно, без перезапуска: имя на время кладётся
/// в щит (<c>runtime\desync-exclude.txt</c>), а winws2 перечитывает списки
/// сам при их изменении (документация bol-van: «перезапуск nfqws2 не нужен»).
/// Рецепты — пробным winws2 на своих портах (<see cref="RecipeTrial"/>), не трогая
/// работающий десинк. Путь через VPN — через вход проверки движка: он ведёт в туннель.
/// </para>
/// </remarks>
public static class SiteFixer
{
    /// <summary>Сколько ждать, пока winws2 перечитает щит после правки.</summary>
    private static readonly TimeSpan ShieldReload = TimeSpan.FromSeconds(2);

    /// <summary>Имя из того, что вставил человек: ссылки, имени, имени с путём.</summary>
    public static string? HostOf(string input)
    {
        var text = input.Trim().Trim('"', '\'');

        if (text.Length == 0)
            return null;

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.IdnHost))
            return null;

        var host = uri.IdnHost.ToLowerInvariant().TrimEnd('.');

        return host.Contains('.') ? host : null;
    }

    /// <summary>Значение правила: вся зона сайта, без www.</summary>
    public static string RuleValue(string host) =>
        "*." + (host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host);

    /// <param name="stepDone">Каждый опробованный шаг — сразу, как кончился: окно показывает ход, а не ждёт минуту.</param>
    public static Task<SiteFixResult> FixAsync(
        string input,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<SiteFixStep>? stepDone = null) =>
        RunAsync(input, write: true, recipes: true, progress, cancellationToken, stepDone);

    /// <summary>
    /// Тот же подбор, но без записи: какой путь открыл имя — в <see cref="SiteFixResult.Found"/>.
    /// </summary>
    /// <remarks>
    /// Для кнопки «Автоподбор» в окне пути маршрута (владелец 27.10: «я вижу это
    /// как кнопку автоподбор в выборе маршрута»). Записывать там должен сам маршрут,
    /// его список, а не новое правило на имя, — и только по «Готово»: окно отмечает
    /// найденный путь, человек соглашается. Щит после пробы «напрямую» поэтому
    /// возвращается всегда: отменённое окно не должно оставить имя без десинка.
    /// </remarks>
    /// <param name="recipes">Пробовать ли рецепты: у маршрутов-списков адресов своего рецепта нет.</param>
    public static Task<SiteFixResult> PickAsync(
        string host,
        bool recipes,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<SiteFixStep>? stepDone = null) =>
        RunAsync(host, write: false, recipes, progress, cancellationToken, stepDone);

    private static async Task<SiteFixResult> RunAsync(
        string input,
        bool write,
        bool recipes,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        IProgress<SiteFixStep>? stepDone)
    {
        var host = HostOf(input);
        var steps = new StepLog(stepDone);

        if (host is null)
            return new(null, steps, null, false, "Не понял адрес. Вставьте ссылку или имя сайта, например aternos.org.");

        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is null || !state.IsSupervisorAlive())
        {
            return new(host, steps, null, false,
                "Движки не запущены — проверять нечего: путь «напрямую» меряется через десинк, "
                + "путь через VPN — через туннель. Запустите движки на «Главной» и повторите.");
        }

        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var (engine, _) = NetZapret.Zapret.RuleSetExpander.LoadFor(settings);
        var decision = engine.Evaluate(ConnectionEvent.Describe(host));
        var route = settings.Engines.Effective(decision.Mode, settings.NeedsProxy);

        // Как есть — она же и называет симптом.
        progress?.Report($"Проверяю {host} как есть…");
        var now = await BlockCheck.CheckAsync(host, null, cancellationToken, throughTunnel: route == RoutingMode.Proxy);
        steps.Add(new($"как есть ({Word(route)})", now.Kind == BlockKind.None, Said(now)));

        var plan = RoutePlan.For(now, route, settings.NeedsDesync, settings.NeedsProxy);

        if (now.Kind == BlockKind.None)
        {
            var open = plan.Advice
                ?? "Если в браузере не открывается, дело не в маршруте: попробуйте окно инкогнито "
                    + "(браузер держит старые соединения) или другой браузер.";

            return new(host, steps, null, false,
                $"{host} открывается как есть ({Word(route)}) — маршрут менять незачем. {open}",
                Advice: open);
        }

        foreach (var step in plan.Steps.Where(s => recipes || s.Kind != RouteTry.Recipes))
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (step.Kind)
            {
                case RouteTry.Direct:
                {
                    progress?.Report($"Пробую {host} напрямую, без десинка…");

                    if (await TryShieldedAsync(host, keep: write, cancellationToken) is not { } direct)
                        break;

                    steps.Add(direct.Step);

                    if (!direct.Step.Works)
                        break;

                    if (!write)
                    {
                        return new(host, steps, null, false, $"{host} открывается напрямую, а десинк ему мешал.",
                            Found: RoutingMode.Direct);
                    }

                    Save(host, RoutingMode.Direct);

                    return new(host, steps, RoutingMode.Direct, false,
                        $"{host} открывается напрямую, а десинк ему мешал. Записал маршрут «напрямую» "
                        + $"для {RuleValue(host)} — действует уже сейчас, перезапуск не нужен.",
                        Found: RoutingMode.Direct);
                }

                case RouteTry.Preset or RouteTry.Recipes:
                {
                    if (await TryDesyncAsync(host, step, state, settings, steps, progress, cancellationToken) is not { } desync)
                        break;

                    var opens = $"{host} открывается {(desync.Id is null ? "с десинком пресета" : $"с рецептом «{desync.Title}»")}"
                        + $" ({desync.Detail}).";
                    var title = desync.Id is null ? null : desync.Title;

                    if (!write)
                        return new(host, steps, null, true, opens, title, Found: RoutingMode.Desync, RecipeId: desync.Id);

                    Save(host, RoutingMode.Desync, desync.Id);

                    return new(host, steps, RoutingMode.Desync, true,
                        opens + " Записал маршрут «десинк»"
                        + (desync.Id is null ? string.Empty : $" с этим рецептом")
                        + $" для {RuleValue(host)} — заработает после перезапуска движков.",
                        title, Found: RoutingMode.Desync, RecipeId: desync.Id);
                }

                case RouteTry.Vpn:
                {
                    progress?.Report($"Пробую {host} через VPN…");

                    var vpn = await ThroughTunnelAsync(host, cancellationToken);
                    steps.Add(vpn);

                    if (!vpn.Works)
                        break;

                    if (!write)
                        return new(host, steps, null, true, $"{host} открывается через VPN.", Found: RoutingMode.Proxy);

                    Save(host, RoutingMode.Proxy);

                    return new(host, steps, RoutingMode.Proxy, true, ViaVpnSummary(host, steps), Found: RoutingMode.Proxy);
                }
            }
        }

        var tried = string.Join("; ", steps.Select(s => $"{s.Path} — {s.Detail}"));
        var advice = plan.Advice
            ?? (settings.NeedsProxy ? "Возможно, лежит сам сайт." : "Включите туннель — через VPN он, возможно, откроется.");

        return new(host, steps, null, false,
            $"{host} не открылся ни одним путём ({tried}). Маршрут не менял. {advice}",
            Advice: advice);
    }

    /// <summary>Опробованные шаги — и каждый новый сразу наружу (<c>stepDone</c>).</summary>
    private sealed class StepLog(IProgress<SiteFixStep>? done) : List<SiteFixStep>
    {
        public new void Add(SiteFixStep step)
        {
            base.Add(step);
            done?.Report(step);
        }
    }

    /// <summary>Сколько рецептов пробовать за один подбор.</summary>
    /// <remarks>
    /// Неработающий рецепт стоит срока рукопожатия — четыре секунды — и подъёма
    /// winws2; восемь — около минуты. Порядок задан по симптому (<see cref="RoutePlan"/>),
    /// так что вероятные идут первыми.
    /// </remarks>
    public const int RecipeBudget = 8;

    /// <summary>Вердикт словами — с адресом, на котором он получен.</summary>
    private static string Said(TargetReport report) =>
        report.Describe() + (report.Answered is { } a ? $", {a}" : string.Empty)
        + (report.Kind != BlockKind.None && report.Why is { } why ? $": {why}" : string.Empty);

    /// <summary>
    /// Можно ли пробовать десинк пробным winws2; нельзя — почему.
    /// </summary>
    public static string? WhyNoTrial(SupervisorState state, bool elevated) =>
        !elevated
            ? "не пробовал: пробному winws2 нужны права администратора — запустите nz из терминала администратора"
            : state.Services.FirstOrDefault(s => s.Name == "winws2") is not { } winws
                ? "не пробовал: десинк не запущен"
                : !winws.KeepsProbesOut
                    ? "не пробовал: десинк запущен без портов пробы — перезапустите движки новой сборкой "
                        + "(или у пресета свой фильтр перехвата файлом)"
                    : null;

    /// <summary>
    /// Пробует десинк пробным winws2: пресет целиком либо рецепты по очереди; первый открывший — ответ.
    /// </summary>
    private static async Task<(string? Id, string Title, string Detail)?> TryDesyncAsync(
        string host,
        RoutePlanStep step,
        SupervisorState state,
        AppSettings settings,
        StepLog steps,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        string label = step.Kind == RouteTry.Preset ? "десинк пресета" : "рецепты десинка";

        if (WhyNoTrial(state, Environment.IsPrivilegedProcess) is { } why)
        {
            steps.Add(new(label, false, why, step.Kind));
            return null;
        }

        var paths = ZapretPaths.Discover();
        var presetPath = settings.PresetName is { } name ? ZapretPaths.FindPreset(name) : null;

        if (paths is null || presetPath is null)
        {
            steps.Add(new(label, false, "не пробовал: не найден Zapret или выбранный пресет", step.Kind));
            return null;
        }

        var preset = new PresetReader().Load(presetPath);
        var bare = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

        if (step.Kind == RouteTry.Preset)
        {
            progress?.Report($"Пробую {host} с десинком пресета «{preset.Name}»…");

            var outcome = await TrialAsync(paths, preset, null, host, cancellationToken);
            steps.Add(new(label, outcome.Works, outcome.Detail, RouteTry.Preset));

            return outcome.Works ? (null, "десинк пресета", outcome.Detail) : null;
        }

        IReadOnlyDictionary<string, string>? providers = null;

        try
        {
            providers = LuaModules.Providers(LuaModules.Scan(paths.LuaDirectory));
        }
        catch (Exception)
        {
            // Не прочиталось — годность рецептов не проверить; winws2 скажет сам.
        }

        var runnable = step.Recipes
            .Select(RecipeCatalog.Find)
            .OfType<CatalogRecipe>()
            .Where(r => providers is null || RecipeCatalog.Check(r, preset, providers).Runnable)
            .Take(RecipeBudget)
            .ToList();

        var ports = PresetPorts.ForDomains(preset, paths.Root, [bare]);
        int tried = 0;

        foreach (var recipe in runnable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Пробую {host} с рецептом «{recipe.Title}» ({++tried} из {runnable.Count})…");

            var profile = new OwnDesyncProfile
            {
                Name = "проба: " + recipe.Id,
                Steps = recipe.Steps,
                HostListPath = RecipeTrial.ListPath,
                Ports = ports,
            };

            var outcome = await TrialAsync(paths, preset, profile, host, cancellationToken);
            steps.Add(new($"рецепт «{recipe.Title}»", outcome.Works, outcome.Detail, RouteTry.Recipes, recipe.Title));

            if (outcome.Works)
                return (recipe.Id, recipe.Title, outcome.Detail);

            // Пробный winws2 не поднялся вовсе — дальше будет то же самое.
            if (outcome.Broken)
                break;
        }

        return null;
    }

    /// <summary>Одна проба пробным winws2; <c>Broken</c> — сам winws2 не поднялся.</summary>
    private static async Task<(bool Works, string Detail, bool Broken)> TrialAsync(
        ZapretPaths paths,
        ZapretPreset preset,
        OwnDesyncProfile? recipe,
        string host,
        CancellationToken cancellationToken)
    {
        var bare = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

        Directory.CreateDirectory(Path.GetDirectoryName(RecipeTrial.ListPath)!);
        await File.WriteAllTextAsync(RecipeTrial.ListPath, bare + Environment.NewLine, cancellationToken);

        var (trial, failure) = await RecipeTrial.StartAsync(paths, preset, recipe, cancellationToken);

        if (trial is null)
            return (false, failure ?? "пробный winws2 не поднялся", true);

        await using (trial)
        {
            var report = await BlockCheck.TrialAsync(host, cancellationToken);

            return (report.Kind == BlockKind.None, Said(report), false);
        }
    }

    /// <summary>
    /// Итог, когда открыл только VPN: что опробовано и чего не пробовали.
    /// </summary>
    /// <remarks>
    /// Стояло «открывается только через VPN» — неправда: рецепты десинка этот
    /// путь не перебирает вовсе, а у имени «напрямую» не пробует и десинк
    /// пресета. Владелец 14.10: modrinth.com стоял «напрямую», итог сказал
    /// «только через VPN», а открылся он своим рецептом medium.
    /// </remarks>
    public static string ViaVpnSummary(string host, IReadOnlyList<SiteFixStep> steps)
    {
        var failed = steps.Where(s => !s.Works).Select(s => s.Path).ToList();
        int recipes = steps.Count(s => s.Path.StartsWith("рецепт «", StringComparison.Ordinal));

        return $"{host} не открылся ({string.Join(", ", failed)}), а через VPN открывается. "
            + $"Записал маршрут «через VPN» для {RuleValue(host)} — заработает после перезапуска движков. "
            + (recipes > 0
                ? $"Рецептов десинка перепробовано {recipes} — ни один не открыл."
                : "Другие рецепты десинка я не пробовал: если хотите без VPN, поставьте сайту «десинк» "
                    + "и выберите рецепт в «Маршрутах».");
    }

    /// <summary>
    /// Меряет имя с выключенным для него десинком: на время кладёт его в щит.
    /// </summary>
    /// <remarks>
    /// Щит восстанавливается дословно, если путь не сработал. Сработал — имя
    /// в щите остаётся: правило «напрямую» будет записано, и при следующем
    /// запуске щит соберётся с ним же. Без записи (<paramref name="keep"/> —
    /// <c>false</c>) щит возвращается всегда.
    /// </remarks>
    private static async Task<(SiteFixStep Step, bool Kept)?> TryShieldedAsync(
        string host,
        bool keep,
        CancellationToken cancellationToken)
    {
        var path = NetZapret.Zapret.WinwsCommandLine.DefaultExcludeListPath;

        if (!File.Exists(path))
            return null;

        var original = await File.ReadAllTextAsync(path, cancellationToken);
        var bare = host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;

        bool works = false;
        TargetReport? report = null;

        try
        {
            var patched = original.TrimEnd('\r', '\n') + Environment.NewLine + bare + Environment.NewLine;
            await File.WriteAllTextAsync(path, patched, cancellationToken);

            await Task.Delay(ShieldReload, cancellationToken);

            report = await BlockCheck.CheckAsync(host, null, cancellationToken);
            works = report.Kind == BlockKind.None;
        }
        finally
        {
            if (!works || !keep)
                await File.WriteAllTextAsync(path, original, CancellationToken.None);
        }

        return (new SiteFixStep("напрямую", works, report is null ? "не проверилось" : Said(report), RouteTry.Direct), works && keep);
    }

    /// <summary>Запрос через вход проверки движка: он ведёт в туннель.</summary>
    /// <remarks>Ответ сайта — путь рабочий: соединение через туннель прошло.</remarks>
    private static async Task<SiteFixStep> ThroughTunnelAsync(string host, CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = EngineKeys.Loopback(SingBoxOptions.DefaultHealthPort, EngineKeys.Current()),
                UseProxy = true,
                AllowAutoRedirect = false,
            };

            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var response = await http.GetAsync($"https://{host}/", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int code = (int)response.StatusCode;
            // 403 через зарубежный выход — почти всегда проверка на робота
            // Cloudflare, её браузер проходит; 451 — отказ по закону.
            bool works = code < 500 && code != 451;

            return new("через VPN", works, $"ответ {code}", RouteTry.Vpn);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new("через VPN", false, "нет ответа за 10 с", RouteTry.Vpn);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new("через VPN", false, ex.GetBaseException().Message, RouteTry.Vpn);
        }
    }

    private static void Save(string host, RoutingMode mode, string? recipe = null)
    {
        var file = UserRulesFile.Load();
        file.Set(MatchKind.Domain, RuleValue(host), mode, recipe);
        file.Save();
    }

    private static string Word(RoutingMode mode) => mode switch
    {
        RoutingMode.Proxy => "через VPN",
        RoutingMode.Desync => "десинк",
        _ => "напрямую",
    };
}
