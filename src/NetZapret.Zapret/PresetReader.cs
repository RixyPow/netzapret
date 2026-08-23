using System.Net;

namespace NetZapret.Zapret;

/// <summary>
/// Читает пресеты winws2 и списки, на которые они ссылаются.
/// </summary>
public sealed class PresetReader
{
    private const string SectionSeparator = "--new";

    /// <summary>
    /// Перечисляет пресеты в каталоге, разбирая каждый целиком.
    /// </summary>
    /// <remarks>
    /// Файлы небольшие — десятки килобайт, — поэтому отдельный «быстрый» режим
    /// разбора только шапки усложнил бы код без выигрыша.
    /// </remarks>
    public IReadOnlyList<ZapretPreset> List(string presetDirectory)
    {
        if (!Directory.Exists(presetDirectory))
            return Array.Empty<ZapretPreset>();

        var presets = new List<ZapretPreset>();

        foreach (var path in Directory.EnumerateFiles(presetDirectory, "*.txt").OrderBy(p => p))
        {
            try
            {
                presets.Add(Load(path));
            }
            catch (IOException)
            {
                // Нечитаемый файл не должен ронять перечисление остальных.
            }
        }

        return presets;
    }

    public ZapretPreset Load(string presetPath)
    {
        var lines = File.ReadAllLines(presetPath);

        string? description = null;
        string? builtinVersion = null;
        string? iconColor = null;
        string? headerName = null;

        var globals = new List<string>();
        var sections = new List<ZapretSection>();
        var current = new List<string>();
        bool seenSeparator = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length == 0)
                continue;

            if (line.StartsWith('#'))
            {
                ReadMetadata(line, ref headerName, ref description, ref builtinVersion, ref iconColor);
                continue;
            }

            if (line.Equals(SectionSeparator, StringComparison.OrdinalIgnoreCase))
            {
                if (!seenSeparator)
                {
                    // Всё до первого --new — глобальные ключи.
                    globals.AddRange(current);
                    seenSeparator = true;
                }
                else if (current.Count > 0)
                {
                    sections.Add(BuildSection(current));
                }

                current = new List<string>();
                continue;
            }

            current.Add(line);
        }

        // Хвост после последнего --new.
        if (current.Count > 0)
        {
            if (seenSeparator)
                sections.Add(BuildSection(current));
            else
                globals.AddRange(current);
        }

        return new ZapretPreset
        {
            Name = headerName ?? Path.GetFileNameWithoutExtension(presetPath),
            FilePath = presetPath,
            Description = description,
            BuiltinVersion = builtinVersion,
            IconColor = iconColor,
            GlobalArguments = globals,
            Sections = sections,
        };
    }

    private static void ReadMetadata(
        string line,
        ref string? name,
        ref string? description,
        ref string? builtinVersion,
        ref string? iconColor)
    {
        var body = line.TrimStart('#').Trim();
        int colon = body.IndexOf(':');

        if (colon <= 0)
            return;

        var key = body[..colon].Trim();
        var value = body[(colon + 1)..].Trim();

        if (value.Length == 0)
            return;

        // Только известные ключи: обычные комментарии внутри пресета тоже
        // содержат двоеточия и метаданными не являются.
        switch (key.ToLowerInvariant())
        {
            case "preset":
                name ??= value;
                break;
            case "description":
                description ??= value;
                break;
            case "builtinversion":
                builtinVersion ??= value;
                break;
            case "iconcolor":
                iconColor ??= value;
                break;
        }
    }

    private static ZapretSection BuildSection(IReadOnlyList<string> arguments)
    {
        string name = string.Empty;
        var hostLists = new List<string>();
        var inlineDomains = new List<string>();
        var ipSets = new List<string>();
        var recipes = new List<string>();

        foreach (var argument in arguments)
        {
            var (key, value) = SplitArgument(argument);

            switch (key)
            {
                case "--name":
                    name = value ?? string.Empty;
                    break;

                case "--hostlist":
                    if (value is not null)
                        hostLists.Add(value);
                    break;

                case "--hostlist-domains":
                    if (value is not null)
                    {
                        inlineDomains.AddRange(value.Split(
                            ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    }

                    break;

                case "--ipset":
                    if (value is not null)
                        ipSets.Add(value);
                    break;

                case "--lua-desync":
                    if (value is not null)
                        recipes.Add(value);
                    break;
            }
        }

        bool isPassThrough = recipes.Count > 0
            && recipes.All(r => r.Equals("pass", StringComparison.OrdinalIgnoreCase));

        return new ZapretSection
        {
            Name = name,
            HostListPaths = hostLists,
            InlineDomains = inlineDomains,
            IpSetPaths = ipSets,
            DesyncRecipes = recipes,
            IsPassThrough = isPassThrough,
            RawArguments = arguments.ToList(),
        };
    }

    private static (string Key, string? Value) SplitArgument(string argument)
    {
        int equals = argument.IndexOf('=');

        return equals < 0
            ? (argument.Trim(), null)
            : (argument[..equals].Trim(), argument[(equals + 1)..].Trim());
    }

    /// <summary>
    /// Собирает домены, которые пресет покрывает десинком.
    /// </summary>
    /// <param name="preset">Разобранный пресет.</param>
    /// <param name="zapretRoot">Корень установки Zapret — относительно него заданы пути списков.</param>
    /// <param name="includePassThrough">
    /// Включать ли секции с <c>--lua-desync=pass</c>. По умолчанию нет: такие
    /// сервисы работают сами, десинком не покрыты и в исключениях не нуждаются.
    /// </param>
    public IReadOnlySet<string> CollectCoveredDomains(
        ZapretPreset preset,
        string zapretRoot,
        bool includePassThrough = false)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = includePassThrough ? preset.Sections : preset.ActiveSections;

        foreach (var section in sections)
        {
            foreach (var domain in section.InlineDomains)
                domains.Add(domain.Trim().TrimEnd('.'));

            foreach (var relative in section.HostListPaths)
            {
                foreach (var line in ReadListFile(zapretRoot, relative))
                    domains.Add(line.TrimEnd('.'));
            }
        }

        return domains;
    }

    /// <summary>
    /// Собирает адреса и подсети, которые пресет покрывает десинком.
    /// </summary>
    /// <remarks>
    /// Это прямой источник для <c>route_exclude_address</c> у TUN-инбаунда
    /// sing-box: такой трафик обязан идти мимо туннеля, иначе WinDivert увидит
    /// переоткрытый сокет вместо исходного потока и часть рецептов перестанет
    /// работать. См. docs/coexistence.md.
    /// </remarks>
    public IReadOnlyList<string> CollectCoveredAddresses(
        ZapretPreset preset,
        string zapretRoot,
        bool includePassThrough = false)
    {
        var addresses = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sections = includePassThrough ? preset.Sections : preset.ActiveSections;

        foreach (var section in sections)
        {
            foreach (var relative in section.IpSetPaths)
            {
                foreach (var line in ReadListFile(zapretRoot, relative))
                {
                    var cidr = NormalizeCidr(line);
                    if (cidr is not null && seen.Add(cidr))
                        addresses.Add(cidr);
                }
            }
        }

        return addresses;
    }

    /// <summary>
    /// Читает файл списка: по записи на строку, комментарии с <c>#</c> и пустые
    /// строки пропускаются.
    /// </summary>
    private static IEnumerable<string> ReadListFile(string zapretRoot, string relativePath)
    {
        var path = Path.Combine(zapretRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(path))
            yield break;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // В строке может быть комментарий после значения.
            int comment = line.IndexOf('#');
            if (comment > 0)
                line = line[..comment].Trim();

            if (line.Length > 0)
                yield return line;
        }
    }

    /// <summary>
    /// Приводит запись ipset к виду CIDR. Голый адрес получает полную маску:
    /// sing-box принимает в <c>route_exclude_address</c> только префиксы.
    /// </summary>
    private static string? NormalizeCidr(string value)
    {
        var text = value.Trim();

        if (text.Contains('/'))
        {
            var slash = text.IndexOf('/');
            return IPAddress.TryParse(text[..slash], out _) && int.TryParse(text[(slash + 1)..], out _)
                ? text
                : null;
        }

        if (!IPAddress.TryParse(text, out var address))
            return null;

        int bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return $"{address}/{bits}";
    }
}
