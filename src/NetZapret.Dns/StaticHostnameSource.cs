using System.Globalization;
using System.Net;
using NetZapret.Core.Connections;

namespace NetZapret.Dns;

/// <summary>
/// Источник доменных имён из заранее заданной карты «адрес → имя».
/// </summary>
/// <remarks>
/// <para>
/// Нужен, чтобы доменные правила можно было проверить в PoC до появления
/// прокси-движка. Файл — по строке на запись, формат <c>адрес имя</c>,
/// строки с <c>#</c> игнорируются:
/// </para>
/// <code>
/// 104.21.32.1   rutracker.org
/// 2606:4700::1  rutracker.org
/// </code>
/// <para>
/// В рабочей конфигурации это место займёт fakeip-пул sing-box, где карта
/// строится автоматически и соответствие адреса имени точное. Наблюдать
/// за реальными ответами DNS для той же цели не выйдет: один адрес CDN
/// отдаётся под сотни имён, и обратное сопоставление становится гаданием.
/// </para>
/// </remarks>
public sealed class StaticHostnameSource : IHostnameSource
{
    private readonly Dictionary<IPAddress, string> _map;

    public StaticHostnameSource(IReadOnlyDictionary<IPAddress, string> map)
    {
        _map = new Dictionary<IPAddress, string>(map.Count);
        foreach (var (address, host) in map)
            _map[address] = host;
    }

    public int Count => _map.Count;

    public static StaticHostnameSource LoadFromFile(string path)
    {
        var map = new Dictionary<IPAddress, string>();
        int lineNumber = 0;

        foreach (var rawLine in File.ReadLines(path))
        {
            lineNumber++;

            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                throw new FormatException(
                    string.Format(CultureInfo.InvariantCulture,
                        "{0}:{1}: ожидается «адрес имя», получено «{2}».", path, lineNumber, line));
            }

            if (!IPAddress.TryParse(parts[0], out var address))
            {
                throw new FormatException(
                    string.Format(CultureInfo.InvariantCulture,
                        "{0}:{1}: «{2}» не разбирается как IP-адрес.", path, lineNumber, parts[0]));
            }

            map[address] = parts[1];
        }

        return new StaticHostnameSource(map);
    }

    public string? Resolve(IPAddress address) => _map.GetValueOrDefault(address);
}
