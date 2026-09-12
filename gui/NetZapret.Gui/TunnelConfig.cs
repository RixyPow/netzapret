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
/// Повторяет то, что консольное меню делает перед каждым запуском. Взять
/// оттуда нельзя: и меню, и сборка конфига — <c>internal</c> в
/// <c>NetZapret.Cli</c>, а библиотеки, из которых всё это складывается,
/// у окна те же самые.
/// </para>
/// </remarks>
internal static class TunnelConfig
{
    public static async Task<BuildOutcome> BuildAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            return new BuildOutcome(false, "Подписка не задана — добавьте ссылку в разделе «VPN».");

        try
        {
            var engine = RuleSetLoader.LoadLayered(
                settings.RulesPath, UserRulesFile.DefaultPath, settings.Mode);

            // Режим из настроек перекрывает файл правил: окно не должно
            // переписывать базовый YAML, который ведётся руками.
            var ruleSet = engine.RuleSet with { Operating = settings.Mode };

            using var client = new SubscriptionClient();
            var info = await client.FetchAsync(new Uri(settings.SubscriptionUrl), cancellationToken);

            // WARP добавляется к серверам подписки, а не вместо них: он запасной
            // выход, и подменять им основной — ровно обратное тому, зачем он
            // заведён. Автоподбор опрашивает всех вместе и, пока живы серверы
            // подписки, оседает на них: они быстрее.
            var servers = settings.WarpEnabled
                ? [.. info.Servers, .. Warp.Exits()]
                : info.Servers;

            var zapretRoot = ZapretPaths.Discover()?.Root;
            var capture = AddressListReader.Expand(ruleSet.CaptureEntries, zapretRoot, out _);

            RuleSetExpander.Expand(ruleSet, zapretRoot);

            // hosts бьёт любой резолв, включая наш: прибитый там домен
            // не получит fakeip и уйдёт мимо туннеля, сколько бы правил
            // на него ни стояло.
            var pinned = HostsFile.CollectPinnedProxyAddresses(ruleSet, out _);

            // Имена, которые десинку трогать нельзя: прибитые в hosts
            // и поставленные на «напрямую». Пин — выбранный руками адрес,
            // а десинк судит по имени и про подмену не знает: он применяет
            // к постороннему узлу рецепт, выверенный на настоящей сети
            // доставки, и рвёт рукопожатие. «Напрямую» же до этого означало
            // для winws2 ровно то же, что «десинк», — то есть ничего.
            WinwsCommandLine.WriteExcludeList(HostsFile.CollectDesyncExclusions(ruleSet));

            // Имена, которым рецепт выбран руками, уходят в свои профили
            // winws2. Без этого «десинк» в маршрутах означал только «мимо
            // туннеля»: что сделать с именем, решал пресет, а не попавшему
            // ни в один его список не делалось ничего.
            WriteOwnDesync(ruleSet, zapretRoot);

            var addresses = AddressOverrides.Merge(new Dictionary<string, string>(), AddressOverrides.Load());

            var result = new SingBoxConfigCompiler().Compile(ruleSet, servers, new SingBoxOptions
            {
                Scope = settings.ProxyOnly ? TunnelScope.ProxyOnly : TunnelScope.Everything,
                DnsServerAddresses = settings.ProxyOnly ? SystemResolvers.Discover() : Array.Empty<string>(),
                DnsServer = settings.DnsServer,
                DnsThroughTunnel = settings.DnsThroughTunnel,
                PreferredServerTag = settings.PreferredServer,
                ForeignExitsOnly = settings.ForeignExitsOnly,

                // Вход проверки поднимается ровно тогда, когда супервизор
                // будет через него стучаться. Разойдясь, эти два решения дают
                // вечно проваливающуюся проверку у исправного движка.
                HealthInbound = settings.VerifyTraffic,

                CaptureAddresses = capture,
                PinnedProxyAddresses = pinned,
                AddressOverrides = addresses,
            });

            SingBoxConfigCompiler.WriteToFile(settings.ProxyConfigPath, result.Json);

            var note = $"Конфиг собран: {result.UsedServers.Count} серверов";

            if (result.SkippedServers.Count > 0)
                note += $", {result.SkippedServers.Count} пропущено";

            return new BuildOutcome(true, note + ".");
        }
        catch (Exception ex)
        {
            return new BuildOutcome(false, "Конфиг не собрался: " + ex.GetBaseException().Message);
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

            var groups = chosen
                .GroupBy(r => r.Recipe!, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var recipe = DesyncRecipes.Find(preset, group.Key);

                    var domains = (IReadOnlyList<string>)group
                        .SelectMany(Names)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Ненайденный рецепт — это молча неприменённая настройка:
                    // пустой набор шагов, профиль без шагов не выпускается,
                    // а в меню по-прежнему написано «десинк: hostfakesplit_multi».
                    // Так пропал голос Discord, и заметить это было нечем.
                    if (recipe is null)
                    {
                        Note($"рецепт «{group.Key}» не найден в пресете «{preset.Name}» — "
                            + $"профиль не создан, имён затронуто {domains.Count}");
                    }

                    return (
                        Name: group.Key,
                        Steps: recipe?.Steps ?? [],
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

    /// <summary>
    /// Строка в общий журнал.
    /// </summary>
    /// <remarks>
    /// Тот же файл, что у супервизора, и тот же, что показывает раздел
    /// «Журнал»: у окна консоли нет, а заводить второй журнал ради одной
    /// строки значило бы разложить историю одного запуска по двум файлам.
    /// Писать в него из двух процессов разом безопасно —
    /// <see cref="SharedLogWriter"/> для того и заведён.
    /// </remarks>
    private static void Note(string message)
    {
        try
        {
            using var log = SharedLogWriter.TryOpen(
                Path.Combine("runtime", "supervisor.log"));

            log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] конфиг: {message}");
        }
        catch (Exception)
        {
            // Потеря строки журнала не должна ронять то, о чём она.
        }
    }
}
