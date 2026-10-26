using System.IO;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;
using NetZapret.Zapret;

namespace NetZapret.Gui;

/// <summary>Чем закончилась сборка конфига.</summary>
internal sealed record BuildOutcome(bool Ok, string Message, string? Warning = null);

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
    /// <summary>Собирает конфиг туннеля и пишет его рядом с прочим рантаймом.</summary>
    /// <remarks>
    /// С 27.10 — обёртка над общей сборкой (<see cref="EngineConfigBuilder.BuildTunnelAsync"/>):
    /// ту же зовёт телефон. Здесь остались только побочные действия окна —
    /// перенос ссылки подписки, напоминание о сроке и перепроверка панелей
    /// после запуска.
    /// </remarks>
    public static async Task<BuildOutcome> BuildAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        try
        {
            // Книга одна на всю сборку: перенос ссылки (new-url) правит ту же,
            // из которой собран пул, — как было, пока сборка жила здесь.
            var book = SubscriptionBook.Load();
            var built = await EngineConfigBuilder.BuildTunnelAsync(settings, WindowsEngineHost.Create(book), cancellationToken);

            // Что поставщик сказал заголовками (15.10): перенёс подписку —
            // ссылка в книге новая; срок кончается — напомнить, раз в сутки.
            if (built.Pool is { } pool)
            {
                foreach (var part in pool.Parts.Where(p => p.Info is not null))
                {
                    SubscriptionBook.Follow(book, part.Source.Url, part.Info!.NewUrl,
                        message => Journal.Write("подписка", message));
                    SubscriptionNotices.RemindExpiry(part.Source.Name, part.Source.Url, part.Info);
                }
            }

            // Не ждём: запуск идёт следом и не должен стоять за панелью.
            if (built.Ok && built.Deferred.Count > 0)
                _ = SubscriptionRecheck.RunAsync(built.Deferred);

            return new BuildOutcome(built.Ok, built.Message, built.Warning);
        }
        catch (Exception ex)
        {
            return new BuildOutcome(false, "Конфиг не собрался: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Собирает конфиг движка, который при одном десинке только отвечает на DNS.
    /// </summary>
    /// <remarks>
    /// Когда — <see cref="AppSettings.NeedsDnsEngine"/>, как — <see cref="DnsEngine"/>;
    /// сама сборка — <see cref="EngineConfigBuilder.BuildDnsOnly"/>.
    /// </remarks>
    public static BuildOutcome BuildDnsOnly(AppSettings settings)
    {
        var built = EngineConfigBuilder.BuildDnsOnly(settings, WindowsEngineHost.Create());

        return new BuildOutcome(built.Ok, built.Message, built.Warning);
    }

    /// <summary>Правила так, как их увидят движки, — общим кодом с nz.</summary>
    private static (RuleSet RuleSet, string? ZapretRoot) LoadRules(AppSettings settings) =>
        WindowsEngineHost.LoadRules(settings);

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
    /// и рвёт рукопожатие. Кроме пинов на адрес самого сервиса — у них
    /// пометка HostsEditor.DesyncMark, и десинк им нужен (замер 06.10).
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
            var described = HostsFile.DescribeDesyncExclusions(ruleSet, tunnelUp: settings.NeedsProxy);
            var excluded = described.Select(each => each.Name).ToList();

            WinwsCommandLine.WriteExcludeList(excluded);

            // Дыры в щите: имена внутри его зон, которым человек выбрал десинк
            // (HostsFile.CollectShieldHoles). Пишется всегда — снятый выбор
            // иначе остался бы дырой из вчерашнего файла.
            WinwsCommandLine.WriteKeepList(HostsFile.CollectShieldHoles(ruleSet, described));

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
