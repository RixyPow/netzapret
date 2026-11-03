using System.Text;
using NetZapret.Core.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NetZapret.Core.Rules;

/// <summary>Маршрут в пакете: правило со всем выбором человека и, у своего списка, сам список.</summary>
public sealed record SharedRoute
{
    public required MatchKind Match { get; init; }

    /// <summary>Значение правила: путь списка, своё имя или подсеть.</summary>
    public required string Value { get; init; }

    /// <summary>Своего правила нет — путь по умолчанию. Только у части каталога: свой маршрут и есть правило.</summary>
    public bool Default { get; init; }

    public RoutingMode Mode { get; init; }

    /// <summary>Часть выключена (<see cref="RoutingRule.Off"/>).</summary>
    public bool Off { get; init; }

    /// <summary>Запись в файле правил выключена (<c>enabled: false</c>) — хранится, но не действует.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Свой рецепт десинка; <c>null</c> — решает пресет.</summary>
    public string? Recipe { get; init; }

    /// <summary>Пул серверов по имени; <c>null</c> — пул по умолчанию.</summary>
    /// <remarks>
    /// Имя, а не ключ: ключ пула — восемь случайных знаков (<see cref="ServerPools.Create"/>),
    /// у получателя под ним нет ничего, а пул «Россия» у него может быть свой.
    /// </remarks>
    public string? Pool { get; init; }

    /// <summary>
    /// Имена и адреса своего списка; <c>null</c> — список не свой: часть каталога
    /// везёт сама программа, у своего домена и подсети списка нет.
    /// </summary>
    public IReadOnlyList<string>? List { get; init; }

    /// <summary>
    /// Место правила среди правил пакета в файле правил отправителя;
    /// <c>null</c> — своего правила нет.
    /// </summary>
    /// <remarks>
    /// Порядок в пакете — порядок в папке, как её видно, а движок берёт первое
    /// совпавшее правило файла, и эти порядки не обязаны совпадать: узкий свой
    /// список, стоящий в файле раньше широкого, после загрузки не должен
    /// оказаться за ним. Место — среди своих, а не номер в чужом файле:
    /// номер выдал бы, сколько у отправителя правил.
    /// </remarks>
    public int? Priority { get; init; }

    /// <summary>Путь словами: «VPN, пул «Россия»», «десинк, рецепт «…»».</summary>
    public string DescribeMode()
    {
        var mode = Default ? "по умолчанию" : Off ? "выключено" : Mode switch
        {
            RoutingMode.Proxy => Pool is { } pool ? $"VPN, пул «{pool}»" : "VPN",
            RoutingMode.Desync => Recipe is { } recipe ? $"десинк, рецепт «{recipe}»" : "десинк",
            _ => "напрямую",
        };

        return Enabled ? mode : mode + " (запись отключена)";
    }
}

/// <summary>
/// Пакет маршрута: один маршрут или одна папка — то, что «Экспорт…» кладёт в файл.
/// </summary>
public sealed record RouteBundle
{
    /// <summary>Папка — её ключ (имя до переименования); <c>null</c> — маршрут сам по себе.</summary>
    public string? Folder { get; init; }

    /// <summary>Имя папки на экране, если её переименовали (<see cref="AppSettings.FolderLabels"/>).</summary>
    public string? Label { get; init; }

    /// <summary>Папка — программа (<see cref="ProgramRoutes"/>): её имя в <see cref="Folder"/>.</summary>
    public bool Program { get; init; }

    /// <summary>Автопополнение программы.</summary>
    public bool AutoFill { get; init; }

    /// <summary>Маршруты в порядке папки.</summary>
    public required IReadOnlyList<SharedRoute> Routes { get; init; }

    /// <summary>
    /// Чего не взято: маршрутов, которых не перенести, и строк своих списков,
    /// которые не имя и не адрес. Только у собранного или прочитанного — в файл не пишется.
    /// </summary>
    public int Left { get; init; }

    /// <summary>Что в пакете — для людей: «папка «Discord»», «программа REPO.exe», «svtv.org».</summary>
    public string Caption =>
        Program ? $"программа {Folder}"
        : Folder is { } folder ? $"папка «{Label ?? folder}»"
        : Routes.Count > 0 ? $"«{RouteBundles.NameOf(Routes[0])}»"
        : "пустой маршрут";
}

/// <summary>Что загрузка сделает с маршрутом.</summary>
public enum RouteImportAction
{
    /// <summary>Такого нет — появится.</summary>
    Add,

    /// <summary>Такой есть — заменится правило и список.</summary>
    Replace,

    /// <summary>Такой есть и не отличается.</summary>
    Same,

    /// <summary>Не загрузится — причина в <see cref="RouteImportStep.Note"/>.</summary>
    Skip,
}

/// <summary>Маршрут пакета на этой машине: что с ним будет и почему.</summary>
public sealed record RouteImportStep
{
    /// <summary>Маршрут в том виде, в каком запишется: список мог лечь под другим названием, пула могло не найтись.</summary>
    public required SharedRoute Route { get; init; }

    public required RouteImportAction Action { get; init; }

    /// <summary>Поле сервера правила здесь: пул получателя с тем же именем; <c>null</c> — пул по умолчанию.</summary>
    public string? Server { get; init; }

    /// <summary>Путь здесь до загрузки, словами; <c>null</c> — маршрута нет.</summary>
    public string? Was { get; init; }

    /// <summary>Что стоит знать: другое название списка, нет пула, нет рецепта, причина пропуска.</summary>
    public string? Note { get; init; }

    public string Name => RouteBundles.NameOf(Route);

    /// <summary>Ключ строки «Маршрутов» (<see cref="RouteFolders.KeyOf"/>).</summary>
    public string Key => RouteFolders.KeyOf(Route.Match, Route.Value);

    /// <summary>Строка перечня в вопросе перед загрузкой.</summary>
    public string Describe() => Action switch
    {
        RouteImportAction.Replace when Was is { } was => $"{Name}: {was} → {Route.DescribeMode()}",
        RouteImportAction.Skip => $"{Name} — {Note}",
        _ => $"{Name} → {Route.DescribeMode()}",
    };
}

/// <summary>Что загрузка пакета сделает здесь (<see cref="RouteBundles.Plan"/>).</summary>
public sealed record RouteImportPlan
{
    public required RouteBundle Bundle { get; init; }

    public required IReadOnlyList<RouteImportStep> Steps { get; init; }

    /// <summary>Программа пакета — имя файла (<see cref="ProgramRoutes.Normalize"/>).</summary>
    public string? Program { get; init; }

    /// <summary>Программы здесь нет — заведётся.</summary>
    public bool NewProgram { get; init; }

    /// <summary>Папки здесь нет — заведётся (<see cref="AppSettings.MadeFolders"/>).</summary>
    public bool NewFolder { get; init; }

    /// <summary>Сколько маршрутов переляжет в папку пакета.</summary>
    public int Moves { get; init; }

    /// <summary>Сменятся имя папки или автопополнение программы.</summary>
    public bool Retitled { get; init; }

    /// <summary>Маршруты получателя в той же папке, которых нет в пакете: они остаются.</summary>
    public int Staying { get; init; }

    /// <summary>Загружать нечего: всё уже так.</summary>
    public bool Nothing =>
        !NewProgram && !NewFolder && Moves == 0 && !Retitled
        && Steps.All(s => s.Action is RouteImportAction.Same or RouteImportAction.Skip);
}

/// <summary>Что загрузка сделала.</summary>
/// <param name="Backup">Папка с прежним файлом правил и заменёнными списками; <c>null</c> — копировать было нечего.</param>
public sealed record RouteImportResult(int Added, int Replaced, string? Backup);

/// <summary>
/// Один маршрут или папка маршрутов файлом: экспорт и загрузка с заменой.
/// </summary>
/// <remarks>
/// <para>
/// «Маршруты файлом» (<see cref="RulesShare"/>) отдают файл правил целиком,
/// и свои списки с ним не едут — правило на них у получателя не ловит ничего.
/// Пакет — для одного маршрута или одной папки: правила с выбором человека
/// (путь, рецепт, пул, выключенность, порядок) и содержимое своих списков
/// и списков программы. Списки каталога не едут: они в программе.
/// </para>
/// <para>
/// Поля маршрута те же, что в rules.user.yaml (match, value, mode, recipe,
/// enabled), но под ключом <c>routes</c>, а не <c>rules</c>: «Загрузить»
/// версий до пакетов прочла бы <c>rules</c> как все маршруты и заменила бы
/// ими сотню чужих. Ключ <c>routes</c> она не знает и скажет «в файле
/// нет ни одного маршрута».
/// </para>
/// <para>
/// Решения (03.11): тот же маршрут — то же правило (тип и значение), у части
/// каталога это её список; та же папка — то же имя папки, программа — то же
/// имя файла. Свой список с тем же названием — тот же маршрут, только если
/// с присланным у него есть общие имена или он пуст; иначе это чужой список
/// с совпавшим названием, и присланный ложится рядом («games-2»), а не поверх.
/// Пул — по имени; нет такого у получателя — через VPN в пул по умолчанию,
/// и об этом говорится до загрузки. Маршруты получателя в той же папке,
/// которых нет в пакете, остаются: загрузка заменяет присланное, а не
/// удаляет чужую работу.
/// </para>
/// <para>
/// Секретного в пакете нет: значения правил — пути внутри config\lists,
/// имена и подсети, пул — именем, ссылок подписок, ключей и тегов серверов
/// нет вовсе. Строки списков берутся, только если это имя или адрес:
/// всё прочее (вписанная по ошибке ссылка vless://, путь) остаётся дома.
/// </para>
/// </remarks>
public static class RouteBundles
{
    /// <summary>Метка пакета в файле — по ней «Загрузить» отличает его от файла правил.</summary>
    public const string Marker = "netzapret-route";

    /// <summary>Версия формата: файл новее программы не читается, а не читается наполовину.</summary>
    public const int Version = 1;

    /// <summary>
    /// Собирает пакет из маршрутов, как они стоят в папке.
    /// </summary>
    /// <param name="routes">Маршруты в порядке папки: тип и значение правила.</param>
    /// <param name="folder">Папка — её ключ; <c>null</c> — маршрут сам по себе.</param>
    /// <param name="root">Корень, от которого считаются пути списков; <c>null</c> — рабочая папка.</param>
    public static RouteBundle Export(
        IEnumerable<(MatchKind Match, string Value)> routes,
        string? folder,
        UserRulesFile rules,
        AppSettings settings,
        string? root = null)
    {
        var shared = new List<SharedRoute>();
        int left = 0;

        foreach (var (match, value) in routes)
        {
            if (Portable(match, value) is null)
            {
                left++;
                continue;
            }

            int index = IndexOf(rules, match, value);
            var entry = index >= 0 ? rules.Entries[index] : null;
            bool own = match is MatchKind.Domain or MatchKind.Ip || OwnLists.IsOwn(value);
            bool program = ProgramRoutes.IsProgramList(value);

            // Свой маршрут без правила — его нет. У списка программы правило
            // заводится вместе с ним, а без правила окно показывает его выключенным.
            if (entry is null && own && !program)
            {
                left++;
                continue;
            }

            IReadOnlyList<string>? list = null;

            if (match == MatchKind.HostList && OwnLists.IsOwn(value))
            {
                var lines = OwnLists.Names(value, root);
                list = lines.Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                left += lines.Count - list.Count;
            }

            shared.Add(entry is null
                ? new SharedRoute { Match = match, Value = value, Default = !own, Off = own, List = list }
                : new SharedRoute
                {
                    Match = match,
                    Value = value,
                    Mode = entry.Mode,
                    Off = entry.Off,
                    Enabled = entry.Enabled,
                    Recipe = !entry.Off && entry.Mode == RoutingMode.Desync ? entry.Recipe : null,
                    Pool = PoolOf(entry, settings),
                    List = list,
                    Priority = index,
                });
        }

        var ranks = shared
            .Where(r => r.Priority is not null)
            .OrderBy(r => r.Priority)
            .Select((r, rank) => (r.Priority!.Value, rank))
            .ToDictionary(p => p.Value, p => p.rank);

        var programRoute = folder is null ? null : settings.ProgramRoutes.FirstOrDefault(p => ProgramRoutes.Same(p.Program, folder));
        string? label = null;

        if (folder is not null && programRoute is null && settings.FolderLabels.TryGetValue(folder, out var shown))
            label = shown;

        return new RouteBundle
        {
            Folder = folder,
            Label = label,
            Program = programRoute is not null,
            AutoFill = programRoute?.AutoFill ?? false,
            Routes = shared.Select(r => r.Priority is { } at ? r with { Priority = ranks[at] } : r).ToList(),
            Left = left,
        };
    }

    /// <summary>Записывает пакет — YAML с теми же полями маршрута, что у файла правил.</summary>
    public static void Write(RouteBundle bundle, string path)
    {
        var text = new StringBuilder();

        text.AppendLine($"# Маршрут NetZapret: {bundle.Caption}.");
        text.AppendLine("#");
        text.AppendLine("# Загрузить: «Маршруты» → «Добавить правило» → «Из файла…». Такой же");
        text.AppendLine("# маршрут или папка у вас заменятся, нет — появятся. Свои списки едут");
        text.AppendLine("# здесь же; пинов, ссылок подписок и ключей серверов в файле нет.");
        text.AppendLine();
        text.AppendLine($"export: {Marker}");
        text.AppendLine($"version: {Version}");

        if (bundle.Folder is { } folder)
        {
            text.AppendLine("folder:");
            text.AppendLine($"  name: {Quote(folder)}");

            if (bundle.Label is { } label)
                text.AppendLine($"  label: {Quote(label)}");

            if (bundle.Program)
            {
                text.AppendLine("  program: true");
                text.AppendLine($"  autofill: {(bundle.AutoFill ? "true" : "false")}");
            }
        }

        if (bundle.Routes.Count == 0)
        {
            text.AppendLine("routes: []");
        }
        else
        {
            text.AppendLine("routes:");

            foreach (var route in bundle.Routes)
            {
                text.AppendLine($"  - match: {MatchName(route.Match)}");
                text.AppendLine($"    value: {Quote(route.Value)}");
                text.AppendLine($"    mode: {ModeName(route)}");

                if (route.Pool is { } pool)
                    text.AppendLine($"    pool: {Quote(pool)}");

                if (route.Recipe is { } recipe)
                    text.AppendLine($"    recipe: {Quote(recipe)}");

                if (!route.Enabled)
                    text.AppendLine("    enabled: false");

                if (route.Priority is { } priority)
                    text.AppendLine($"    priority: {priority}");

                if (route.List is { Count: 0 })
                {
                    text.AppendLine("    list: []");
                }
                else if (route.List is { } list)
                {
                    text.AppendLine("    list:");

                    foreach (var line in list)
                        text.AppendLine($"      - {Quote(line)}");
                }
            }
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Похож ли файл на пакет — по метке, без разбора.</summary>
    /// <remarks>Для «Загрузить»: файл одного маршрута не должен заменить собой все.</remarks>
    public static bool Looks(string path)
    {
        try
        {
            return File.ReadLines(path)
                .Take(40)
                .Any(line => line.Replace(" ", string.Empty).Trim() == "export:" + Marker);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Читает пакет; не пакет или не разобрался — <see cref="InvalidDataException"/> с причиной.</summary>
    /// <remarks>
    /// Значения здесь не проверяются — только разбираются. Проверяет их
    /// <see cref="Plan"/>: файл чужой, и что в нём записано, решает не он.
    /// </remarks>
    public static RouteBundle Read(string path)
    {
        FileDto? dto;

        try
        {
            dto = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build()
                .Deserialize<FileDto>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is not IOException and not UnauthorizedAccessException)
        {
            throw new InvalidDataException("файл не разбирается: " + ex.GetBaseException().Message, ex);
        }

        if (dto is null || !string.Equals(dto.Export?.Trim(), Marker, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("это не маршрут NetZapret");

        if (dto.Version > Version)
            throw new InvalidDataException("файл сделан более новой версией NetZapret — обновите программу");

        var routes = new List<SharedRoute>();
        int left = 0;

        foreach (var raw in dto.Routes ?? [])
        {
            if (raw is null || string.IsNullOrWhiteSpace(raw.Value) || ParseMatch(raw.Match) is not { } match
                || ParseMode(raw.Mode) is not var (mode, off, isDefault))
            {
                left++;
                continue;
            }

            routes.Add(new SharedRoute
            {
                Match = match,
                Value = raw.Value.Trim(),
                Default = isDefault,
                Mode = mode,
                Off = off,
                Enabled = raw.Enabled ?? true,
                Recipe = string.IsNullOrWhiteSpace(raw.Recipe) ? null : raw.Recipe.Trim(),
                Pool = string.IsNullOrWhiteSpace(raw.Pool) ? null : raw.Pool.Trim(),
                Priority = raw.Priority,
                List = raw.List?.Where(l => l is not null).Select(l => l.Trim()).ToList(),
            });
        }

        var folder = string.IsNullOrWhiteSpace(dto.Folder?.Name) ? null : dto.Folder.Name.Trim();

        return new RouteBundle
        {
            Folder = folder,
            Label = folder is null || string.IsNullOrWhiteSpace(dto.Folder?.Label) ? null : dto.Folder.Label.Trim(),
            Program = folder is not null && dto.Folder?.Program == true,
            AutoFill = dto.Folder?.Autofill ?? true,
            Routes = routes,
            Left = left,
        };
    }

    /// <summary>
    /// Что загрузка пакета сделает здесь — до записи, для вопроса.
    /// </summary>
    /// <param name="recipeKnown">Знает ли эта машина рецепт (пресет или каталог); <c>null</c> — не проверять.</param>
    public static RouteImportPlan Plan(
        RouteBundle bundle,
        UserRulesFile rules,
        AppSettings settings,
        string? root = null,
        Func<string, bool>? recipeKnown = null)
    {
        var folder = bundle.Folder;
        var program = bundle.Program ? ProgramRoutes.Normalize(folder) : null;
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var steps = bundle.Routes
            .Select(route => Step(route, program, rules, settings, root, recipeKnown, taken))
            .ToList();

        // Программа с негодным именем — всё мимо: списки программы без неё ничьи.
        if (bundle.Program && program is null)
            steps = steps.Select(s => s with { Action = RouteImportAction.Skip, Note = "имя программы не годится" }).ToList();

        var live = steps.Where(s => s.Action != RouteImportAction.Skip).ToList();
        bool catalogFolder = folder is not null && !bundle.Program && IsCatalogFolder(folder);

        bool newProgram = program is not null && !settings.ProgramRoutes.Any(p => ProgramRoutes.Same(p.Program, program));
        bool newFolder = folder is not null && !bundle.Program && !catalogFolder
            && !settings.MadeFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)
            && !settings.RouteFolders.Values.Any(f => string.Equals(f.Trim(), folder, StringComparison.OrdinalIgnoreCase));

        int moves = folder is null || bundle.Program
            ? 0
            : live.Count(s => !string.Equals(FolderNow(s, settings), folder, StringComparison.OrdinalIgnoreCase));

        bool retitled = program is not null && !newProgram
            ? settings.ProgramRoutes.First(p => ProgramRoutes.Same(p.Program, program)).AutoFill != bundle.AutoFill
            : folder is not null && bundle.Label is { } label
                && !(settings.FolderLabels.TryGetValue(folder, out var was) && was == label);

        return new RouteImportPlan
        {
            Bundle = bundle,
            Steps = steps,
            Program = program,
            NewProgram = newProgram,
            NewFolder = newFolder,
            Moves = moves,
            Retitled = retitled,
            Staying = folder is null || bundle.Program ? 0 : Staying(folder, catalogFolder, live, rules, settings),
        };
    }

    /// <summary>
    /// Загружает: списки, правила, папку и порядок — и копию прежнего в runtime.
    /// </summary>
    /// <param name="backupFolder">Куда класть копию; <c>null</c> — <c>runtime</c> корня.</param>
    /// <remarks>
    /// Копируются файл правил и заменяемые списки — настройки нет: в них ссылки
    /// подписок, а копии личного копятся и уезжают (CLAUDE.md, «Секреты»).
    /// Перекладка в папку и порядок — только вид, их потеря стоит щелчка.
    /// </remarks>
    public static RouteImportResult Apply(
        RouteImportPlan plan,
        string? rulesPath = null,
        string? settingsPath = null,
        string? root = null,
        string? backupFolder = null)
    {
        var rulesFile = rulesPath ?? UserRulesFile.DefaultPath;
        var settingsFile = settingsPath ?? AppSettings.DefaultPath;

        var live = plan.Steps.Where(s => s.Action != RouteImportAction.Skip).ToList();
        var writes = live.Where(s => s.Action is RouteImportAction.Add or RouteImportAction.Replace).ToList();

        var backup = Backup(writes, rulesFile, root, backupFolder);

        foreach (var step in writes.Where(s => s.Route.List is not null))
            WriteList(step.Route.Value, step.Route.List!, plan.Program, root);

        if (writes.Count > 0 || plan.NewProgram)
        {
            var rules = UserRulesFile.Load(rulesFile);

            foreach (var step in writes)
                Put(rules, step);

            // Новая программа — наверх, как у заведённой окном (ProgramRoutes.Add):
            // её маршруты узкие и выбраны нарочно.
            if (plan.NewProgram && plan.Program is { } program)
            {
                rules.Move(IndexOf(rules, MatchKind.HostList, ProgramRoutes.DomainsPath(program)), 0);
                rules.Move(IndexOf(rules, MatchKind.HostList, ProgramRoutes.AddressesPath(program)), 1);
            }

            Rank(rules, live);
            rules.Save();
        }

        if (plan.Bundle.Folder is not null)
            Place(plan, live, settingsFile);

        return new RouteImportResult(
            writes.Count(s => s.Action == RouteImportAction.Add),
            writes.Count(s => s.Action == RouteImportAction.Replace),
            backup);
    }

    /// <summary>Имя маршрута для людей: «Discord · Голос», название своего списка, имя, подсеть.</summary>
    public static string NameOf(SharedRoute route)
    {
        var value = route.Value.Replace('\\', '/').Trim();

        if (route.Match is MatchKind.Domain)
            return value.TrimStart('*', '.');

        if (route.Match is not (MatchKind.HostList or MatchKind.IpSet))
            return value;

        if (ProgramRoutes.IsProgramList(value))
            return value.EndsWith(".domains.txt", StringComparison.OrdinalIgnoreCase) ? "Домены" : "Адреса";

        if (OwnLists.IsOwn(value))
            return OwnLists.NameOf(value);

        foreach (var service in ServiceCatalog.All)
        {
            if (service.Parts.FirstOrDefault(p => string.Equals(p.List, value, StringComparison.OrdinalIgnoreCase)) is { } part)
                return service.Grouping ? part.Name : service.Parts.Count == 1 ? service.Name : $"{service.Name} · {part.Name}";
        }

        return OwnLists.NameOf(value);
    }

    /// <summary>
    /// Значение правила, годное для пакета, в каноническом виде; <c>null</c> — такое не переносится.
    /// </summary>
    /// <remarks>
    /// Списки — только свои (по названию, без подпапок), программы и части
    /// каталога этой версии: правило на путь вне config\lists не ловит ничего
    /// у получателя, а при загрузке по нему писался бы файл где попало.
    /// </remarks>
    internal static string? Portable(MatchKind match, string value)
    {
        var text = value.Replace('\\', '/').Trim();

        switch (match)
        {
            case MatchKind.Domain:
                bool star = text.StartsWith("*.", StringComparison.Ordinal);
                return CleanName(star ? text[2..] : text) is { } name ? (star ? "*." : string.Empty) + name : null;

            case MatchKind.Ip:
                return AddressInput.Normalize(text);

            case MatchKind.HostList when ProgramRoutes.IsProgramList(text):
            {
                var file = text[ProgramRoutes.Folder.Length..];

                return !file.Contains('/') && file.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
                    && (file.EndsWith(".domains.txt", StringComparison.OrdinalIgnoreCase)
                        || file.EndsWith(".addresses.txt", StringComparison.OrdinalIgnoreCase))
                    ? text
                    : null;
            }

            case MatchKind.HostList when OwnLists.IsOwn(text):
                return OwnLists.Normalize(OwnLists.NameOf(text)) is { } own
                    && string.Equals(OwnLists.PathFor(own), text, StringComparison.OrdinalIgnoreCase)
                        ? OwnLists.PathFor(own)
                        : null;

            case MatchKind.HostList or MatchKind.IpSet:
                return ServiceCatalog.All
                    .SelectMany(s => s.Parts)
                    .FirstOrDefault(p => p.ByAddress == (match == MatchKind.IpSet)
                        && string.Equals(p.List, text, StringComparison.OrdinalIgnoreCase))
                    ?.List;

            default:
                // Правила по программе сняты (ProgramRulesOff) и не везутся.
                return null;
        }
    }

    /// <summary>Строка своего списка — имя или адрес; <c>null</c> — ни то ни другое, не везётся.</summary>
    internal static string? Clean(string line)
    {
        var text = line.Split('#')[0].Trim();

        return AddressInput.Normalize(text) ?? CleanName(text.TrimStart('*', '.'));
    }

    /// <remarks>
    /// Только буквы, цифры, дефис, подчёркивание и точки, и точка обязательна:
    /// так в пакет не пролезут ни ссылка с ключом, ни путь, ни строка без
    /// точки вроде UUID, которую человек мог вставить в список по ошибке.
    /// </remarks>
    private static string? CleanName(string text)
    {
        var name = text.Trim().ToLowerInvariant();

        return name.Length is > 0 and <= 253
            && name.Contains('.')
            && !name.StartsWith('.') && !name.EndsWith('.') && !name.Contains("..")
            && name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
                ? name
                : null;
    }

    private static RouteImportStep Step(
        SharedRoute route,
        string? program,
        UserRulesFile rules,
        AppSettings settings,
        string? root,
        Func<string, bool>? recipeKnown,
        ISet<string> taken)
    {
        RouteImportStep Skip(string why) => new() { Route = route, Action = RouteImportAction.Skip, Note = why };

        var value = route.Value.Replace('\\', '/').Trim();
        bool programList = route.Match == MatchKind.HostList && ProgramRoutes.IsProgramList(value);
        string? note = null;
        string target;

        if (programList)
        {
            if (program is null)
                return Skip("список программы едет только с её папкой");

            if (Same(value, ProgramRoutes.DomainsPath(program)))
                target = ProgramRoutes.DomainsPath(program);
            else if (Same(value, ProgramRoutes.AddressesPath(program)))
                target = ProgramRoutes.AddressesPath(program);
            else
                return Skip("список другой программы");
        }
        else if (route.Match == MatchKind.HostList && OwnLists.IsOwn(value))
        {
            if (Portable(route.Match, value) is not { } path)
                return Skip("название списка не годится для файла");

            (target, note) = OwnTarget(path, route.List ?? [], rules, root, taken);
            taken.Add(target);
        }
        else if (Portable(route.Match, value) is { } portable)
        {
            target = portable;
        }
        else
        {
            return Skip(route.Match switch
            {
                MatchKind.Process => "правила по программе сняты — программа теперь папка своих списков",
                MatchKind.HostList or MatchKind.IpSet => "в этой версии программы такого маршрута нет",
                _ => "не похоже ни на имя, ни на адрес",
            });
        }

        bool listed = OwnLists.IsOwn(target);

        // Свой маршрут — это и есть правило: «по умолчанию» у него нет.
        if (route.Default && (listed || route.Match is MatchKind.Domain or MatchKind.Ip))
            return Skip("свой маршрут без пути");

        var cleaned = route with
        {
            Value = target,
            Recipe = !route.Default && !route.Off && route.Mode == RoutingMode.Desync ? route.Recipe : null,
            Pool = !route.Default && !route.Off && route.Mode == RoutingMode.Proxy ? route.Pool : null,
            List = listed
                ? (route.List ?? []).Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : null,
        };

        // Пул — по имени: ключ пула у каждого свой.
        string? server = null;

        if (cleaned.Pool is { } poolName)
        {
            if (settings.Pools.FirstOrDefault(p => string.Equals(p.Name.Trim(), poolName, StringComparison.OrdinalIgnoreCase)) is { } pool)
            {
                server = ServerPools.RuleServer(pool.Id);
            }
            else
            {
                note = Join(note, $"пула «{poolName}» у вас нет — пойдёт через VPN в пул по умолчанию");
                cleaned = cleaned with { Pool = null };
            }
        }

        if (cleaned.Recipe is { } recipe && recipeKnown is not null && !recipeKnown(recipe))
            note = Join(note, $"рецепта «{recipe}» нет ни в вашем пресете, ни в каталоге — пока его нет, имя чинит пресет");

        int index = IndexOf(rules, route.Match, target);
        var before = index >= 0 ? rules.Entries[index] : null;
        var have = listed ? OwnLists.Names(target, root).Select(Clean).OfType<string>().ToList() : null;

        // Часть каталога у получателя есть всегда — «по умолчанию»; присланный
        // путь её заменяет, а не добавляет.
        bool catalog = !listed && route.Match is MatchKind.HostList or MatchKind.IpSet;
        bool exists = catalog || before is not null || (listed && File.Exists(Full(target, root)));

        var action = !exists
            ? cleaned.Default ? RouteImportAction.Same : RouteImportAction.Add
            : Equal(before, cleaned, server, have) ? RouteImportAction.Same : RouteImportAction.Replace;

        var was = before is null ? listed ? "без правила" : "по умолчанию" : Describe(before, settings);

        if (action == RouteImportAction.Replace && have is not null && cleaned.List is { } incoming
            && !have.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(incoming))
        {
            was += $" (в списке {have.Count}, станет {incoming.Count})";
        }

        return new RouteImportStep
        {
            Route = cleaned,
            Action = action,
            Server = server,
            Was = exists ? was : null,
            Note = note,
        };
    }

    /// <summary>
    /// Куда ляжет свой список: то же название, если это тот же маршрут, иначе свободное рядом.
    /// </summary>
    /// <remarks>
    /// Тот же — у которого с присланным есть общие имена, или пустой: пустому
    /// терять нечего. Совпало одно название — это чужой список: «games» у двух
    /// людей — разные игры, и затереть его присланным значило бы молча
    /// увести чужие имена. Следующая загрузка того же файла найдёт «games-2»
    /// по общим именам и заменит его, а не заведёт «games-3».
    /// </remarks>
    private static (string Target, string? Note) OwnTarget(
        string path,
        IReadOnlyList<string> incoming,
        UserRulesFile rules,
        string? root,
        ISet<string> taken)
    {
        var name = OwnLists.NameOf(path);
        var wanted = incoming.Select(Clean).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Название с приставкой «-N» не длиннее 64 знаков, как у заведённого окном (OwnLists.Normalize).
        var stem = name.Length > 60 ? name[..60] : name;

        for (int n = 1; n < 1000; n++)
        {
            var candidate = n == 1 ? name : $"{stem}-{n}";
            var value = OwnLists.PathFor(candidate);

            if (taken.Contains(value))
                continue;

            bool exists = IndexOf(rules, MatchKind.HostList, value) >= 0 || OwnLists.Exists(candidate, root);

            if (!exists)
            {
                return (value, n == 1 ? null
                    : $"у вас уже есть свой список «{name}» с другими именами — присланный ляжет рядом как «{candidate}»");
            }

            var have = OwnLists.Names(value, root).Select(Clean).OfType<string>().ToList();

            if (have.Count == 0 || have.Any(wanted.Contains))
                return (value, n == 1 ? null : $"присланный «{name}» у вас лежит как «{candidate}» — заменится он");
        }

        throw new IOException($"Не нашлось свободного названия для списка «{name}».");
    }

    private static bool Equal(UserRuleEntry? before, SharedRoute route, string? server, IReadOnlyList<string>? have)
    {
        if (route.Default)
            return before is null;

        if (before is null || before.Off != route.Off || before.Enabled != route.Enabled)
            return false;

        if (!route.Off)
        {
            var recipe = before.Mode == RoutingMode.Desync ? before.Recipe : null;

            if (before.Mode != route.Mode
                || !string.Equals(recipe ?? string.Empty, route.Recipe ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(before.Server ?? string.Empty, server ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return route.List is null
            || (have is not null && have.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(route.List));
    }

    private static string Describe(UserRuleEntry entry, AppSettings settings) =>
        new SharedRoute
        {
            Match = entry.Match,
            Value = entry.Value,
            Mode = entry.Mode,
            Off = entry.Off,
            Enabled = entry.Enabled,
            Recipe = !entry.Off && entry.Mode == RoutingMode.Desync ? entry.Recipe : null,
            Pool = PoolOf(entry, settings),
        }.DescribeMode();

    private static string? PoolOf(UserRuleEntry entry, AppSettings settings) =>
        entry is { Off: false, Mode: RoutingMode.Proxy }
        && ServerPools.Find(settings.Pools, ServerPools.IdOf(entry.Server)) is { } pool
            ? pool.Name
            : null;

    private static bool IsCatalogFolder(string folder) =>
        ServiceCatalog.Find(folder) is { Grouping: false, Parts.Count: > 1 };

    /// <summary>В какой папке маршрут стоит сейчас: перекладка человека или место по каталогу.</summary>
    private static string? FolderNow(RouteImportStep step, AppSettings settings) =>
        settings.RouteFolders.TryGetValue(step.Key, out var moved)
            ? moved.Trim() is { Length: > 0 } folder ? folder : null
            : RouteFolders.HomeOf(step.Route.Match, step.Route.Value);

    /// <summary>Маршруты получателя в папке, которых нет в пакете.</summary>
    private static int Staying(
        string folder,
        bool catalogFolder,
        IReadOnlyList<RouteImportStep> live,
        UserRulesFile rules,
        AppSettings settings)
    {
        var mine = live.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        var keys = settings.RouteFolders
            .Where(m => string.Equals(m.Value.Trim(), folder, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Key)
            .Where(key => Alive(key, rules));

        if (catalogFolder)
        {
            keys = keys.Concat(ServiceCatalog.Find(folder)!.Parts
                .Select(p => RouteFolders.KeyOf(p.ByAddress ? MatchKind.IpSet : MatchKind.HostList, p.List))
                .Where(key => !settings.RouteFolders.ContainsKey(key)));
        }

        return keys.Where(key => !mine.Contains(key)).Distinct(StringComparer.Ordinal).Count();
    }

    /// <summary>Есть ли ещё маршрут под ключом перекладки: свой мог быть убран, а запись осталась.</summary>
    private static bool Alive(string key, UserRulesFile rules)
    {
        if (key.Split('|', 2) is not [var kind, var value])
            return false;

        return kind switch
        {
            "own" => IndexOf(rules, OwnLists.IsOwn(value) ? MatchKind.HostList : MatchKind.Domain, value) >= 0,
            "net" => IndexOf(rules, MatchKind.Ip, value) >= 0,
            "hostlist" => Portable(MatchKind.HostList, value) is not null,
            "ipset" => Portable(MatchKind.IpSet, value) is not null,
            _ => false,
        };
    }

    private static void Put(UserRulesFile rules, RouteImportStep step)
    {
        var route = step.Route;

        if (route.Default)
        {
            rules.Remove(route.Match, route.Value);
            return;
        }

        // Часть внутри более широкого списка — раньше его правила, как пишет окно (ServicePart.Within).
        var within = ServiceCatalog.All
            .SelectMany(s => s.Parts)
            .FirstOrDefault(p => string.Equals(p.List, route.Value, StringComparison.OrdinalIgnoreCase))
            ?.Within;

        if (route.Off)
            rules.SetOff(route.Match, route.Value, before: within);
        else
            rules.Set(route.Match, route.Value, route.Mode, route.Recipe, before: within, server: step.Server);

        if (!route.Enabled && IndexOf(rules, route.Match, route.Value) is var at and >= 0)
            rules.ReplaceAt(at, rules.Entries[at] with { Enabled = false });
    }

    /// <summary>
    /// Правила пакета — в порядке отправителя, на тех местах файла, которые они заняли.
    /// </summary>
    /// <remarks>
    /// Места не меняются — переставляются только сами правила пакета: чужие
    /// правила получателя стоят, где стояли, а заменённое не уезжает в конец.
    /// </remarks>
    private static void Rank(UserRulesFile rules, IReadOnlyList<RouteImportStep> live)
    {
        var placed = live
            .Where(s => !s.Route.Default && s.Route.Priority is not null)
            .Select(s => (Priority: s.Route.Priority!.Value, At: IndexOf(rules, s.Route.Match, s.Route.Value)))
            .Where(p => p.At >= 0)
            .DistinctBy(p => p.At)
            .ToList();

        var slots = placed.Select(p => p.At).Order().ToList();
        var entries = placed.OrderBy(p => p.Priority).Select(p => rules.Entries[p.At]).ToList();

        for (int i = 0; i < slots.Count; i++)
            rules.ReplaceAt(slots[i], entries[i]);
    }

    /// <summary>Папка, программа, имя папки и порядок маршрутов в ней — в настройки.</summary>
    private static void Place(RouteImportPlan plan, IReadOnlyList<RouteImportStep> live, string settingsFile)
    {
        var bundle = plan.Bundle;
        var folder = bundle.Folder!;
        var settings = AppSettings.Load(settingsFile);
        var keys = live.Select(s => s.Key).ToList();

        if (plan.Program is { } program)
        {
            bool known = settings.ProgramRoutes.Any(p => ProgramRoutes.Same(p.Program, program));

            settings = settings with
            {
                ProgramRoutes = known
                    ? settings.ProgramRoutes.Select(p => ProgramRoutes.Same(p.Program, program) ? p with { AutoFill = bundle.AutoFill } : p).ToList()
                    : [.. settings.ProgramRoutes, new ProgramRoute(program, bundle.AutoFill)],
                RouteOrder = Block(settings.RouteOrder, keys),
            };

            settings.Save(settingsFile);
            return;
        }

        if (bundle.Program)
            return;

        var moved = settings.RouteFolders;

        foreach (var step in live)
            moved = RouteFolders.Move(moved, step.Key, folder, RouteFolders.HomeOf(step.Route.Match, step.Route.Value));

        IReadOnlyList<string> made = plan.NewFolder ? [.. settings.MadeFolders, folder] : settings.MadeFolders;

        var labels = new Dictionary<string, string>(settings.FolderLabels, StringComparer.OrdinalIgnoreCase);

        if (bundle.Label is { } label)
        {
            if (string.Equals(label, folder, StringComparison.Ordinal))
                labels.Remove(folder);
            else
                labels[folder] = label;
        }

        // Место пустой заведённой папки — впереди её маршрутов: иначе папка
        // встала бы там, где стояло её прежнее место, отдельно от них.
        if (made.Contains(folder, StringComparer.OrdinalIgnoreCase))
            keys.Insert(0, RouteFolders.Place(folder));

        (settings with
        {
            RouteFolders = moved,
            MadeFolders = made,
            FolderLabels = labels,
            RouteOrder = Block(settings.RouteOrder, keys),
        }).Save(settingsFile);
    }

    /// <summary>
    /// Ключи пакета — одним куском в порядке пакета: где стоял первый из них, или в начале.
    /// </summary>
    /// <remarks>
    /// Порядок в папке — порядок первого появления её ключей (<see cref="RouteFolders.Order"/>),
    /// так что кусок задаёт и его. Новой папки в порядке нет — она встаёт
    /// в начало, как заведённая кнопкой: новую надо видеть сразу.
    /// </remarks>
    internal static IReadOnlyList<string> Block(IReadOnlyList<string> order, IReadOnlyList<string> block)
    {
        var set = block.ToHashSet(StringComparer.Ordinal);
        int first = -1;

        for (int i = 0; i < order.Count && first < 0; i++)
        {
            if (set.Contains(order[i]))
                first = i;
        }

        var rest = order.Where(k => !set.Contains(k)).ToList();
        rest.InsertRange(Math.Max(first, 0), block.Distinct(StringComparer.Ordinal));

        return rest;
    }

    private static void WriteList(string value, IReadOnlyList<string> entries, string? program, string? root)
    {
        var header = ProgramRoutes.IsProgramList(value) && program is not null
            ? ProgramRoutes.Header(program, value.EndsWith(".domains.txt", StringComparison.OrdinalIgnoreCase) ? "домены" : "адреса")
            : OwnLists.Header(OwnLists.NameOf(value));

        var body = new StringBuilder(header);

        foreach (var line in entries)
            body.Append(line).Append("\r\n");

        var full = Full(value, root);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body.ToString(), new UTF8Encoding(false));
    }

    private static string? Backup(IReadOnlyList<RouteImportStep> writes, string rulesFile, string? root, string? folder)
    {
        if (writes.Count == 0)
            return null;

        var lists = writes
            .Where(s => s.Route.List is not null && File.Exists(Full(s.Route.Value, root)))
            .Select(s => s.Route.Value)
            .ToList();

        bool rules = File.Exists(rulesFile);

        if (!rules && lists.Count == 0)
            return null;

        var target = Path.Combine(
            folder ?? Path.Combine(root ?? Directory.GetCurrentDirectory(), "runtime"),
            $"route-import-{DateTime.Now:yyyyMMdd-HHmmss}");

        Directory.CreateDirectory(target);

        if (rules)
            File.Copy(rulesFile, Path.Combine(target, Path.GetFileName(rulesFile)), overwrite: true);

        foreach (var value in lists)
        {
            var copy = Path.Combine(target, value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(Full(value, root), copy, overwrite: true);
        }

        return target;
    }

    private static int IndexOf(UserRulesFile rules, MatchKind match, string value)
    {
        for (int i = 0; i < rules.Entries.Count; i++)
        {
            if (rules.Entries[i].Match == match && rules.Entries[i].Matches(value.Trim()))
                return i;
        }

        return -1;
    }

    private static bool Same(string a, string b) =>
        string.Equals(a.Replace('\\', '/').Trim(), b, StringComparison.OrdinalIgnoreCase);

    private static string? Join(string? note, string more) => note is null ? more : note + "; " + more;

    private static string Full(string value, string? root) =>
        Path.GetFullPath(Path.Combine(root ?? Directory.GetCurrentDirectory(), value));

    private static string MatchName(MatchKind match) => match switch
    {
        MatchKind.Domain => "domain",
        MatchKind.HostList => "hostlist",
        MatchKind.IpSet => "ipset",
        MatchKind.Ip => "ip",
        _ => "process",
    };

    private static string ModeName(SharedRoute route) =>
        route.Default ? "default"
        : route.Off ? "off"
        : route.Mode switch
        {
            RoutingMode.Proxy => "proxy",
            RoutingMode.Desync => "desync",
            _ => "direct",
        };

    private static MatchKind? ParseMatch(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "domain" => MatchKind.Domain,
            "hostlist" or "domainlist" => MatchKind.HostList,
            "ipset" or "iplist" => MatchKind.IpSet,
            "ip" or "cidr" => MatchKind.Ip,
            "process" => MatchKind.Process,
            _ => null,
        };

    private static (RoutingMode Mode, bool Off, bool Default)? ParseMode(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "default" => (RoutingMode.Direct, false, true),
            "off" or "выключено" => (RoutingMode.Direct, true, false),
            "direct" => (RoutingMode.Direct, false, false),
            "proxy" => (RoutingMode.Proxy, false, false),
            "desync" or "zapret" => (RoutingMode.Desync, false, false),
            _ => null,
        };

    /// <summary>Строка YAML в двойных кавычках — как в файле правил (<see cref="UserRulesFile"/>).</summary>
    /// <remarks>
    /// И с переводом строки: имя пула и папки человек вписывает сам, и перевод
    /// строки в нём разорвал бы файл, а не одно поле.
    /// </remarks>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private sealed class FileDto
    {
        public string? Export { get; set; }
        public int? Version { get; set; }
        public FolderDto? Folder { get; set; }
        public List<RouteDto?>? Routes { get; set; }
    }

    private sealed class FolderDto
    {
        public string? Name { get; set; }
        public string? Label { get; set; }
        public bool? Program { get; set; }
        public bool? Autofill { get; set; }
    }

    private sealed class RouteDto
    {
        public string? Match { get; set; }
        public string? Value { get; set; }
        public string? Mode { get; set; }
        public string? Recipe { get; set; }
        public string? Pool { get; set; }
        public bool? Enabled { get; set; }
        public int? Priority { get; set; }
        public List<string>? List { get; set; }
    }
}
