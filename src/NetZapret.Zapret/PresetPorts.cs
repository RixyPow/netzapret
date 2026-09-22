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

        foreach (var section in PresetZones.Build(preset, zapretRoot).AllFor(domains))
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
}
