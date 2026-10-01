using System.IO;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui;

/// <summary>Чем закончилась сборка конфига.</summary>
internal sealed record BuildOutcome(bool Ok, string Message);

/// <summary>
/// Собирает конфиг туннеля из настроек, правил и подписки.
/// </summary>
/// <remarks>
/// <para>
/// Без этого окно было несамостоятельным не в смысле удобства, а буквально:
/// оно запускало движки с тем <c>runtime\singbox.json</c>, который лежал на
/// диске, и смена сервера, правки маршрутов и переключение режима доходили
/// до туннеля только после того, как конфиг соберёт консоль. Настройка при
/// этом показывалась новая — расходилось только поведение.
/// </para>
/// <para>
/// Прежде повторяло то, что консольное меню делало перед запуском;
/// консоль удалена 23.09, и эта сборка — единственная.
/// </para>
/// </remarks>
internal static class TunnelConfig
{
    public static async Task<BuildOutcome> BuildAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        // Выхода нет вовсе — ни подписки, ни WARP. Дальше идти незачем:
        // конфиг вышел бы с пустой группой выходов, и движок держал бы TUN,
        // не умея никуда доставить.
        //
        // Смотрим на оба источника, а не на одну ссылку: WARP заведён ровно
        // затем, чтобы работать без подписки, и прежде он в одиночку
        // не поднимался — отказ приходил раньше, чем кто-либо смотрел
        // на выключатель.
        if (!settings.HasTunnelExit)
        {
            return new BuildOutcome(false,
                "Ни подписки, ни WARP — заворачивать трафик некуда. "
                + "Добавьте ссылку в разделе «VPN» либо включите там же бесплатный WARP.");
        }

        try
        {
            var (ruleSet, zapretRoot) = LoadRules(settings);

            // WARP и подписки — одно из двух (владелец, 01.10; Warp.TunnelExits).
            // При включённом WARP подписки на паузе, и ходить за ними незачем:
            // запуск не ждёт панелей, которые всё равно не понадобятся.
            IReadOnlyList<ProxyServer> fromSubscription = [];
            int sourceCount = 0, keyCount = 0, reserved = 0;

            if (!settings.WarpEnabled)
            {
                // Пул (0.9.0): серверы всех подписок в работе, а не одной
                // действующей. Указатель из настроек — на случай, когда список
                // подписок пуст, а ссылку поставила консоль.
                var book = SubscriptionBook.Load();

                var sources = book.Pool
                    .Select(e => new PoolSource(e.Name, e.Url))
                    .ToList();

                if (sources.Count == 0 && !string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
                    sources.Add(new PoolSource("Основная", settings.SubscriptionUrl));

                // Отдельные ключи — последним источником пула (0.9.0).
                var pool = await SubscriptionPool.BuildAsync(sources, cancellationToken, keys: book.PoolKeys);
                fromSubscription = pool.Servers;
                sourceCount = sources.Count;
                keyCount = book.PoolKeys.Count;
                reserved = pool.Parts.Count(p => p.FromReserve);

                // Подписки были, а серверов нет ни от одной, и запаса тоже:
                // собирать не из чего, и сказать надо, почему. Имена подписок,
                // не ссылки: ссылка — пароль.
                if ((sources.Count > 0 || book.PoolKeys.Count > 0) && fromSubscription.Count == 0)
                {
                    return new BuildOutcome(false, "Конфиг не собрался: ни подписки, ни ключи не дали серверов: "
                        + string.Join("; ", pool.Parts.Select(p => $"«{p.Source.Name}» — {p.Error ?? "пусто"}")) + ".");
                }

                // Кто пришёл из запаса — в журнал: без этого «сервер из вчерашнего
                // списка» было бы нечем объяснить.
                foreach (var part in pool.Parts.Where(p => p.FromReserve || p.Error is not null))
                {
                    Journal.Write("подписка", part.FromReserve
                        ? $"«{part.Source.Name}» не ответила — серверы из запаса от {part.ReserveAt:dd.MM HH:mm}"
                        : $"«{part.Source.Name}» не прочиталась и запаса нет: {part.Error}");
                }
            }

            var servers = Warp.TunnelExits(settings, fromSubscription);

            var capture = AddressListReader.Expand(ruleSet.CaptureEntries, zapretRoot, out _);

            // hosts бьёт любой резолв, включая наш: прибитый там домен
            // не получит fakeip и уйдёт мимо туннеля, сколько бы правил
            // на него ни стояло.
            var pinned = HostsFile.CollectPinnedProxy(ruleSet, out _);

            var addresses = AddressOverrides.Merge(new Dictionary<string, string>(), AddressOverrides.Load());

            // Кто из серверов заведомо мёртв — по нашим же замерам. Сборка
            // в сеть не ходит и судить о живости не может, поэтому список
            // приносим ей мы.
            var options = new SingBoxOptions();
            var dead = ServerHealthCache.Load().Dead(options.DeadAfterFailures);

            // «В пуле только рабочие» (владелец, 28.09) — буквально: мёртвый
            // по замерам сервер в конфиг не идёт вовсе, а не только выводится
            // из автоподбора. С пулом из трёх подписок в конфиге стало 118
            // выходов, и sing-box поднимался 7 секунд против прежних двух;
            // каждый мёртвый выход — лишняя работа движку ни за что.
            // Закреплённый человеком остаётся — его выбрали руками. И не до
            // пустоты: мёртвыми бывают все сразу — по устаревшим замерам, —
            // и тогда пусть движок перебирает всех. В списке вкладки мёртвые
            // видны по-прежнему; удачный замер вернёт сервер со следующим
            // запуском.
            var alive = servers
                .Where(s => !dead.Contains(s.Tag) || s.Tag == Warp.PreferredExit(settings) || s.IsSelfRegistering)
                .ToList();

            int dropped = servers.Count - alive.Count;

            if (alive.Any(s => !s.IsSelfRegistering))
                servers = alive;
            else
                dropped = 0;

            // Охват TUN следует за десинком, а не за настройкой, и это
            // исправление 21.09.
            //
            // «Только прокси» заведено ради сосуществования с winws2: TUN
            // забирает один диапазон fakeip, и WinDivert видит исходные
            // потоки приложений, а не переоткрытые сокетом sing-box. Пока
            // десинк работает, это необходимо.
            //
            // Без десинка защищать нечего, а режим «всё через туннель» такая
            // настройка ломает начисто: route_address у TUN остаётся списком
            // из fakeip и десятка адресов, ютуб со своим настоящим адресом
            // Google в туннель не попадает вовсе и уходит напрямую. Владелец
            // это и увидел: «на режиме только туннель не работает ютуб» —
            // и был прав, назвав следствие: выключение winws2 оказалось
            // единственным, что режим делал.
            //
            // С 23.09 — по TunnelTakesAll: при игнорируемых исключениях
            // туннель забирает всё и при включённом выключателе десинка.
            //
            // С 29.09 — и не при программе, которую правила ведут в туннель
            // (RuleSet.RoutesProgramIntoTunnel, обсуждение №10): её трафик
            // идёт на адреса, которых в выборочном перехвате нет.
            bool narrow = !settings.Engines.TunnelTakesAll && settings.ProxyOnly && !ruleSet.RoutesProgramIntoTunnel;

            // Полный перехват только ради программы — игры с маршрутом мимо VPN
            // выводятся из туннеля и идут так же, как без этого правила
            // (ProgramCapture, 30.09). Выбранный человеком режим «всё через
            // туннель» не трогаем: там туннель забирает всё по его решению.
            bool forcedByProgram = !settings.Engines.TunnelTakesAll && settings.ProxyOnly && ruleSet.RoutesProgramIntoTunnel;

            var keptOut = forcedByProgram
                ? AddressListReader.Expand(ProgramCapture.KeepOut(ruleSet), zapretRoot, out _)
                : [];

            var result = new SingBoxConfigCompiler().Compile(ruleSet, servers, new SingBoxOptions
            {
                Scope = narrow ? TunnelScope.ProxyOnly : TunnelScope.Everything,
                DnsServerAddresses = narrow ? SystemResolvers.Discover() : Array.Empty<string>(),
                DnsServer = settings.DnsServer,

                // Имя сертификата и путь — у провайдера из списка выбора.
                // Адрес, вписанный руками, идёт как прежде: без имени.
                DnsServerName = DnsSurvey.ByAddress(settings.DnsServer)?.TlsName,
                DnsServerPath = DnsSurvey.ByAddress(settings.DnsServer)?.DohPath,
                DnsThroughTunnel = settings.DnsThroughTunnel,

                // Все подписки из книги, а не одна действующая: переключаются
                // на другую как раз тогда, когда выход текущей лёг.
                PanelHosts = SubscriptionHosts.From(
                    [settings.SubscriptionUrl, .. SubscriptionBook.Load().Entries.Select(e => e.Url)]),
                PreferredServerTag = Warp.PreferredExit(settings),

                // «Игнорировать исключения» сюда больше не передаётся:
                // с 23.09 она отменяет все прямые правила, а не одно
                // российское, и делает это режимом — ProxyStrict, где
                // правил нет вовсе. См. EngineChoice.Mode.
                ForeignExitsOnly = settings.ForeignExitsOnly,

                // Вход проверки — всегда, а не только под «Проверку прохода
                // трафика». Через него же идут замер скорости через туннель
                // и проверка блокировок через туннель, а проверка прохода
                // по умолчанию выключена. До 0.10.3 у большинства замер
                // «через туннель» падал с «конечный компьютер отверг запрос»
                // (жалоба 01.10): стучаться было не во что. Стучится ли
                // в него сам супервизор, решает по-прежнему VerifyTraffic
                // (SupervisorHost), — лишний вход ему не мешает.
                HealthInbound = true,

                // Новые пароли служебных входов на каждую сборку: окно, nz
                // и сторож читают их из этого же конфига (EngineKeys.Current).
                Keys = EngineKeys.Generate(),

                CaptureAddresses = capture,
                DesyncAddresses = keptOut,
                PinnedProxyAddresses = pinned.Addresses,
                PinnedProxyNames = pinned.Names,
                AddressOverrides = addresses,

                // Мёртвые — мимо автоподбора, но в селекторе остаются:
                // закрепить такой сервер руками законное желание, он мог
                // подняться между нашими замерами.
                // И убранные человеком, и отвечающие через раз (ServerHealth.Flaky):
                // в селекторе они остаются, выбрать руками можно, а автоподбор
                // на них не садится.
                DeadServerTags = dead
                    .Concat(settings.AutoPickExcluded)
                    .Concat(ServerHealthCache.Load().Flaky())
                    .ToHashSet(StringComparer.Ordinal),

                // Проверка серверов — как настроил человек (AppSettings).
                LatencyTestInterval = $"{Math.Max(1, settings.FullCheckMinutes)}m",
                AutoPickPerEntry = Math.Max(0, settings.AutoPickPerEntry),
                KnownLatency = ServerHealthCache.Load().Latencies(),
            });

            SingBoxConfigCompiler.WriteToFile(settings.ProxyConfigPath, result.Json);

            var note = settings.WarpEnabled
                ? "Конфиг собран: туннель через WARP, подписки на паузе"
                : $"Конфиг собран: {result.UsedServers.Count} серверов"
                    + (sourceCount > 1 ? $" из {sourceCount} подписок" : string.Empty)
                    + (keyCount > 0 ? $" и {keyCount} ключей" : string.Empty);

            if (reserved > 0)
                note += $", {reserved} из запаса — панель не ответила";

            // Про мёртвых говорим вслух. Молча выведенный из автоподбора
            // сервер — это сервер, который человек считает рабочим, а он
            // не участвует в выборе, и почему — не видно нигде.
            if (dead.Count > 0)
                note += dropped > 0
                    ? $", {dropped} не отвечали и в конфиг не взяты"
                    : $", {dead.Count} не отвечали и выведены из автоподбора";

            if (result.SkippedServers.Count > 0)
                note += $", {result.SkippedServers.Count} пропущено";

            return new BuildOutcome(true, note + ".");
        }
        catch (Exception ex)
        {
            return new BuildOutcome(false, "Конфиг не собрался: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Правила так, как их увидят движки, — общим кодом с nz.</summary>
    private static (RuleSet RuleSet, string? ZapretRoot) LoadRules(AppSettings settings)
    {
        var (engine, zapretRoot) = RuleSetExpander.LoadFor(settings);

        return (engine.RuleSet, zapretRoot);
    }

    /// <summary>
    /// Пишет winws2 его списки: исключения и свои рецепты.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Отдельно от сборки конфига туннеля, и это исправление 23.09 (issue #1).
    /// Прежде списки писались внутри <see cref="BuildAsync"/>, а её зовут
    /// только при поднимаемом туннеле. С одним десинком winws2 получал
    /// вчерашний список исключений или никакого: «напрямую» у Twitch
    /// до него не доходило, и перезапуск ничего не менял.
    /// </para>
    /// <para>
    /// Имена, которые десинку трогать нельзя: прибитые в hosts и поставленные
    /// на «напрямую», а без туннеля — ещё и «через VPN» (таблица владельца
    /// 23.09: при одном десинке VPN идёт напрямую). Пин — выбранный руками
    /// адрес, а десинк судит по имени и про подмену не знает: он применяет
    /// к постороннему узлу рецепт, выверенный на настоящей сети доставки,
    /// и рвёт рукопожатие.
    /// </para>
    /// <para>
    /// Домен регистрации WARP сюда НЕ добавляется, и это решение по замеру,
    /// а не недосмотр: без десинка его рукопожатие не проходит вовсе.
    /// См. Warp.RegistrationHost.
    /// </para>
    /// </remarks>
    public static BuildOutcome WriteDesyncLists(AppSettings settings)
    {
        try
        {
            var (ruleSet, zapretRoot) = LoadRules(settings);

            // Туннель считается поднятым, только если он вправду поднимется:
            // выключатель без выхода TUN не даёт, и «через VPN» тогда тоже
            // идёт напрямую.
            var excluded = HostsFile.CollectDesyncExclusions(ruleSet, tunnelUp: settings.NeedsProxy);

            WinwsCommandLine.WriteExcludeList(excluded);

            // Имена, которым рецепт выбран руками, уходят в свои профили
            // winws2. Без этого «десинк» в маршрутах означал только «мимо
            // туннеля»: что сделать с именем, решал пресет, а не попавшему
            // ни в один его список не делалось ничего.
            WriteOwnDesync(ruleSet, zapretRoot);

            // Игровой UDP Riot — из перехвата winws2, если его часть не на «десинке».
            UdpOffDesync.Write(UdpOffDesync.Choose(new RuleEngine(ruleSet), zapretRoot));

            return new BuildOutcome(true, $"Десинк не тронет имён: {excluded.Count}.");
        }
        catch (Exception ex)
        {
            return new BuildOutcome(false, "Списки десинка не собрались: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Имена одного правила: само имя либо всё содержимое его списка.
    /// </summary>
    /// <remarks>
    /// Списки к этому моменту уже развёрнуты <c>RuleSetExpander</c>'ом, так
    /// что читать файл заново не нужно. Звёздочка снимается: в списках Zapret
    /// записи — это зоны, и winws2 понимает их так же, без маски.
    /// </remarks>
    private static IEnumerable<string> Names(RoutingRule rule)
    {
        var values = rule.Match == MatchKind.HostList
            ? rule.HostListDomains
            : [rule.Value];

        return values.Select(v => v.StartsWith("*.", StringComparison.Ordinal) ? v[2..] : v);
    }

    /// <summary>
    /// Раскладывает имена с выбранным рецептом по профилям winws2.
    /// </summary>
    /// <remarks>
    /// Пишется всегда, в том числе пустым: снятый рецепт иначе продолжал бы
    /// применяться из вчерашнего файла, и снять его было бы нечем.
    /// </remarks>
    private static void WriteOwnDesync(RuleSet ruleSet, string? zapretRoot)
    {
        try
        {
            // И одиночные имена, и списки: у сервиса рецепт нужен чаще, чем
            // у своего домена, — там десятки имён, и когда пресет их не
            // открывает, разбираться руками не в чем.
            var chosen = ruleSet.Rules
                .Where(r => r.Mode == RoutingMode.Desync
                    && r.Match is MatchKind.Domain or MatchKind.HostList
                    && !string.IsNullOrWhiteSpace(r.Recipe))
                .ToList();

            if (chosen.Count == 0)
            {
                OwnDesyncLists.Write([]);
                return;
            }

            // Пресет может быть не выбран вовсе — тогда десинк не запускается
            // и чинить имя нечем; правило при этом остаётся, просто без рецепта.
            var presetPath = AppSettings.Load(AppSettings.DefaultPath).PresetName is { } name
                ? ZapretPaths.FindPreset(name)
                : null;

            if (presetPath is null)
            {
                OwnDesyncLists.Write([]);
                return;
            }

            var preset = new PresetReader().Load(presetPath);

            // Приёмы, которые умеет движок, — чтобы отличить рецепт,
            // требующий неподключённого модуля, от исправного.
            IReadOnlyDictionary<string, string>? providers = null;

            try
            {
                if (ZapretPaths.Discover() is { } paths)
                    providers = LuaModules.Providers(LuaModules.Scan(paths.LuaDirectory));
            }
            catch (Exception)
            {
                // Не прочиталось — проверять нечем, и объявлять нехватку,
                // которую не проверяли, нельзя. Останется прежнее поведение.
            }

            var groups = chosen
                .GroupBy(r => r.Recipe!, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    // По обоим источникам: рецепт мог быть выбран из каталога,
                    // и в пресете его нет по построению. Пока искали только
                    // там, выбранный из каталога молча не применялся вовсе —
                    // профиль не создавался, а в маршрутах стояло
                    // «десинк: tls-multisplit-sni — нет в пресете».
                    var recipe = RecipeResolver.Find(preset, group.Key, providers);

                    var domains = (IReadOnlyList<string>)group
                        .SelectMany(Names)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Ненайденный рецепт — это молча неприменённая настройка:
                    // пустой набор шагов, профиль без шагов не выпускается,
                    // а в меню по-прежнему написано «десинк: hostfakesplit_multi».
                    // Так пропал голос Discord, и заметить это было нечем.
                    if (!recipe.Found)
                    {
                        Note($"рецепт «{group.Key}» не найден ни в пресете «{preset.Name}», "
                            + $"ни в каталоге — профиль не создан, имён затронуто {domains.Count}");
                    }

                    // Беда особого рода, и дороже предыдущей. Шаги известны,
                    // но приём объявлен в модуле, которого пресет не подключает:
                    // winws2 на такое отвечает «desync function does not exist»
                    // и не поднимается вовсе — то есть один рецепт оставляет
                    // без десинка все имена разом.
                    //
                    // Поэтому профиль не выпускается, а не выпускается сломанным.
                    else if (recipe.MissingModules.Count > 0)
                    {
                        Note($"рецепт «{group.Key}» требует модулей, которых пресет "
                            + $"«{preset.Name}» не подключает: {string.Join(", ", recipe.MissingModules)}. "
                            + "Профиль не создан — иначе winws2 не запустился бы вовсе. "
                            + "Подключить их можно на вкладке «Десинк».");
                    }

                    return (
                        Name: group.Key,
                        Steps: recipe.MissingModules.Count > 0 ? [] : recipe.Steps,
                        Domains: domains,

                        // Порты берутся у секции, которую профиль подменяет.
                        // Свой профиль стоит первым и забирает имя себе,
                        // а значит обязан покрывать то же, что покрыла бы она.
                        Ports: (string?)PresetPorts.ForDomains(preset, zapretRoot, domains));
                })
                .ToList();

            OwnDesyncLists.Write(WinwsCommandLine.WriteOwnLists(groups));
        }
        catch (Exception)
        {
            // Своя настройка не должна мешать сборке конфига: без неё
            // всё работает ровно так, как работало до неё.
            OwnDesyncLists.Write([]);
        }
    }

    private static void Note(string message) => Journal.Write("конфиг", message);
}
