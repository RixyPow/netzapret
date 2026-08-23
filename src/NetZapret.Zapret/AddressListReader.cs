using System.Net;

namespace NetZapret.Zapret;

/// <summary>
/// Разворачивает записи секции <c>capture</c> в список префиксов.
/// </summary>
/// <remarks>
/// Запись — либо готовый CIDR, либо путь к файлу списка Zapret. Второе
/// избавляет от копирования сотен диапазонов в свой конфиг и от их
/// устаревания: списки обновляются вместе с Zapret.
/// </remarks>
public static class AddressListReader
{
    /// <summary>
    /// Разворачивает записи в префиксы.
    /// </summary>
    /// <param name="entries">CIDR либо пути к спискам.</param>
    /// <param name="zapretRoot">Корень Zapret для относительных путей.</param>
    /// <param name="problems">Записи, которые не удалось разобрать.</param>
    public static IReadOnlyList<string> Expand(
        IReadOnlyList<string> entries,
        string? zapretRoot,
        out IReadOnlyList<string> problems)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var raw in entries)
        {
            var entry = raw.Trim();

            if (entry.Length == 0)
                continue;

            var cidr = NormalizeCidr(entry);

            if (cidr is not null)
            {
                if (seen.Add(cidr))
                    result.Add(cidr);

                continue;
            }

            var path = ResolvePath(entry, zapretRoot);

            if (path is null)
            {
                failures.Add($"{entry}: не CIDR и файл не найден");
                continue;
            }

            int added = 0;

            foreach (var line in ReadList(path))
            {
                var parsed = NormalizeCidr(line);

                if (parsed is not null && seen.Add(parsed))
                {
                    result.Add(parsed);
                    added++;
                }
            }

            if (added == 0)
                failures.Add($"{entry}: список пуст или не содержит адресов");
        }

        problems = failures;
        return result;
    }

    private static string? ResolvePath(string entry, string? zapretRoot)
    {
        var candidates = new List<string>();

        if (Path.IsPathRooted(entry))
        {
            candidates.Add(entry);
        }
        else
        {
            var relative = entry.Replace('/', Path.DirectorySeparatorChar);

            if (!string.IsNullOrEmpty(zapretRoot))
                candidates.Add(Path.Combine(zapretRoot, relative));

            candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), relative));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> ReadList(string path)
    {
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            int comment = line.IndexOf('#');
            if (comment > 0)
                line = line[..comment].Trim();

            if (line.Length > 0)
                yield return line;
        }
    }

    /// <summary>
    /// Приводит запись к виду CIDR; <c>null</c>, если это не адрес.
    /// </summary>
    private static string? NormalizeCidr(string value)
    {
        var text = value.Trim();

        if (text.Length == 0)
            return null;

        int slash = text.IndexOf('/');

        if (slash >= 0)
        {
            return IPAddress.TryParse(text[..slash], out _) && int.TryParse(text[(slash + 1)..], out _)
                ? text
                : null;
        }

        if (!IPAddress.TryParse(text, out var address))
            return null;

        // route_address принимает только префиксы, а в списках Zapret
        // встречаются голые адреса.
        int bits = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
        return $"{address}/{bits}";
    }
}
