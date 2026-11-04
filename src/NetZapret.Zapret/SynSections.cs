using System.Globalization;
using System.Net;
using NetZapret.Core.Services;

namespace NetZapret.Zapret;

/// <summary>Секция пресета, которая берёт соединение к адресу ещё на SYN, и часть «Маршрутов» с её списком.</summary>
/// <param name="Section">Имя секции (<c>--name</c>).</param>
/// <param name="List">Файл списка адресов, в котором нашёлся адрес.</param>
/// <param name="Service">Сервис части с тем же списком; <c>null</c> — в «Маршрутах» такой части нет.</param>
/// <param name="Part">Часть «Маршрутов» с тем же списком.</param>
public sealed record SynSection(string Section, string List, string? Service, ServicePart? Part);

/// <summary>
/// Какая секция пресета забирает соединение к адресу до того, как в нём появится имя.
/// </summary>
/// <remarks>
/// <para>
/// Обсуждение №52: у двоих «Сайт не открывается?» на все имена Discord ответил
/// «порт 443 закрыт» и посоветовал VPN, а порт закрывала наша же секция
/// «discord.com (IP fallback)» — её подделка SYN (<c>send</c> + <c>syndata</c>)
/// по <c>ipset-discord</c> глушила TCP к 162.159.x. Пробный winws2 без пресета
/// тот же discord.com открывал.
/// </para>
/// <para>
/// На SYN имени нет, поэтому его берут только секции по адресам: секция со
/// списком имён без имени не совпадает, щит «напрямую» — тоже. winws2 отдаёт
/// пакет первому совпавшему профилю, значит ответ — первая по порядку секция,
/// у которой адрес в списке, порт в <c>--filter-tcp</c> и нет фильтра по
/// протоколу (<c>--filter-l7</c> на SYN не известен). Секция <c>pass</c> первой —
/// соединение берёт она и десинк не трогает: ответ «никакая».
/// </para>
/// </remarks>
public static class SynSections
{
    /// <summary>Первая секция, которая возьмёт SYN к <paramref name="address"/> на <paramref name="port"/>; <c>null</c> — никакая.</summary>
    public static SynSection? Find(ZapretPreset preset, string? zapretRoot, IPAddress address, int port)
    {
        string text = address.ToString();

        foreach (var section in preset.Sections)
        {
            if (section.IpSetPaths.Count == 0 || section.HostListPaths.Count > 0 || section.InlineDomains.Count > 0)
                continue;

            if (!section.CarriesTcp || section.RawArguments.Any(a => a.StartsWith("--filter-l7=", StringComparison.OrdinalIgnoreCase)))
                continue;

            if (section.TcpPorts is { } ports && !PortsInclude(ports, port))
                continue;

            foreach (var list in section.IpSetPaths)
            {
                var ranges = AddressListReader.Expand([list], zapretRoot, out _);

                if (AddressOwners.From(("list", ranges.ToArray())).Of(text) is null)
                    continue;

                if (section.IsPassThrough)
                    return null;

                var (service, part) = PartOf(list);

                return new SynSection(section.Name, Path.GetFileName(list.Replace('\\', '/')), service, part);
            }
        }

        return null;
    }

    /// <summary>Часть «Маршрутов» по адресам с тем же файлом списка (наш <c>nz-</c> — тот же).</summary>
    public static (string? Service, ServicePart? Part) PartOf(string list)
    {
        var bare = Bare(list);

        foreach (var service in ServiceCatalog.All)
        {
            foreach (var part in service.Parts.Where(p => p.ByAddress))
            {
                if (Bare(part.List).Equals(bare, StringComparison.OrdinalIgnoreCase))
                    return (service.Name, part);
            }
        }

        return (null, null);
    }

    /// <summary>Входит ли порт в запись <c>--filter-tcp</c> вида <c>80,443-65535</c>.</summary>
    public static bool PortsInclude(string spec, int port)
    {
        foreach (var raw in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = raw.IndexOf('-');

            if (dash < 0)
            {
                if (int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int single) && single == port)
                    return true;

                continue;
            }

            if (int.TryParse(raw[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out int from)
                && int.TryParse(raw[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int to)
                && port >= from && port <= to)
            {
                return true;
            }
        }

        return false;
    }

    private static string Bare(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        return name.StartsWith("nz-", StringComparison.OrdinalIgnoreCase) ? name[3..] : name;
    }
}
