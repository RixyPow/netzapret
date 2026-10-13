using System.Text;

namespace NetZapret.Core.Rules;

/// <summary>Программа в «Маршрутах»: папка из двух своих списков.</summary>
/// <param name="Program">Имя файла программы: «Diablo IV.exe».</param>
/// <param name="AutoFill">Дописывать ли в списки то, что программа открывает (<see cref="Connections.ProgramRouteLearner"/>).</param>
public sealed record ProgramRoute(string Program, bool AutoFill = true);

/// <summary>
/// Маршрут программы — не правило по процессу, а два своих списка:
/// домены и адреса, которые программа открывает.
/// </summary>
/// <remarks>
/// <para>
/// Правило по процессу sing-box узнаёт только внутри TUN, а в «Гибриде»
/// TUN забирает лишь адреса из правил: серверы игры по голым адресам шли
/// мимо, и «через VPN» для программы не значило ничего (04.10, снято —
/// <see cref="ProgramRulesOff"/>). Списки имён и адресов действуют при
/// любом перехвате и у обоих движков — так же, как свой список сайта.
/// </para>
/// <para>
/// Владелец 13.10: папка программы на два маршрута — домены и адреса,
/// изначально пустые и выключенные, пополняются по «Наблюдению», у каждой
/// программы — выключатель автопополнения. Маршрут у каждого списка свой:
/// домены можно отправить в VPN, а адреса — оставить напрямую.
/// </para>
/// <para>
/// Списки лежат среди своих (<see cref="OwnLists.Folder"/>), в подпапке
/// <c>programs/</c>: они личные, в git и в поставку не идут, а движок
/// разбирает в них и имена, и адреса (<see cref="AddressInput.Split"/>).
/// Какие программы есть и пополняются ли — в настройках
/// (<see cref="AppSettings.ProgramRoutes"/>), имя файла списка из имени
/// программы не восстановить.
/// </para>
/// </remarks>
public static class ProgramRoutes
{
    /// <summary>Папка списков программ — в том виде, в каком путь пишется в правило.</summary>
    public const string Folder = OwnLists.Folder + "programs/";

    /// <summary>Имя программы так, как оно хранится: имя файла без пути.</summary>
    /// <returns><c>null</c> — не программа.</returns>
    public static string? Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var name = Path.GetFileName(text.Trim().Trim('"'));

        return name.Length > 4
            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
                ? name
                : null;
    }

    /// <summary>Основа имени файла: «Diablo IV.exe» → «diablo-iv.exe».</summary>
    internal static string Slug(string program) =>
        new(program.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-')
            .ToArray());

    /// <summary>Список доменов программы — значение правила.</summary>
    public static string DomainsPath(string program) => Folder + Slug(program) + ".domains.txt";

    /// <summary>Список адресов программы — значение правила.</summary>
    public static string AddressesPath(string program) => Folder + Slug(program) + ".addresses.txt";

    /// <summary>Список ли это программы, а не свой список сайта.</summary>
    public static bool IsProgramList(string? value) =>
        value is not null
        && value.Replace('\\', '/').StartsWith(Folder, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Заводит программу: два пустых списка и два выключенных маршрута на них.
    /// </summary>
    /// <returns><c>false</c> — такая программа уже есть.</returns>
    /// <remarks>
    /// Маршруты встают первыми в файле правил: у программы они узкие и
    /// выбраны человеком нарочно, а движок берёт первое совпавшее. Выключены —
    /// пока человек сам не выберет путь: пополненный наблюдением список
    /// без спроса не должен уводить имена программы куда-либо.
    /// </remarks>
    public static bool Add(string program, string settingsPath, string rulesPath, string? root = null)
    {
        var settings = AppSettings.Load(settingsPath);

        if (settings.ProgramRoutes.Any(p => Same(p.Program, program)))
            return false;

        WriteHeader(DomainsPath(program), program, "домены", root);
        WriteHeader(AddressesPath(program), program, "адреса", root);

        var rules = UserRulesFile.Load(rulesPath);
        rules.SetOff(MatchKind.HostList, DomainsPath(program));
        rules.SetOff(MatchKind.HostList, AddressesPath(program));

        // SetOff пишет в конец (или на место прежней записи) — переносим наверх:
        // домены, затем адреса. Ищем по значению, а не по месту в конце.
        rules.Move(IndexOf(rules, DomainsPath(program)), 0);
        rules.Move(IndexOf(rules, AddressesPath(program)), 1);
        rules.Save();

        (settings with { ProgramRoutes = [.. settings.ProgramRoutes, new ProgramRoute(program)] }).Save(settingsPath);

        return true;
    }

    /// <summary>Убирает программу целиком: маршруты, списки и запись в настройках.</summary>
    public static void Remove(string program, string settingsPath, string rulesPath, string? root = null)
    {
        var rules = UserRulesFile.Load(rulesPath);
        rules.Remove(MatchKind.HostList, DomainsPath(program));
        rules.Remove(MatchKind.HostList, AddressesPath(program));
        rules.Save();

        OwnLists.Delete(DomainsPath(program), root);
        OwnLists.Delete(AddressesPath(program), root);

        var settings = AppSettings.Load(settingsPath);
        (settings with { ProgramRoutes = settings.ProgramRoutes.Where(p => !Same(p.Program, program)).ToList() })
            .Save(settingsPath);
    }

    /// <summary>Включает или выключает автопополнение программы.</summary>
    public static void SetAutoFill(string program, bool on, string settingsPath)
    {
        var settings = AppSettings.Load(settingsPath);

        (settings with
        {
            ProgramRoutes = settings.ProgramRoutes
                .Select(p => Same(p.Program, program) ? p with { AutoFill = on } : p)
                .ToList(),
        }).Save(settingsPath);
    }

    /// <summary>Строки списка — домены или адреса, без комментариев.</summary>
    public static IReadOnlyList<string> Entries(string listPath, string? root = null) =>
        OwnLists.Names(listPath, root);

    /// <summary>Дописывает строку в список программы.</summary>
    internal static void Append(string listPath, string value, string? root = null)
    {
        var full = Full(listPath, root);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var prefix = File.Exists(full) && new FileInfo(full).Length > 0 && !File.ReadAllText(full).EndsWith('\n')
            ? "\r\n"
            : string.Empty;

        File.AppendAllText(full, prefix + value + "\r\n", new UTF8Encoding(false));
    }

    private static int IndexOf(UserRulesFile rules, string value)
    {
        for (int i = 0; i < rules.Entries.Count; i++)
        {
            if (rules.Entries[i].Match == MatchKind.HostList && rules.Entries[i].Matches(value))
                return i;
        }

        return -1;
    }

    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void WriteHeader(string listPath, string program, string what, string? root)
    {
        var full = Full(listPath, root);

        // Оставшийся от прежнего раза список не переписываем: в нём может быть
        // дописанное руками.
        if (File.Exists(full))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var body = new StringBuilder()
            .Append($"# Маршрут программы {program}: {what}.\r\n")
            .Append("#\r\n")
            .Append("# Пополняется сам, пока у программы включено автопополнение и идёт\r\n")
            .Append("# «Наблюдение». Дописывать можно и руками, по одному в строке.\r\n")
            .Append("# Правка действует со следующего запуска движков.\r\n");

        File.WriteAllText(full, body.ToString(), new UTF8Encoding(false));
    }

    private static string Full(string value, string? root) =>
        Path.GetFullPath(Path.Combine(root ?? Directory.GetCurrentDirectory(), value));
}
