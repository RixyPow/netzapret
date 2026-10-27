#if NZ_INTEGRATED
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Zapret;

namespace NetZapret.Android.Host;

/// <summary>Что nfqws2 берёт из списков десинка: щит, дыры в нём, свои рецепты, UDP Riot.</summary>
/// <param name="Exclude">Щит (<c>runtime/desync-exclude.txt</c>) полным путём; <c>null</c> — исключать нечего.</param>
/// <param name="Keep">Дыры в щите (<c>runtime/desync-keep.txt</c>); <c>null</c> — дыр нет.</param>
/// <param name="Own">Свои профили — имена с рецептом, выбранным руками в «Маршрутах».</param>
/// <param name="UdpOff">Сети, UDP к которым nfqws2 не перехватывает (игровой UDP Riot).</param>
internal sealed record DesyncListFiles(
    string? Exclude,
    string? Keep,
    IReadOnlyList<OwnDesyncProfile> Own,
    IReadOnlyList<string> UdpOff);

/// <summary>
/// Пишет nfqws2 его списки перед каждым запуском с root — то же, что на
/// Windows окно пишет winws2 (<c>TunnelConfig.WriteDesyncLists</c>).
/// </summary>
/// <remarks>
/// <para>
/// До 27.10 на телефоне их не писал никто: подготовка запуска только
/// подбирала файлы, если они есть, и в комментарии обещала, что их пишет
/// сборка конфига. Сборка (<c>EngineConfigBuilder</c>) пишет один конфиг
/// sing-box, а единственный писатель списков — код окна Windows, в APK его
/// нет. На телефоне файлов не бывало вовсе, и nfqws2 поднимался без щита:
/// пины на посредника (ChatGPT, Spotify, Supercell) получали рецепт пресета —
/// на Windows так их рвало (замер 06.10: со щитом 20 из 20, под рецептом
/// 0 из 10), — «напрямую» и зоны .ru тоже, выбранный руками рецепт
/// пропадал, а игровой UDP Riot оставался в перехвате (найдено чтением кода,
/// на телефоне ничего ещё не запускалось).
/// </para>
/// <para>
/// Место этому — библиотека (CLAUDE.md, «Код»): тело повторяет окно шаг
/// в шаг и теми же вызовами библиотек, чтобы при переносе в общий тип его
/// можно было снять, ничего не меняя в поведении. Отличия только телефонные:
/// пины — свой файл приложения (<c>files/config/hosts</c>), а не системный
/// hosts Windows; пути — полные, от папки приложения; пресет — уже
/// прочитанный запуском, а не заново из настроек на диске.
/// </para>
/// <para>
/// Пишется всегда, в том числе пустым: снятый пин, снятый рецепт или Riot,
/// поставленная на «десинк», иначе продолжали бы действовать из файла
/// прошлого запуска (библиотечные писатели пустое так и пишут).
/// </para>
/// </remarks>
internal static class DesyncLists
{
    public static DesyncListFiles Write(
        AppSettings settings,
        ZapretPreset preset,
        string files,
        string zapretRoot,
        string hostsPath,
        bool tunnelUp,
        List<string> warnings)
    {
        var excludePath = Path.Combine(files, WinwsCommandLine.DefaultExcludeListPath);
        var keepPath = Path.Combine(files, WinwsCommandLine.DefaultKeepListPath);
        var ownPath = Path.Combine(files, OwnDesyncLists.DefaultPath);
        var ownDirectory = Path.Combine(files, WinwsCommandLine.OwnListsDirectory);
        var udpOffPath = Path.Combine(files, UdpOffDesync.DefaultPath);

        try
        {
            // Те же правила, что видит сборка конфига (EngineHost.LoadRules).
            var (engine, _) = RuleSetExpander.LoadFor(settings);
            var ruleSet = engine.RuleSet;

            // Туннель считается поднятым, только если он вправду поднимается
            // (план запуска): без выхода «через VPN» идёт напрямую, и десинку
            // его тоже трогать нельзя — меняется лишь причина.
            var described = HostsFile.DescribeDesyncExclusions(ruleSet, hostsPath, tunnelUp);

            WinwsCommandLine.WriteExcludeList(described.Select(each => each.Name).ToList(), excludePath);

            // Дыры в щите: имена внутри его зон, которым человек выбрал десинк.
            WinwsCommandLine.WriteKeepList(HostsFile.CollectShieldHoles(ruleSet, described), keepPath);

            WriteOwn(ruleSet, preset, zapretRoot, ownDirectory, ownPath, warnings);

            // Игровой UDP Riot — из перехвата nfqws2, если его часть не на «десинке».
            // Движок правил — тот же, что дала загрузка (окно строит второй по тем же правилам).
            UdpOffDesync.Write(UdpOffDesync.Choose(engine, zapretRoot), udpOffPath);
        }
        catch (Exception error)
        {
            // Как на Windows: запуск не срывается, работает то, что лежит
            // с прошлого раза (или ничего). Тип и текст — секретов в правилах нет.
            warnings.Add($"Списки десинка не собрались ({error.GetType().Name}: {error.GetBaseException().Message}) — nfqws2 с прежними.");
        }

        // Подбираются с диска, а не из памяти — как winws2 на Windows
        // (SupervisorHost.AddWinws): пустой список библиотека не оставляет
        // файлом, а свой профиль без файла имён OwnDesyncLists.Read отбросит.
        return new DesyncListFiles(
            File.Exists(excludePath) ? excludePath : null,
            File.Exists(keepPath) ? keepPath : null,
            OwnDesyncLists.Read(ownPath),
            UdpOffDesync.Read(udpOffPath));
    }

    /// <summary>Имена правила: само имя либо всё содержимое его списка, без «*.».</summary>
    /// <remarks>Списки уже развёрнуты <c>RuleSetExpander</c>; nfqws2, как winws2, раскрывает зону сам.</remarks>
    private static IEnumerable<string> Names(RoutingRule rule)
    {
        var values = rule.Match == MatchKind.HostList ? rule.HostListDomains : [rule.Value];
        return values.Select(v => v.StartsWith("*.", StringComparison.Ordinal) ? v[2..] : v);
    }

    /// <summary>
    /// Имена с рецептом, выбранным руками, — по своим профилям nfqws2
    /// (<c>TunnelConfig.WriteOwnDesync</c> окна).
    /// </summary>
    /// <remarks>
    /// Без этого «десинк: рецепт» в «Маршрутах» на телефоне с root означал бы
    /// только «мимо туннеля»: что делать с именем, решал бы пресет.
    /// </remarks>
    private static void WriteOwn(
        RuleSet ruleSet, ZapretPreset preset, string zapretRoot, string directory, string path, List<string> warnings)
    {
        try
        {
            var chosen = ruleSet.Rules
                .Where(r => r.Mode == RoutingMode.Desync
                    && r.Match is MatchKind.Domain or MatchKind.HostList
                    && !string.IsNullOrWhiteSpace(r.Recipe))
                .ToList();

            if (chosen.Count == 0)
            {
                // И старые файлы имён тоже: снятый рецепт не должен лежать списком.
                OwnDesyncLists.Write(WinwsCommandLine.WriteOwnLists([], directory), path);
                return;
            }

            // Приёмы, которые умеют модули lua поставки: рецепт с неподключённым
            // модулем nfqws2 не поднял бы вовсе («desync function does not exist»),
            // и без десинка остались бы все имена разом.
            IReadOnlyDictionary<string, string>? providers = null;

            try
            {
                providers = LuaModules.Providers(LuaModules.Scan(Path.Combine(zapretRoot, "lua")));
            }
            catch (Exception)
            {
                // Не прочиталось — проверять нечем, а объявлять непроверенную нехватку нельзя.
            }

            var groups = chosen
                .GroupBy(r => r.Recipe!, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var recipe = RecipeResolver.Find(preset, group.Key, providers);

                    var domains = (IReadOnlyList<string>)group
                        .SelectMany(Names)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (!recipe.Found)
                    {
                        warnings.Add($"nfqws2: рецепт «{group.Key}» не найден ни в пресете «{preset.Name}», "
                            + $"ни в каталоге — профиль не создан, имён затронуто {domains.Count}.");
                    }
                    else if (recipe.MissingModules.Count > 0)
                    {
                        warnings.Add($"nfqws2: рецепт «{group.Key}» требует модулей, которых пресет «{preset.Name}» "
                            + $"не подключает: {string.Join(", ", recipe.MissingModules)}. Профиль не создан — "
                            + "иначе nfqws2 не запустился бы вовсе.");
                    }

                    return (
                        Name: group.Key,
                        Steps: recipe.MissingModules.Count > 0 ? [] : recipe.Steps,
                        Domains: domains,

                        // Порты — у секции, которую профиль подменяет: свой профиль
                        // стоит первым и обязан покрыть то же, что покрыла бы она.
                        Ports: (string?)PresetPorts.ForDomains(preset, zapretRoot, domains));
                })
                .ToList();

            OwnDesyncLists.Write(WinwsCommandLine.WriteOwnLists(groups, directory), path);
        }
        catch (Exception)
        {
            // Своя настройка не должна мешать запуску: без неё — пресет как есть.
            OwnDesyncLists.Write([], path);
        }
    }
}
#endif
