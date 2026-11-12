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
/// <param name="Pinned">Имя прибито в hosts к этому запасному адресу Cloudflare (<see cref="RouteTry.CloudflareSpare"/>).</param>
/// <param name="Ours">Закрывает не провайдер, а секция нашего пресета — её имя (или «секция по адресам», если не найдена).</param>
public sealed record SiteFixResult(
    string? Host,
    IReadOnlyList<SiteFixStep> Steps,
    RoutingMode? Applied,
    bool NeedsRestart,
    string Summary,
    string? Recipe = null,
    string? Advice = null,
    RoutingMode? Found = null,
    string? RecipeId = null,
    string? Pinned = null,
    string? Ours = null)
{
    /// <summary>Чем кончилось — хвост строки журнала.</summary>
    public string Outcome() =>
        Applied is { } mode ? $" → записан маршрут {mode}"
        : Pinned is { } spare ? $" → прибит к запасному адресу Cloudflare {spare}"
        : Ours is { } section ? $" → закрывает наш десинк: {section}, маршрут не менялся"
        : " → маршрут не менялся";
}

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

        // «Десинк» с рецептом pass — это «напрямую»: профиль pass пропускает имя
        // мимо всего десинка. Иначе подбор пробует «напрямую» как другой путь —
        // и сравнивает путь сам с собой. 27.10 seriesgraph.com (часть «десинк, pass»):
        // «как есть» замерло на 27 380 Б, «напрямую» один раз прошло — записалось
        // «напрямую», а через минуту то же «напрямую» замерло на тех же 27 380 Б.
        if (route == RoutingMode.Desync && decision.Rule?.Recipe is { } own
            && own.Trim().Equals(RecipeCatalog.Pass, StringComparison.OrdinalIgnoreCase))
        {
            route = RoutingMode.Direct;
        }

        // Как есть — она же и называет симптом.
        progress?.Report($"Проверяю {host} как есть…");
        var now = await BlockCheck.CheckAsync(host, null, cancellationToken, throughTunnel: route == RoutingMode.Proxy);
        steps.Add(new($"как есть ({Word(route)})", now.Opens, Said(now)));

        if (await OursAsync(host, now, route, state, settings, steps, progress, cancellationToken) is { } ours)
            return ours;

        bool cloudflare = CloudflareNet.Contains(now.Answered ?? now.Addresses.FirstOrDefault());
        var plan = RoutePlan.For(now, route, settings.NeedsDesync, settings.NeedsProxy, cloudflare);

        // Сайт отвечает проверкой на робота, а скриптов своей зоны у неё нет
        // или они малы для заморозки: судить не по чему. Другие пути увидели бы ту же
        // проверку, и подбор записал бы первый попавшийся — маршрут не трогаем.
        // Замёрзший скрипт проверки сюда не доходит: это уже обрыв (Stall).
        if (now.BehindBotWall)
        {
            var wall = $"{host} отвечает проверкой на робота ({Word(route)}) — саму страницу проба не видит "
                + "и судить, открывается ли сайт, ей не по чему. Маршрут не менял. Если в браузере "
                + "страница встаёт на полпути (без стилей и картинок) — поставьте маршрут руками: "
                + "«десинк» или «через VPN».";

            return new(host, steps, null, false, wall, Advice: wall);
        }

        if (now.Kind == BlockKind.None)
        {
            var open = plan.Advice
                ?? "Если в браузере не открывается, дело не в маршруте: попробуйте окно инкогнито "
                    + "(браузер держит старые соединения) или другой браузер.";

            return new(host, steps, null, false,
                $"{host} открывается как есть ({Word(route)}) — маршрут менять незачем. {open}",
                Advice: open);
        }

        if (now.Kind == BlockKind.Dns && await WrongAddressAsync(host, now, settings, steps, progress, cancellationToken) is { } dns)
            return dns;

        // Пин — правка hosts для имени, а подбор без записи (окно пути маршрута)
        // выбирает путь всему маршруту: пину там не место.
        foreach (var step in plan.Steps.Where(s => (recipes || s.Kind != RouteTry.Recipes)
            && (write || s.Kind != RouteTry.CloudflareSpare)))
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

                case RouteTry.CloudflareSpare:
                {
                    if (await TrySpareAsync(host, now.Answered, steps, progress, cancellationToken) is not { } spare)
                        break;

                    if (Pin(host, spare) is { } failed)
                    {
                        steps.Add(new("пин в hosts", false, failed, RouteTry.CloudflareSpare));
                        break;
                    }

                    return new(host, steps, null, false,
                        $"{host} на Cloudflare режут по адресу {now.Answered ?? "сайта"}, а на запасном адресе "
                        + $"Cloudflare {spare} он открывается. Прибил {host} к {spare} в hosts — с десинком, "
                        + "действует уже сейчас. Снять — на вкладке «Файл hosts».",
                        Pinned: spare);
                }

                case RouteTry.Vpn:
                {
                    progress?.Report($"Пробую {host} через VPN…");

                    var vpn = await ThroughTunnelAsync(host, now.Kind, cancellationToken);
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
        + (!report.Opens && report.Why is { } why ? $": {why}" : string.Empty);

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
        var companions = PresetCompanions.ForDomains(preset, paths.Root, [bare]);
        int tried = 0;

        foreach (var recipe in runnable)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Пробую {host} с рецептом «{recipe.Title}» ({++tried} из {runnable.Count})…");

            var profile = new OwnDesyncProfile
            {
                Name = "проба: " + recipe.Id,
                Steps = PresetCompanions.With(companions, recipe.Steps),
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

            return (report.Opens, Said(report), false);
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
            works = report.Opens;
        }
        finally
        {
            if (!works || !keep)
                await File.WriteAllTextAsync(path, original, CancellationToken.None);
        }

        return (new SiteFixStep("напрямую", works, report is null ? "не проверилось" : Said(report), RouteTry.Direct), works && keep);
    }

    /// <summary>Открылось ли через VPN: код ответа через туннель и вердикт мимо него.</summary>
    /// <remarks>
    /// 403 через зарубежный выход — почти всегда проверка на робота Cloudflare,
    /// её браузер проходит; 451 — отказ по закону. Но если мимо туннеля сайт уже
    /// ответил 403 без объяснений (<see cref="BlockKind.Forbidden"/>), тот же 403
    /// через VPN — тот же отказ, а не открытие: ns.photonengine.io отвечает
    /// «403 HTTP not supported» откуда угодно, и подбор записал бы ему «через VPN»
    /// на пустом месте (27.10, после того как такой 403 стал своим вердиктом).
    /// </remarks>
    public static bool TunnelOpens(int code, BlockKind here) =>
        code < 500 && code != 451 && !(code == 403 && here == BlockKind.Forbidden);

    /// <summary>Запрос через вход проверки движка: он ведёт в туннель.</summary>
    /// <remarks>Ответ сайта — путь рабочий: соединение через туннель прошло (<see cref="TunnelOpens"/>).</remarks>
    private static async Task<SiteFixStep> ThroughTunnelAsync(string host, BlockKind here, CancellationToken cancellationToken)
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

            // Без User-Agent защита сайта отказывает роботу откуда угодно, и 403
            // через VPN читался как «тот же отказ, что и мимо туннеля». №59, замер
            // 12.11 через туннель: game8.co (CloudFront) без заголовка — 403 2 из 2,
            // с тем же, что у пробы проверки блокировок, — 200 2 из 2; мимо
            // туннеля — 403 с любым (отказ России). Подбор не предлагал VPN.
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            request.Headers.TryAddWithoutValidation("Accept", "text/html");

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            int code = (int)response.StatusCode;
            bool works = TunnelOpens(code, here);

            return new("через VPN", works,
                !works && code == 403 ? "ответ 403 — тот же отказ, что и мимо туннеля" : $"ответ {code}", RouteTry.Vpn);
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

    /// <summary>
    /// «Резолвер даёт нерабочий адрес»: прибито в hosts — снять пин; иначе — какой резолвер даёт рабочий.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Нашёлся резолвер с рабочим адресом — маршрут не пишется: смена резолвера
    /// движка чинит и остальные имена той же сети доставки, а VPN — одно имя
    /// и за трафик подписки. 08.11 так у человека из чата: Google отдавал картинкам
    /// Steam мёртвый узел Akamai, смена резолвера на Zapret DNS починила всё разом.
    /// </para>
    /// <para>
    /// Пин в hosts сильнее и резолвера, и маршрута: имя «через VPN» с пином
    /// подставного адреса не получит, а проба через вход проверки движка
    /// разрешила бы имя сама и «открыла» бы его — подбор записал бы VPN впустую.
    /// </para>
    /// </remarks>
    /// <returns>Итог; <c>null</c> — резолвера не нашлось, дальше шаги плана (VPN).</returns>
    private static async Task<SiteFixResult?> WrongAddressAsync(
        string host,
        TargetReport now,
        AppSettings settings,
        StepLog steps,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (HostsEditor.Parse().FirstOrDefault(e => e.Enabled
                && e.Names.Any(n => string.Equals(n, host, StringComparison.OrdinalIgnoreCase))) is { } pin)
        {
            var unpin = $"{host} прибит в hosts к {pin.Address}, а этот адрес не работает. Маршрут тут не поможет — "
                + "снимите пин на вкладке «Файл hosts» (или подберите новый). Маршрут не менял.";

            return new(host, steps, null, false, unpin, Advice: unpin);
        }

        progress?.Report($"Спрашиваю {host} у других резолверов…");

        var dead = new List<string>();

        try
        {
            dead.AddRange((await System.Net.Dns.GetHostAddressesAsync(host, cancellationToken)).Select(a => a.ToString()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Имя не разрешилось вовсе — мёртвых адресов нет, ищем любой рабочий.
        }

        var current = settings.DnsServer;
        var mine = DnsSurvey.ByAddress(current)?.Name ?? current;
        var found = await BlockCheck.WorkingResolverAsync(host, dead, current, cancellationToken);

        if (found is not { } working)
        {
            steps.Add(new("другие резолверы", false, "рабочего адреса не дал ни один из списка вкладки DNS"));
            return null;
        }

        steps.Add(new($"резолвер {working.Provider.Name}", true, $"даёт {working.Address} — отвечает"));

        var said = dead.Count > 0
            ? $"Резолвер движка ({mine}) даёт {host} адрес {dead[0]}, а он не отвечает"
            : $"Резолвер движка ({mine}) не разрешает {host}";
        var fix = $"{said}; {working.Provider.Name} даёт {working.Address}, и он отвечает. Выберите {working.Provider.Name} "
            + "резолвером движка на вкладке DNS и перезапустите движки — это поправит и другие сайты той же сети. "
            + "Маршрут не менял.";

        return new(host, steps, null, false, fix, Advice: fix);
    }

    /// <summary>
    /// Закрыто — кем: провайдером или нашим же десинком? Закрыл десинк — итог с секцией.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Обсуждение №52: на все имена Discord «порт 443 закрыт» и совет VPN, а порт
    /// закрывала секция пресета «discord.com (IP fallback)» подделкой SYN. Проба
    /// «как есть» идёт через весь пресет и не знает, кто закрыл. Здесь тот же
    /// вопрос задаётся с портов пробы, которые основной winws2 не перехватывает
    /// (<see cref="ProbePorts"/>), через Wi-Fi: это путь мимо десинка, и прав
    /// администратора он не требует. Мимо десинка встало то, что через него
    /// не встало (TCP при закрытом порте, рукопожатие при «режут адрес»), — закрыли мы.
    /// </para>
    /// <para>
    /// Тогда ничего не пишется: «напрямую» не поможет (SYN имени не несёт,
    /// щит его не узнаёт), а VPN «открыл» бы и записал бы лишнее. Говорится,
    /// какая секция берёт адрес (<see cref="SynSections"/>) и какой частью
    /// «Маршрутов» её выключить; выключать ли — решать человеку: на той же
    /// секции держится голос Discord. Вживую не проверено — у владельца
    /// с IP fallback всё открывается.
    /// </para>
    /// </remarks>
    private static async Task<SiteFixResult?> OursAsync(
        string host,
        TargetReport now,
        RoutingMode route,
        SupervisorState state,
        AppSettings settings,
        StepLog steps,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (route == RoutingMode.Proxy || !settings.NeedsDesync
            || now.Kind is not (BlockKind.Full or BlockKind.HttpsPort or BlockKind.TlsByAddress)
            || state.Services.FirstOrDefault(s => s.Name == "winws2") is not { KeepsProbesOut: true })
        {
            return null;
        }

        progress?.Report($"Пробую {host} мимо десинка…");

        var around = await BlockCheck.ProbeAroundTunnelAsync(host, null, desync: false, cancellationToken);

        // Сравнивается та стадия, на которой закрыто «как есть». Не встал TCP —
        // смотрим TCP: имя, которое провайдер режет по имени, мимо десинка рукопожатие
        // не пройдёт и так (замер 05.11: discord.com мимо десинка — TCP есть, TLS
        // молчит). Так и было в №52: TCP глушила наша секция, имя резал провайдер,
        // а лечила его секция по имени — когда до неё доходило.
        bool tcpStage = now.Kind is BlockKind.Full or BlockKind.HttpsPort;
        bool opens = tcpStage ? around.Tcp.Ok : around.Tls.Ok;

        steps.Add(new("мимо десинка", opens,
            (opens ? (tcpStage ? "соединение встаёт" : "рукопожатие проходит") : "так же закрыто")
                + $" — {Said(around)}",
            RouteTry.Around));

        if (!opens)
            return null;

        var address = now.Answered ?? now.Addresses.FirstOrDefault();
        var section = address is null ? null : SectionAt(address, settings);

        string who = section is null
            ? "секция пресета по адресам (какая — не нашёл)"
            : $"секция пресета «{section.Section}» по списку {section.List}";

        string fix = section?.Part is { } part
            ? $"Выключите в «Маршрутах» часть «{section.Service} — {part.Name}»: с ней гаснет и эта секция "
                + "(после перезапуска движков)." + (part.Note is { } note ? $" Учтите: {note}." : string.Empty)
            : "Части в «Маршрутах» с этим списком нет — секцию убирает только другой пресет.";

        return new(host, steps, null, false,
            $"{host} закрывает наш десинк, а не провайдер: мимо десинка "
            + (tcpStage ? "соединение встаёт" : "рукопожатие проходит") + $", через него — {now.Describe()}. "
            + $"Адрес {address ?? "имени"} забирает {who} ещё на SYN, до имени, — поэтому «напрямую» тут "
            + "не поможет. Без неё имя поведёт секция пресета по имени, если она есть. "
            + fix,
            Advice: fix,
            Ours: section?.Section ?? "секция по адресам");
    }

    /// <summary>Секция выбранного пресета, которая берёт SYN к адресу на 443; уже выключенные «Маршрутами» — не в счёт.</summary>
    private static SynSection? SectionAt(string address, AppSettings settings)
    {
        try
        {
            if (!System.Net.IPAddress.TryParse(address, out var ip)
                || ZapretPaths.Discover() is not { } paths
                || settings.PresetName is not { } name
                || ZapretPaths.FindPreset(name) is not { } presetPath)
            {
                return null;
            }

            var preset = AddressSectionsOff.Strip(new PresetReader().Load(presetPath), AddressSectionsOff.Read(), out _);

            return SynSections.Find(preset, paths.Root, ip, 443);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Пробует имя на запасных адресах Cloudflare; первый открывший — ответ.</summary>
    /// <remarks>
    /// Открывший — рукопожатие и ответ сайта, и браузерное приветствие тоже
    /// (<see cref="BlockCheck.CheckAtAsync"/>). 403 без страны засчитан: на своём
    /// адресе связи не было вовсе, а отказ словами — уже ответ сайта
    /// (www.patreon.com 05.11: свой адрес молчит 8 с, запасной — 403 самого сайта).
    /// </remarks>
    private static async Task<string?> TrySpareAsync(
        string host,
        string? own,
        StepLog steps,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        foreach (var spare in CloudflareNet.Spares.Where(a => a != own))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Пробую {host} на запасном адресе Cloudflare {spare}…");

            var report = await BlockCheck.CheckAtAsync(host, System.Net.IPAddress.Parse(spare), cancellationToken);
            bool works = report.Kind is BlockKind.None or BlockKind.Forbidden;

            steps.Add(new($"запасной адрес {spare}", works, Said(report), RouteTry.CloudflareSpare));

            if (works)
                return spare;
        }

        return null;
    }

    /// <summary>Прибивает имя к адресу с десинком; не вышло — почему.</summary>
    /// <remarks>
    /// С пометкой десинка (<see cref="HostsEditor.PinWithDesync"/>): адрес — самого
    /// Cloudflare, а не посредника, и щит такой пин брать не должен — рецепт
    /// секции по имени имени по-прежнему нужен.
    /// </remarks>
    private static string? Pin(string host, string address)
    {
        try
        {
            var result = HostsEditor.PinWithDesync(
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase) { [host] = [address] });

            if (result.Reverted is { } reverted)
                return reverted;

            HostsEditor.FlushDns();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "hosts не записался: нужны права администратора";
        }
        catch (IOException ex)
        {
            return "hosts не записался: " + ex.Message;
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
