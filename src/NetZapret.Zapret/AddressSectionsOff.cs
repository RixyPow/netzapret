using System.Text;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Zapret;

/// <summary>
/// Секции пресета по адресам, чья часть в «Маршрутах» выключена: winws2
/// их не получает.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №52 (03.11): у двоих Discord перестал открываться через десинк,
/// а у «Запасного пути по адресам» Discord в «Маршрутах» стояло «выключено» —
/// по умолчанию, у всех. Выключатель гасил только правило движка, а секция
/// пресета «discord.com (IP fallback)» по тому же <c>ipset-discord</c> работала
/// всегда: в отчёте winws2 загружал список (58 сетей) и подделывал SYN к
/// 162.159.x. Окно рецептов в том же отчёте — пробный winws2 без пресета —
/// открывало discord.com теми же рецептами (страница 200 за 0,6 с), а основной
/// десинк не ставил даже TCP. Окно говорило «выключено», а секция работала;
/// владелец 03.11 решил, что выключатель управляет и секцией.
/// </para>
/// <para>
/// В первом виде (03.11) секцию гасило и «выключено» по умолчанию — у всех,
/// кто часть не трогал. Это была моя ошибка: на секции держался голос Discord.
/// Управляющее соединение к discord.media на 162.159.x без её подделки SYN
/// замерзало через 14 с, голос переподключался каждые 19 с (04.11, журнал
/// Discord и A/B у владельца); по замеру 03.10 она же нужна сайтам Cloudflare
/// (i.ua, ukr.net, fandom без неё 0 из 3). С 04.11 запасной путь по умолчанию
/// «десинк» (<see cref="ServicePart.PresetDesync"/>), и секцию гасит только
/// «выключено», выбранное человеком.
/// </para>
/// <para>
/// Решение — тем же вопросом, каким «Маршруты» показывают часть
/// (<see cref="ServiceRouting.Describe"/>): своя запись «выключено» или своей
/// записи нет, а часть выключена по умолчанию. Движок правил этого не знает —
/// выключенное правило в него не попадает вовсе (<see cref="RoutingRule.Off"/>).
/// </para>
/// <para>
/// Передаётся файлом, как игровой UDP Riot (<see cref="UdpOffDesync"/>):
/// правила читает сборка списков, строку winws2 собирает супервизор.
/// </para>
/// </remarks>
public static class AddressSectionsOff
{
    public static string DefaultPath => Path.Combine("runtime", "desync-ipset-off.txt");

    /// <summary>Имена файлов списков частей по адресам, выключенных сейчас.</summary>
    public static IReadOnlyList<string> Choose(UserRulesFile userRules)
    {
        var result = new List<string>();

        foreach (var part in ServiceCatalog.All.SelectMany(s => s.Parts).Where(p => p.ByAddress))
        {
            var own = userRules.Entries.FirstOrDefault(e =>
                e.Match == MatchKind.IpSet && e.Enabled && e.Matches(part.List));

            bool off = own is { Off: true } || own is null && part.DefaultOff;

            if (off)
                result.Add(Path.GetFileName(part.List));
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Пресет без секций, которые узнают трафик только по выключенным спискам адресов.
    /// </summary>
    /// <remarks>
    /// Секция со списком имён остаётся: имя она узнаёт сама, и выключенный
    /// список адресов у неё — лишь часть охвата. Наш список с приставкой
    /// <c>nz-</c> (так его кладут <c>build.cmd</c> и <c>pack.cmd</c>) — тот же список.
    /// </remarks>
    public static ZapretPreset Strip(ZapretPreset preset, IReadOnlyCollection<string> offLists, out IReadOnlyList<string> removed)
    {
        var dropped = new List<string>();

        if (offLists.Count == 0)
        {
            removed = dropped;
            return preset;
        }

        var off = offLists.Select(Bare).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = preset.Sections.Where(section =>
        {
            bool byAddressOnly = section.IpSetPaths.Count > 0
                && section.HostListPaths.Count == 0
                && section.InlineDomains.Count == 0;

            if (byAddressOnly && section.IpSetPaths.All(p => off.Contains(Bare(p))))
            {
                dropped.Add(section.Name);
                return false;
            }

            return true;
        }).ToList();

        removed = dropped;
        return dropped.Count == 0 ? preset : preset with { Sections = kept };
    }

    private static string Bare(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        return name.StartsWith("nz-", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
    }

    /// <summary>Записывает списки; пустой тоже пишется — иначе вчерашний выключал бы секцию.</summary>
    public static void Write(IReadOnlyList<string> lists, string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllLines(target, lists, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // Не записалось — секции пресета остаются все, как до 03.11.
        }
    }

    public static IReadOnlyList<string> Read(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            return File.Exists(target)
                ? File.ReadAllLines(target).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
                : [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}
