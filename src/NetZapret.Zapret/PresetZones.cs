namespace NetZapret.Zapret;

/// <summary>
/// Пресет с прочитанными списками — чтобы отвечать про имена быстро.
/// </summary>
/// <remarks>
/// <para>
/// Заведён после того, как раздел маршрутов стал заметно тормозить. Спрашивать
/// «какая секция заберёт это имя» приходится на каждую часть каждого сервиса —
/// их около восьмидесяти, — а каждый такой вопрос перечитывал списки всех
/// секций пресета заново. В <c>russia-blacklist.txt</c> сто семнадцать тысяч
/// имён, и он оказывался прочитан восемьдесят раз подряд.
/// </para>
/// <para>
/// Здесь списки читаются однажды, а поиск идёт по множеству: у имени берутся
/// его зоны от самой длинной к короткой, и каждая проверяется на вхождение.
/// Запись списка покрывает поддомены, поэтому «есть ли зона имени в списке»
/// и есть искомое — но теперь это три проверки вместо ста семнадцати тысяч
/// сравнений.
/// </para>
/// </remarks>
public sealed class PresetZones
{
    /// <summary>
    /// Докуда искать обратное совпадение.
    /// </summary>
    /// <remarks>
    /// Обратное — это когда запись секции лежит под нашим именем: у секции
    /// <c>updates.discord.com</c>, а у нас правило на <c>discord.com</c>
    /// целиком. Такое бывает и учитывать его надо, но перебором, а быстрого
    /// способа нет.
    ///
    /// Поэтому предел. Большие списки — это перечни вроде реестра
    /// заблокированных, и совпадение в них по обратной стороне было бы
    /// случайным: наше имя не является зоной для ста тысяч чужих. Прямое
    /// совпадение, которое там и имеет смысл, ищется по множеству и предела
    /// не знает.
    /// </remarks>
    private const int ReverseLimit = 512;

    private readonly List<(ZapretSection Section, HashSet<string> Zones)> _sections;

    private PresetZones(List<(ZapretSection, HashSet<string>)> sections) =>
        _sections = sections;

    /// <summary>Читает списки пресета один раз.</summary>
    public static PresetZones Build(ZapretPreset preset, string? zapretRoot)
    {
        var sections = new List<(ZapretSection, HashSet<string>)>(preset.Sections.Count);

        foreach (var section in preset.Sections)
        {
            var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var inline in section.InlineDomains)
                Add(zones, inline);

            foreach (var path in section.HostListPaths)
            {
                try
                {
                    foreach (var name in HostListReader.Read(path, zapretRoot, out _))
                        Add(zones, name);
                }
                catch (Exception)
                {
                    // Списка может не быть: пресет пишут под полную установку
                    // Zapret, а у нас встроенная копия. Отсутствие файла —
                    // не повод остаться вовсе без ответа.
                }
            }

            sections.Add((section, zones));
        }

        return new PresetZones(sections);
    }

    /// <summary>
    /// Секция, которая заберёт эти имена себе; <c>null</c> — ни одна.
    /// </summary>
    /// <remarks>
    /// Первая по файлу, и это не выбор из равных: winws2 отдаёт пакет первому
    /// профилю, чей фильтр совпал, и дальше не смотрит.
    /// </remarks>
    public ZapretSection? FirstFor(IReadOnlyList<string> domains) =>
        AllFor(domains).FirstOrDefault();

    /// <summary>Все покрывающие секции, в порядке файла.</summary>
    public IEnumerable<ZapretSection> AllFor(IReadOnlyList<string> domains)
    {
        if (domains.Count == 0)
            yield break;

        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var domain in domains)
            Add(ours, domain);

        foreach (var (section, zones) in _sections)
        {
            if (Covers(zones, ours, domains))
                yield return section;
        }
    }

    private static bool Covers(
        HashSet<string> zones,
        HashSet<string> ours,
        IReadOnlyList<string> domains)
    {
        // Прямое: запись секции покрывает наше имя. Берём зоны имени от самой
        // длинной к короткой — так же, как их сопоставляет сам движок.
        foreach (var domain in domains)
        {
            foreach (var zone in HostNames.ZoneChain(Trim(domain)))
            {
                if (zones.Contains(zone))
                    return true;
            }
        }

        // Обратное: запись секции лежит под нашим именем.
        if (zones.Count > ReverseLimit)
            return false;

        foreach (var entry in zones)
        {
            foreach (var zone in HostNames.ZoneChain(entry))
            {
                if (ours.Contains(zone))
                    return true;
            }
        }

        return false;
    }

    private static void Add(HashSet<string> set, string value)
    {
        var name = Trim(value);

        if (name.Length > 0)
            set.Add(name);
    }

    private static string Trim(string value) => value.Trim().Trim('*', '.');
}
