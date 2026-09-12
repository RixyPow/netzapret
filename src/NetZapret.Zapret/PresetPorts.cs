namespace NetZapret.Zapret;

/// <summary>
/// На каких портах пресет ловит эти имена.
/// </summary>
/// <remarks>
/// <para>
/// Свой профиль встаёт перед пресетовскими и забирает имя себе. Значит порты
/// у него обязаны быть те же, что у секции, которую он подменяет: стоя уже,
/// он заберёт часть трафика, а остальное пройдёт вообще без обработки —
/// и снаружи это выглядит как «рецепт помогает через раз».
/// </para>
/// <para>
/// У Discord за этим стоит не теория. Секции <c>discord.com</c>
/// и <c>discord.media</c> объявлены на <c>80,443,1080,2053,2083,2087,2096,8443</c>,
/// и всё, кроме первых двух, — запасные порты HTTPS у Cloudflare. Клиент
/// переходит на них сам, когда сеть ведёт себя плохо, то есть ровно в наших
/// условиях. Профиль на <c>80,443</c> такую сессию не видел вовсе.
/// </para>
/// </remarks>
public static class PresetPorts
{
    /// <summary>Порты по умолчанию, когда в пресете ничего не нашлось.</summary>
    public const string Default = "80,443";

    /// <summary>
    /// Объединение <c>--filter-tcp</c> у секций, чьи списки покрывают эти имена.
    /// </summary>
    /// <remarks>
    /// Объединение, а не первая подходящая секция: имя может встречаться
    /// в нескольких, и брать порты у той, что попалась раньше, значило бы
    /// решать по случайности расположения. Лишний порт в фильтре безвреден —
    /// на нём просто не окажется трафика.
    /// </remarks>
    public static string ForDomains(
        ZapretPreset preset,
        string? zapretRoot,
        IReadOnlyList<string> domains)
    {
        if (domains.Count == 0)
            return Default;

        var ports = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in SectionsFor(preset, zapretRoot, domains))
        {
            if (section.TcpPorts is not { } value)
                continue;

            foreach (var port in value.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(port))
                    ports.Add(port);
            }
        }

        return ports.Count == 0 ? Default : string.Join(',', ports);
    }

    /// <summary>
    /// Секции пресета, покрывающие эти имена, в порядке файла.
    /// </summary>
    /// <remarks>
    /// Порядок сохранён намеренно: winws2 отдаёт пакет первому профилю, чей
    /// фильтр совпал, и дальше не смотрит. Значит первая в этом перечне —
    /// и есть та, что решит судьбу имени, а остальные до него не дойдут.
    /// </remarks>
    public static IEnumerable<ZapretSection> SectionsFor(
        ZapretPreset preset,
        string? zapretRoot,
        IReadOnlyList<string> domains)
    {
        if (domains.Count == 0)
            yield break;

        foreach (var section in preset.Sections)
        {
            if (Covers(section, zapretRoot, domains))
                yield return section;
        }
    }

    /// <summary>Есть ли в секции хоть одно из этих имён.</summary>
    private static bool Covers(
        ZapretSection section,
        string? zapretRoot,
        IReadOnlyList<string> domains)
    {
        foreach (var entry in Entries(section, zapretRoot))
        {
            foreach (var domain in domains)
            {
                if (SameZone(entry, domain))
                    return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> Entries(ZapretSection section, string? zapretRoot)
    {
        foreach (var inline in section.InlineDomains)
            yield return inline;

        foreach (var path in section.HostListPaths)
        {
            IReadOnlyList<string> names;

            try
            {
                names = HostListReader.Read(path, zapretRoot, out _);
            }
            catch (Exception)
            {
                // Списка может не быть: пресет пишут под полную установку
                // Zapret, а у нас встроенная копия. Отсутствие файла —
                // не повод остаться вовсе без портов.
                continue;
            }

            foreach (var name in names)
                yield return name;
        }
    }

    /// <summary>
    /// Одна ли это зона.
    /// </summary>
    /// <remarks>
    /// В обе стороны: запись списка — зона, и <c>discord.media</c> покрывает
    /// голосовые серверы, а <c>discordapp.net</c> — оба прокси картинок.
    /// Но и наше имя бывает шире записи, когда правило поставлено на сервис
    /// целиком, а секция знает лишь один его поддомен.
    /// </remarks>
    private static bool SameZone(string entry, string domain)
    {
        var a = entry.Trim().Trim('*', '.');
        var b = domain.Trim().Trim('*', '.');

        if (a.Length == 0 || b.Length == 0)
            return false;

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            || a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase)
            || b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase);
    }
}
