using System.Globalization;

namespace NetZapret.Zapret;

/// <summary>Откуда брать приёмы ByeDPI.</summary>
public enum ByeDpiStrategy
{
    /// <summary>Перевод пресета winws2 — те же секции, что на Windows, в группах ByeDPI.</summary>
    Preset,

    /// <summary>Строка по умолчанию (<see cref="ByeDpiCommandLine.DefaultStrategy"/>).</summary>
    Default,

    /// <summary>Своя строка человека (<see cref="ByeDpiRequest.CustomArguments"/>).</summary>
    Custom,
}

/// <summary>Что нужно для командной строки ByeDPI.</summary>
public sealed record ByeDpiRequest
{
    /// <summary>Файл пресета, <c>presets/&lt;имя&gt;.txt</c>.</summary>
    public required string PresetPath { get; init; }

    /// <summary>Корень поставки Zapret (<c>lists/</c>, <c>bin/</c>) — от него пути пресета.</summary>
    public required string ZapretRoot { get; init; }

    /// <summary>Куда хозяин запишет списки групп (<see cref="ByeDpiPlan.Files"/>).</summary>
    public required string WorkDirectory { get; init; }

    /// <summary>Порт слушателя на 127.0.0.1.</summary>
    public required int Port { get; init; }

    public ByeDpiStrategy Strategy { get; init; } = ByeDpiStrategy.Preset;

    /// <summary>Своя строка человека — только при <see cref="ByeDpiStrategy.Custom"/>.</summary>
    public string? CustomArguments { get; init; }

    /// <summary>Ядро умеет <c>TCP_MD5SIG</c>: только тогда в группах бывает <c>-S</c>.</summary>
    public bool Md5Sig { get; init; }

    /// <summary>
    /// UDP ведётся в ByeDPI. Нет — секции UDP не переводятся, и слушатель
    /// отклоняет UDP ASSOCIATE (<c>-U</c>).
    /// </summary>
    public bool Udp { get; init; }
}

/// <summary>Готовый запуск ByeDPI.</summary>
/// <param name="Arguments">Аргументы без имени файла.</param>
/// <param name="Files">Списки групп: путь от <see cref="ByeDpiRequest.WorkDirectory"/> → содержимое.</param>
/// <param name="Warnings">Что не переводится без root и что пришлось выбросить.</param>
/// <param name="PassHosts">Имена секций <c>pass</c>: sing-box ведёт их напрямую, мимо ByeDPI.</param>
public sealed record ByeDpiPlan(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> PassHosts);

/// <summary>
/// Командная строка ByeDPI — десинк на телефоне без root (<c>docs/android.md</c>,
/// заметка <c>byedpi.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// Пресет переводится секция за секцией в группы ByeDPI: группа отбирает
/// соединения теми же списками (<c>-H</c> — имена, <c>-j</c> — адреса) и
/// применяет перевод рецепта. Порядок групп — порядок секций: у ByeDPI, как
/// у winws2, побеждает первая подходящая группа (<c>find_dp</c>).
/// </para>
/// <para>
/// Групп у ByeDPI мало (<see cref="GroupLimit"/>), а секций в пресете —
/// семьдесят с лишним, поэтому соседние секции с одинаковым переводом
/// склеиваются в одну группу. Склеивать можно не всегда: секция, поднятая
/// к группе выше, обгоняет всё, что стоит между ними, и имя, которое
/// раньше забирала промежуточная секция, досталось бы ей. Поэтому склейка —
/// только когда ни одна секция с другим рецептом между ними не пересекается
/// с ней: по именам — у списков имён, по подсетям — у списков адресов.
/// Список имён и список адресов сравнить нельзя (адрес имени известен только
/// при соединении), такая пара считается пересекающейся: иначе секция
/// «discord.com (IP fallback)» с Google и Cloudflare целиком поднялась бы
/// к Telegram и забрала бы Google у всех секций по именам ниже.
/// </para>
/// </remarks>
public static class ByeDpiCommandLine
{
    /// <summary>Слушатель: только SOCKS5 и только 127.0.0.1 (по умолчанию ByeDPI слушает 0.0.0.0).</summary>
    public const string Listen = "socks5://127.0.0.1";

    /// <summary>
    /// Общие ключи: отказ на имена (<c>-N</c>), до 1024 клиентов, 3 с на ответ.
    /// </summary>
    /// <remarks>
    /// <c>-N</c> — потому что имя в запросе ByeDPI разрешает сам, синхронно
    /// и системным DNS мимо VPN, у провайдера, подменявшего открытый DNS летом
    /// 2026 года; адрес ему отдаёт sing-box (<c>resolve</c>). <c>-c 1024</c> —
    /// через ByeDPI идёт весь «десинк» телефона, по два дескриптора на
    /// соединение. <c>-T 3</c> — чтобы запасная группа (<c>t</c>) сработала
    /// на молчании за 3 с, а не за минуты таймаута ядра.
    /// </remarks>
    public const string Header = "-N -c 1024 -T 3";

    /// <summary>Первая группа строки по умолчанию: по умолчанию в ByeByeDPI, держится дольше всех.</summary>
    public const string FirstGroup = "-K t,h -o1 -r-5+se";

    /// <summary>
    /// Запасные группы: срабатывают, когда нет ServerHello или соединение
    /// оборвано (<c>-A t,s</c>), — у любой группы, и у переведённой из пресета.
    /// </summary>
    /// <remarks>
    /// Вторая — подделка с именем Google и случайными полями (май 2026,
    /// ByeDPIAndroid №415, №420), третья и четвёртая — ещё две ходовые строки
    /// 2026 года (<c>byedpi.md</c> §4.2). На телефоне не проверены.
    /// </remarks>
    public const string Fallbacks =
        "-A t,s -K t,h -d1 -n www.google.com -Q rand -f-1 "
        + "-A t,s -K t,h " + ByeDpiRecipes.MultiStage + " "
        + "-A t,s -K t,h -q1+s -s25+s -o5+s";

    /// <summary>Группа UDP строки по умолчанию: одна подделка перед первой датаграммой.</summary>
    public const string UdpGroup = "-A n -K u -a1";

    /// <summary>
    /// Строка по умолчанию без слушателя (<c>-i</c>, <c>-p</c>) — <c>byedpi.md</c> §4.3.
    /// На телефоне не проверена.
    /// </summary>
    public const string DefaultStrategy = Header + " " + FirstGroup + " " + Fallbacks + " " + UdpGroup;

    /// <summary>
    /// Сколько групп выдерживает ByeDPI — вместе с пустой, которую он дописывает сам.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Не 64, хотя маска групп у него <c>uint64_t</c>: бит группы ставится
    /// выражением <c>1 &lt;&lt; dp->id</c> (<c>main.c</c>, <c>add_group</c>,
    /// main@ba532298) — сдвиг 32-битного <c>int</c>. С 31-й группы это
    /// неопределённое поведение, а на деле (ARM64 и x86 берут сдвиг по модулю
    /// 32) группа 32+N получает бит группы N. <c>find_dp</c> помечает
    /// проверенные группы битами и группу с уже помеченным битом пропускает —
    /// значит, группы с 32-й недостижимы вовсе: их бит гасит неподошедшая
    /// группа из первой тридцатки. Прочитано в коде 27.10, не замерено.
    /// </para>
    /// <para>
    /// Тридцать одна — номера 0–30, где сдвиг определён. Правка ByeDPI
    /// (<c>(uint64_t)1 &lt;&lt; dp->id</c>) вернула бы 64.
    /// </para>
    /// </remarks>
    public const int GroupLimit = 31;

    /// <summary>Собирает запуск ByeDPI.</summary>
    /// <exception cref="InvalidOperationException">
    /// Пресет не помещается в <see cref="GroupLimit"/> групп и после уступок —
    /// ошибка сборки, а не молчание: лишние группы ByeDPI не заметил бы.
    /// </exception>
    public static ByeDpiPlan Build(ByeDpiRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(request), request.Port, "порт слушателя ByeDPI вне 1–65535");

        var warnings = new List<string>();
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var arguments = new List<string> { "-i", Listen, "-p", request.Port.ToString(CultureInfo.InvariantCulture) };

        var preset = Load(request.PresetPath, warnings);

        // Пресет читается при любой строке — ради имён pass (PassHosts). Что
        // в нём не переводится, интересно, только когда переводится он сам.
        var translation = new List<string>();
        var sections = preset is null ? null : Analyze(preset, request, translation);

        if (request.Strategy == ByeDpiStrategy.Preset)
            warnings.AddRange(translation);

        switch (request.Strategy)
        {
            case ByeDpiStrategy.Preset when sections is not null:
                arguments.AddRange(Groups(sections, request, files, warnings));
                break;

            case ByeDpiStrategy.Custom when !string.IsNullOrWhiteSpace(request.CustomArguments):
                arguments.AddRange(Custom(request.CustomArguments!, request, warnings));
                break;

            case ByeDpiStrategy.Custom:
                warnings.Add("своя строка ByeDPI пуста — взята строка по умолчанию");
                arguments.AddRange(ByeDpiRecipes.Split(DefaultStrategy));
                break;

            case ByeDpiStrategy.Preset:
                warnings.Add("пресет не прочитан — взята строка ByeDPI по умолчанию");
                arguments.AddRange(ByeDpiRecipes.Split(DefaultStrategy));
                break;

            default:
                arguments.AddRange(ByeDpiRecipes.Split(DefaultStrategy));
                break;
        }

        // UDP к ByeDPI не ведётся — UDP ASSOCIATE отклоняется. Ключ общий,
        // от места не зависит, поэтому в конце: строка по умолчанию остаётся
        // дословной.
        if (!request.Udp)
            arguments.Add("-U");

        var pass = sections is null ? [] : PassHosts(sections);

        return new ByeDpiPlan(arguments, files, warnings, pass);
    }

    /// <summary>
    /// Список для <c>-H</c>: без комментариев, <c>*.</c> и ведущей точки,
    /// строчными, IDN в punycode, по имени на строку (<c>byedpi.md</c> §3.5).
    /// </summary>
    /// <remarks>
    /// Расширяет смысл <c>--hostlist-domains</c>: у winws2 он сравнивает
    /// буквально, а у ByeDPI имя покрывает и поддомены. Почти всегда это и было
    /// нужно (virustotal.com, modrinth.com, awsapps.com), но разница есть.
    /// </remarks>
    public static string Normalize(IEnumerable<string> lines) =>
        ByeDpiLists.Join(ByeDpiLists.Names(lines, null));

    /// <summary>Список для <c>-j</c>: подсети «сеть/префикс», без комментариев и без <c>/0</c>.</summary>
    public static string NormalizeAddresses(IEnumerable<string> lines) =>
        ByeDpiLists.Join(ByeDpiLists.Addresses(lines, null));

    /// <summary>
    /// Метка имени в punycode по RFC 3492, без <c>xn--</c>, — свой кодировщик.
    /// </summary>
    /// <remarks>
    /// Запасной путь <see cref="Normalize"/>, когда <see cref="System.Globalization.IdnMapping"/>
    /// отказывает (Android без ICU — не проверено). Открыт затем, чтобы тест
    /// проверял именно его: на Windows до него дело не доходит.
    /// </remarks>
    public static string? Punycode(string label) =>
        ByeDpiLists.Punycode((label ?? string.Empty).ToLowerInvariant());

    private static ZapretPreset? Load(string path, List<string> warnings)
    {
        try
        {
            return new PresetReader().Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            warnings.Add($"пресет {Path.GetFileName(path)} не прочитан: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>Чем секция отбирает соединения в ByeDPI.</summary>
    private enum Proto { Tcp, Udp }

    /// <summary>Секция пресета с прочитанными списками и переводом рецепта.</summary>
    private sealed class Part
    {
        public required int Index { get; init; }
        public required string Name { get; init; }
        public required Proto Proto { get; init; }
        public required bool IsPass { get; init; }
        public List<string>? Hosts { get; init; }
        public List<string>? Addresses { get; init; }
        public required ByeDpiRecipe Recipe { get; init; }

        private NameZones? _zones;
        private CidrSet? _cidrs;

        public NameZones? Zones => Hosts is null ? null : _zones ??= new NameZones(Hosts);
        public CidrSet? Cidrs => Addresses is null ? null : _cidrs ??= CidrSet.From(Addresses);

        /// <summary>Чем группа отличается для склейки: протокол, вид отбора и приёмы.</summary>
        public string Key => $"{Proto}|{(Hosts is null ? "" : "H")}{(Addresses is null ? "" : "J")}|{Recipe.Key}";

        public string Title => string.IsNullOrWhiteSpace(Name) ? $"секция {Index}" : Name;

        /// <summary>
        /// Может ли одно соединение подойти обеим секциям.
        /// </summary>
        /// <remarks>
        /// Если у одной из секций нет списка имён, по именам она не ограничена;
        /// так же с адресами. Отсюда и осторожный ответ для пары «имена — адреса»:
        /// у первой нет ограничения по адресам, у второй — по именам, и
        /// пересечение не исключено.
        /// </remarks>
        public bool Meets(Part other)
        {
            bool names = Zones is null || other.Zones is null || Zones.Intersects(other.Zones);
            bool addresses = Cidrs is null || other.Cidrs is null || Cidrs.Intersects(other.Cidrs);
            return names && addresses;
        }
    }

    private sealed class Group
    {
        public required Proto Proto { get; init; }
        public required string Key { get; init; }
        public List<Part> Members { get; } = [];
    }

    /// <summary>Ключи winws2, общие для всех профилей (как <c>WinwsCommandLine.SplitGlobals</c>).</summary>
    private static readonly string[] GlobalKeys =
        ["--blob", "--lua-init", "--wf-", "--ipcache-", "--ctrack-", "--debug", "--writable"];

    /// <summary>
    /// Секции пресета с прочитанными списками; блок до первого <c>--new</c> — секция 0.
    /// </summary>
    /// <remarks>
    /// <c>PresetReader.Load</c> относит всё до первого <c>--new</c> к глобальным
    /// ключам, а у V12 Lite там стоит первая секция — git.zapret.moe с рецептом
    /// (<c>byedpi.md</c> §3.1). Без неё её имена ушли бы ниже, к чужому рецепту.
    /// </remarks>
    private static List<Part> Analyze(ZapretPreset preset, ByeDpiRequest request, List<string> warnings)
    {
        var blobs = new ByeDpiBlobs(preset.GlobalArguments, request.ZapretRoot);
        var sections = new List<ZapretSection>();

        var head = preset.GlobalArguments
            .Where(a => !GlobalKeys.Any(k => a.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (head.Any(a => a.StartsWith("--lua-desync=", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("--hostlist", StringComparison.OrdinalIgnoreCase)
                || a.StartsWith("--ipset", StringComparison.OrdinalIgnoreCase)))
        {
            sections.Add(Section(head));
        }

        sections.AddRange(preset.Sections);

        var parts = new List<Part>();
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        var dropped = new List<string>();
        int udpSkipped = 0;
        bool excludes = false;

        for (int index = 0; index < sections.Count; index++)
        {
            var section = sections[index];
            var title = string.IsNullOrWhiteSpace(section.Name) ? $"секция {index}" : section.Name;

            // --skip: профиль выключен автором — winws2 его не грузит.
            if (section.RawArguments.Any(a => a.Equals("--skip", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (section.RawArguments.Any(a => a.StartsWith("--ipset-exclude", StringComparison.OrdinalIgnoreCase)
                    || a.StartsWith("--hostlist-exclude", StringComparison.OrdinalIgnoreCase)))
            {
                excludes = true;
            }

            bool tcp = section.CarriesTcp;
            var proto = tcp ? Proto.Tcp : Proto.Udp;

            if (proto == Proto.Udp && !request.Udp)
            {
                udpSkipped++;
                continue;
            }

            List<string>? hosts = null;
            List<string>? addresses = null;

            if (section.HostListPaths.Count > 0 || section.InlineDomains.Count > 0)
            {
                var lines = new List<string>(section.InlineDomains);

                foreach (var path in section.HostListPaths)
                    lines.AddRange(ReadList(request.ZapretRoot, path, missing));

                hosts = ByeDpiLists.Names(lines, dropped);
            }

            if (section.IpSetPaths.Count > 0)
            {
                var lines = new List<string>();

                foreach (var path in section.IpSetPaths)
                    lines.AddRange(ReadList(request.ZapretRoot, path, missing));

                addresses = ByeDpiLists.Addresses(lines, dropped);
            }

            // Список есть, а записей нет (файла нет или он пуст): секция не забирает
            // ничего. Пустой файл ByeDPI отвергает вовсе — ftob() на нулевой длине
            // даёт «read/parse», и процесс выходит с кодом 255.
            if (hosts is { Count: 0 } || addresses is { Count: 0 })
            {
                warnings.Add($"«{title}»: список пуст или не найден — секция пропущена");
                continue;
            }

            if (proto == Proto.Udp)
            {
                // У UDP ByeDPI имён не смотрит (udp_hook берёт первую группу без
                // триггера, прошедшую check_l34), а классификатора (--filter-l7)
                // у него нет: такая группа забрала бы весь UDP.
                if (hosts is not null || (addresses is null && section.RawArguments.Any(a => a.StartsWith("--filter-l7=", StringComparison.OrdinalIgnoreCase))))
                {
                    warnings.Add($"«{title}»: UDP по именам или по виду трафика (--filter-l7) ByeDPI не отбирает — секция пропущена");
                    continue;
                }
            }

            var recipe = proto == Proto.Tcp
                ? ByeDpiRecipes.Tcp(section.DesyncRecipes, blobs, request.Md5Sig)
                : ByeDpiRecipes.Udp(section.DesyncRecipes, blobs);

            if (recipe.Kind == ByeDpiRecipeKind.Empty && recipe.Lost.Count > 0)
            {
                warnings.Add($"«{title}»: {string.Join(", ", recipe.Lost.Distinct())} без root недоступны — группа без приёмов,"
                    + " при отказе сработают запасные; надёжнее маршрут через VPN");
            }
            else if (recipe.Lost.Count > 0)
            {
                warnings.Add($"«{title}»: {string.Join(", ", recipe.Lost.Distinct())} без root недоступны — переведено остальное");
            }

            parts.Add(new Part
            {
                Index = index,
                Name = section.Name,
                Proto = proto,
                IsPass = section.IsPassThrough,
                Hosts = hosts,
                Addresses = addresses,
                Recipe = recipe,
            });
        }

        if (udpSkipped > 0)
            warnings.Add($"UDP в ByeDPI не ведётся — секций UDP пропущено: {udpSkipped}");

        if (excludes)
            warnings.Add("исключений внутри секции (--ipset-exclude, --hostlist-exclude) у ByeDPI нет — секции переведены без них");

        foreach (var path in missing)
            warnings.Add($"список не найден: {path}");

        if (dropped.Count > 0)
        {
            var sample = string.Join(", ", dropped.Distinct().Take(5));
            warnings.Add($"в списках отброшено записей, которых ByeDPI не примет: {dropped.Count} (например: {sample})");
        }

        if (parts.Any(p => p.Recipe.Kind == ByeDpiRecipeKind.Translated))
        {
            warnings.Add("рецепты переведены приближённо: подделка одна, TTL постоянный, без seqovl, tcp_ts, tcp_ack,"
                + " autottl и повторов — на телефоне не проверено");
        }

        if (!request.Md5Sig && sections.Any(s => s.DesyncRecipes.Any(r => r.Contains("tcp_md5", StringComparison.OrdinalIgnoreCase))))
            warnings.Add("tcp_md5 без -S: ядро без TCP_MD5SIG рвало бы соединения");

        return parts;
    }

    private static ZapretSection Section(IReadOnlyList<string> arguments)
    {
        string name = string.Empty;
        var hosts = new List<string>();
        var inline = new List<string>();
        var ipsets = new List<string>();
        var recipes = new List<string>();

        foreach (var argument in arguments)
        {
            int eq = argument.IndexOf('=');
            var key = eq < 0 ? argument.Trim() : argument[..eq].Trim();
            var value = eq < 0 ? null : argument[(eq + 1)..].Trim();

            switch (key)
            {
                case "--name":
                    name = value ?? string.Empty;
                    break;
                case "--hostlist" when value is not null:
                    hosts.Add(value);
                    break;
                case "--hostlist-domains" when value is not null:
                    inline.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--ipset" when value is not null:
                    ipsets.Add(value);
                    break;
                case "--lua-desync" when value is not null:
                    recipes.Add(value);
                    break;
            }
        }

        return new ZapretSection
        {
            Name = name,
            HostListPaths = hosts,
            InlineDomains = inline,
            IpSetPaths = ipsets,
            DesyncRecipes = recipes,
            IsPassThrough = recipes.Count > 0 && recipes.All(r => r.Equals("pass", StringComparison.OrdinalIgnoreCase)),
            RawArguments = arguments.ToList(),
        };
    }

    private static IEnumerable<string> ReadList(string zapretRoot, string path, ISet<string> missing)
    {
        var full = Path.IsPathRooted(path)
            ? path
            : Path.Combine(zapretRoot, path.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar));

        try
        {
            return File.ReadAllLines(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            missing.Add(path);
            return [];
        }
    }

    /// <summary>
    /// Группы пресета: склейка, файлы списков, ключи; в конце — запасные.
    /// </summary>
    private static List<string> Groups(
        List<Part> parts,
        ByeDpiRequest request,
        IDictionary<string, string> files,
        List<string> warnings)
    {
        var tcp = Merge(parts.Where(p => p.Proto == Proto.Tcp).ToList());
        var udp = Merge(parts.Where(p => p.Proto == Proto.Udp).ToList());

        int fallbacks = ByeDpiRecipes.Split(Fallbacks).Count(t => t == "-A");
        int tail = fallbacks + (request.Udp ? 1 : 0) + 1;   // + пустая группа, которую ByeDPI дописывает сам

        // Не помещается: сперва уступает UDP из пресета — QUIC к именам десинка
        // sing-box и так отклоняет, браузер уходит на TCP.
        if (Math.Max(tcp.Count, 1) + udp.Count + tail > GroupLimit && udp.Count > 0)
        {
            warnings.Add($"групп больше {GroupLimit}: группы UDP из пресета ({udp.Count}) не включены");
            udp.Clear();
        }

        int total = Math.Max(tcp.Count, 1) + udp.Count + tail;

        if (total > GroupLimit)
        {
            throw new InvalidOperationException(
                $"пресет даёт {total} групп ByeDPI вместе с запасными, а ByeDPI выдерживает {GroupLimit} — выберите пресет легче или строку по умолчанию");
        }

        var arguments = new List<string>(ByeDpiRecipes.Split(Header));
        int number = 0;

        foreach (var group in tcp.Concat(udp))
        {
            if (number > 0)
                arguments.AddRange(["-A", "n"]);

            arguments.AddRange(group.Proto == Proto.Tcp ? ["-K", "t,h"] : ["-K", "u"]);

            var hosts = group.Members[0].Hosts is not null;
            var addresses = group.Members[0].Addresses is not null;

            if (hosts)
            {
                var name = $"g{number:D2}-hosts.txt";
                files[name] = ByeDpiLists.Join(Distinct(group.Members.SelectMany(m => m.Hosts!)));
                arguments.AddRange(["-H", InWork(request.WorkDirectory, name)]);
            }

            if (addresses)
            {
                var name = $"g{number:D2}-ipset.txt";
                files[name] = ByeDpiLists.Join(Distinct(group.Members.SelectMany(m => m.Addresses!)));
                arguments.AddRange(["-j", InWork(request.WorkDirectory, name)]);
            }

            arguments.AddRange(group.Members[0].Recipe.Tokens);
            number++;
        }

        // Ни одной группы TCP: первая группа строки всё равно должна быть
        // ограничена, иначе она — без -K — забрала бы и весь UDP.
        if (number == 0)
        {
            arguments.AddRange(["-K", "t,h"]);
            number++;
        }

        arguments.AddRange(ByeDpiRecipes.Split(Fallbacks));

        if (request.Udp)
            arguments.AddRange(ByeDpiRecipes.Split(UdpGroup));

        return arguments;
    }

    private static List<string> Distinct(IEnumerable<string> entries) =>
        entries.Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Путь к списку группы — через «/», как его увидит ByeDPI на телефоне.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.Combine(string, string)"/> на Windows (тесты, просмотр
    /// интерфейса) поставил бы «\» — строка вышла бы разной на двух машинах.
    /// Windows прямой слэш понимает тоже.
    /// </remarks>
    private static string InWork(string directory, string name) =>
        directory.TrimEnd('/', '\\') + "/" + name;

    /// <summary>
    /// Жадная склейка: секция дописывается к последней группе с тем же ключом,
    /// если ни одна группа после неё не забирает общих с секцией соединений.
    /// </summary>
    /// <remarks>
    /// Группа стоит в строке на месте своей первой секции. Секция, ушедшая
    /// в группу, обгоняет все группы, начатые после этой. Если с какой-то из них
    /// у неё есть общее имя (или подсеть, или сравнить нельзя) — имя сменило бы
    /// рецепт, и склейки нет. Группы с тем же рецептом не в счёт: кто из них
    /// заберёт имя, всё равно. Проверять группы раньше последней подходящей
    /// незачем — после них стоит ещё больше групп.
    /// </remarks>
    private static List<Group> Merge(List<Part> parts)
    {
        var groups = new List<Group>();

        foreach (var part in parts)
        {
            var key = part.Key;
            int candidate = groups.FindLastIndex(g => g.Key == key);

            bool joins = candidate >= 0
                && (part.Hosts is not null || part.Addresses is not null)
                && !groups.Skip(candidate + 1)
                    .Where(g => g.Key != key)
                    .SelectMany(g => g.Members)
                    .Any(part.Meets);

            if (joins)
            {
                groups[candidate].Members.Add(part);
                continue;
            }

            var group = new Group { Proto = part.Proto, Key = key };
            group.Members.Add(part);
            groups.Add(group);
        }

        return groups;
    }

    /// <summary>
    /// Имена секций <c>pass</c>, которые и на деле проходят нетронутыми.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Запасные группы ByeDPI (<c>-A t,s</c>) срабатывают на любом соединении,
    /// и группа <c>pass</c> от них не спасает: при отказе Riot или GitHub получили
    /// бы подделки. Поэтому эти имена sing-box ведёт напрямую, мимо ByeDPI —
    /// «напрямую» и «десинк + pass» в winws2 одинаковы (замер A/B/A 29.09).
    /// </para>
    /// <para>
    /// Не все имена секции: имя, которое забирает секция с рецептом выше,
    /// не проходит нетронутым и на Windows. У V12 Lite «Выбрано вручную: github»
    /// стоит над секцией GitHub с <c>pass</c> — github.com получает рецепт, и
    /// прямой маршрут отнял бы его. Выбрасывается и имя, под которым лежит
    /// имя секции с рецептом выше (у той <c>api.example.com</c>, у этой
    /// <c>example.com</c>): правило «напрямую» на зону забрало бы и его.
    /// Секции по адресам выше не учтены — адреса имени заранее не известны.
    /// </para>
    /// </remarks>
    private static List<string> PassHosts(List<Part> parts)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var above = new List<NameZones>();

        foreach (var part in parts.Where(p => p.Proto == Proto.Tcp && p.Zones is not null))
        {
            if (!part.IsPass)
            {
                above.Add(part.Zones!);
                continue;
            }

            foreach (var name in part.Hosts!)
            {
                if (above.Any(z => z.Covers(name) || z.HasUnder(name)))
                    continue;

                if (seen.Add(name))
                    result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Своя строка человека: слушатель и <c>-N</c> — наши, остальное — его.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Убираются ключи, которые сломали бы запуск под VPN: слушатель
    /// (<c>-i</c>, <c>-p</c> — только наш, на 127.0.0.1 и с паролем), демон
    /// и pid-файл, прозрачный режим (нужен root), справка и версия (процесс
    /// вышел бы сразу). <c>-N</c> ставится всегда: без него имя ByeDPI разрешал
    /// бы системным DNS мимо VPN. <c>-S</c> без проверенного ядра снимается.
    /// </para>
    /// <para>
    /// Разбирается так же, как getopt ByeDPI: у ключа с аргументом он — остаток
    /// слова или следующее слово (<c>-Kt,h</c> и <c>-K t,h</c> одно и то же),
    /// ключи без аргумента склеиваются (<c>-NU</c>).
    /// </para>
    /// </remarks>
    private static List<string> Custom(string line, ByeDpiRequest request, List<string> warnings)
    {
        var words = Tokens(line);
        var result = new List<string>(["-N"]);
        var removed = new SortedSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < words.Count; i++)
        {
            var word = words[i];

            if (word.StartsWith("--", StringComparison.Ordinal) && word.Length > 2)
            {
                int eq = word.IndexOf('=');
                var longName = eq < 0 ? word[2..] : word[2..eq];

                if (!LongOptions.TryGetValue(longName, out var opt))
                {
                    warnings.Add($"своя строка ByeDPI: незнакомый ключ {word}");
                    result.Add(word);
                    continue;
                }

                string? value = null;

                if (TakesValue(opt))
                    value = eq >= 0 ? word[(eq + 1)..] : (i + 1 < words.Count ? words[++i] : null);

                Keep(opt, value);
                continue;
            }

            if (word.Length > 1 && word[0] == '-' && !char.IsDigit(word[1]))
            {
                for (int c = 1; c < word.Length; c++)
                {
                    char opt = word[c];

                    if (!TakesValue(opt) && !NoValue.Contains(opt))
                    {
                        warnings.Add($"своя строка ByeDPI: незнакомый ключ -{opt}");
                        result.Add("-" + word[c..]);
                        break;
                    }

                    if (!TakesValue(opt))
                    {
                        Keep(opt, null);
                        continue;
                    }

                    var value = c + 1 < word.Length ? word[(c + 1)..] : (i + 1 < words.Count ? words[++i] : null);
                    Keep(opt, value);
                    break;
                }

                continue;
            }

            // Слово без ключа — getopt ByeDPI его пропустил бы; передаём как есть.
            result.Add(word);
        }

        if (removed.Count > 0)
            warnings.Add($"своя строка ByeDPI: сняты ключи {string.Join(", ", removed)}");

        return result;

        void Keep(char opt, string? value)
        {
            switch (opt)
            {
                case 'i' or 'p' or 'D' or 'w' or 'E' or 'h' or 'v':
                    removed.Add("-" + opt);
                    return;

                case 'N':
                    return;   // уже первым

                case 'S' when !request.Md5Sig:
                    removed.Add("-S (ядро без проверенного TCP_MD5SIG)");
                    return;
            }

            result.Add("-" + opt);

            if (value is not null)
                result.Add(value);
        }
    }

    /// <summary>Ключи ByeDPI без аргумента (<c>main.c</c>, <c>options[]</c>, main@ba532298).</summary>
    private static readonly HashSet<char> NoValue = ['N', 'X', 'U', 'h', 'v', 'E', 'D', 'F', 'S', 'Z', 'Y'];

    /// <summary>Ключи ByeDPI с аргументом.</summary>
    private static readonly HashSet<char> WithValue =
        ['w', 'i', 'p', 'I', 'b', 'c', 'x', 'A', 'L', 'u', 'T', 'B', 'y', 'K', 'H', 'V', 'R', 's', 'd', 'o', 'q',
         'f', 'n', 't', 'l', 'O', 'Q', 'e', 'M', 'r', 'm', 'a', 'g', 'W', 'P', 'j', 'C', '#', '/'];

    private static bool TakesValue(char opt) => WithValue.Contains(opt);

    private static readonly Dictionary<string, char> LongOptions = new(StringComparer.Ordinal)
    {
        ["daemon"] = 'D', ["pidfile"] = 'w', ["no-domain"] = 'N', ["no-ipv6"] = 'X', ["no-udp"] = 'U',
        ["help"] = 'h', ["version"] = 'v', ["ip"] = 'i', ["port"] = 'p', ["transparent"] = 'E',
        ["conn-ip"] = 'I', ["buf-size"] = 'b', ["max-conn"] = 'c', ["debug"] = 'x', ["tfo"] = 'F',
        ["auto"] = 'A', ["auto-mode"] = 'L', ["cache-ttl"] = 'u', ["timeout"] = 'T', ["copy"] = 'B',
        ["cache-file"] = 'y', ["proto"] = 'K', ["hosts"] = 'H', ["pf"] = 'V', ["round"] = 'R',
        ["split"] = 's', ["disorder"] = 'd', ["oob"] = 'o', ["disoob"] = 'q', ["fake"] = 'f',
        ["md5sig"] = 'S', ["fake-sni"] = 'n', ["ttl"] = 't', ["fake-data"] = 'l', ["fake-offset"] = 'O',
        ["fake-tls-mod"] = 'Q', ["oob-data"] = 'e', ["mod-http"] = 'M', ["tlsrec"] = 'r', ["tlsminor"] = 'm',
        ["udp-fake"] = 'a', ["def-ttl"] = 'g', ["wait-send"] = 'Z', ["await-int"] = 'W', ["drop-sack"] = 'Y',
        ["protect-path"] = 'P', ["ipset"] = 'j', ["connect-to"] = 'C', ["comment"] = '#', ["cache-merge"] = '/',
    };

    /// <summary>Слова строки: пробелы делят, кавычки (<c>"…"</c>, <c>'…'</c>) держат вместе.</summary>
    /// <remarks>
    /// Строки из обсуждений пишутся для оболочки: <c>-H:"ggpht.com googlevideo.com"</c> —
    /// одно слово <c>-H:ggpht.com googlevideo.com</c>. Кавычки снимаются, как сняла бы их
    /// оболочка.
    /// </remarks>
    public static IReadOnlyList<string> Tokens(string line)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        bool inWord = false;
        char quote = '\0';

        foreach (char c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    current.Append(c);

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (inWord)
                {
                    words.Add(current.ToString());
                    current.Clear();
                    inWord = false;
                }

                continue;
            }

            current.Append(c);
            inWord = true;
        }

        if (inWord)
            words.Add(current.ToString());

        return words;
    }
}
