using System.Net;
using System.Net.Sockets;
using NetZapret.Core.Connections;

namespace NetZapret.Core.Rules;

/// <summary>
/// Одно правило маршрутизации из конфига.
/// </summary>
public sealed class RoutingRule
{
    public required MatchKind Match { get; init; }

    /// <summary>Шаблон: имя exe, доменная маска или CIDR — в зависимости от <see cref="Match"/>.</summary>
    public required string Value { get; init; }

    public required RoutingMode Mode { get; init; }

    /// <summary>
    /// Тег исходящего для <see cref="RoutingMode.Proxy"/>: конкретный сервер подписки
    /// либо "auto" (селектор с проверкой задержки). Игнорируется для прочих режимов.
    /// </summary>
    public string? Server { get; init; }

    /// <summary>
    /// Индекс в исходном списке — для диагностики. Проставляется при загрузке
    /// конфига и перезаписывается в <see cref="RuleEngine.Build"/>, чтобы номера
    /// в логе всегда соответствовали порядку строк в файле.
    /// </summary>
    public int Ordinal { get; set; }

    // Скомпилированные представления Value. Считаются один раз при загрузке конфига,
    // потому что Matches вызывается на каждое новое соединение.
    private GlobMatcher? _glob;
    private IpCidrRange? _cidr;
    private IReadOnlyList<IpCidrRange>? _cidrSet;

    /// <summary>
    /// Подсети, загруженные из файла для <see cref="MatchKind.IpSet"/>.
    /// </summary>
    /// <remarks>
    /// Пусто, пока не вызван <see cref="LoadIpSet"/>: загрузка требует знания
    /// корня Zapret, а модель правил о нём не знает и знать не должна.
    /// Непрогруженное правило просто не совпадает ни с чем.
    /// </remarks>
    public IReadOnlyList<string> IpSetCidrs { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Готовит правило к использованию и валидирует <see cref="Value"/>.
    /// Бросает <see cref="RuleConfigurationException"/> на неразбираемом значении.
    /// </summary>
    public void Compile()
    {
        switch (Match)
        {
            case MatchKind.Process:
            case MatchKind.Domain:
                _glob = GlobMatcher.Compile(Value);
                break;

            case MatchKind.Ip:
                _cidr = IpCidrRange.Parse(Value);
                break;

            case MatchKind.IpSet:
                // Путь к файлу проверяется при загрузке списка, а не здесь.
                break;

            default:
                throw new RuleConfigurationException($"Неизвестный тип правила '{Match}' (правило #{Ordinal}).");
        }
    }

    /// <summary>
    /// Проверяет соединение против правила.
    /// </summary>
    /// <param name="connection">Наблюдаемое соединение.</param>
    /// <param name="reason">Что именно совпало — для лога.</param>
    public bool Matches(ConnectionEvent connection, out string reason)
    {
        reason = string.Empty;

        switch (Match)
        {
            case MatchKind.Process:
            {
                // Сопоставляем и с полным путём, и с одним именем файла: в конфиге
                // удобно писать "somegame.exe", но иногда нужно различить два
                // одноимённых exe из разных каталогов.
                var path = connection.ExecutablePath;
                if (path is null)
                    return false;

                if (_glob!.IsMatch(path))
                {
                    reason = $"process path ~ {Value}";
                    return true;
                }

                var name = connection.ExecutableName;
                if (name is not null && _glob.IsMatch(name))
                {
                    reason = $"process name ~ {Value}";
                    return true;
                }

                return false;
            }

            case MatchKind.Domain:
            {
                var host = connection.Hostname;
                if (host is null)
                    return false;

                if (!_glob!.IsMatch(host))
                    return false;

                reason = $"domain {host} ~ {Value}";
                return true;
            }

            case MatchKind.Ip:
            {
                var addr = connection.RemoteAddress;
                if (addr is null)
                    return false;

                if (!_cidr!.Contains(addr))
                    return false;

                reason = $"ip {addr} in {Value}";
                return true;
            }

            case MatchKind.IpSet:
            {
                var addr = connection.RemoteAddress;
                if (addr is null || _cidrSet is null)
                    return false;

                foreach (var range in _cidrSet)
                {
                    if (!range.Contains(addr))
                        continue;

                    reason = $"ip {addr} в списке {Value}";
                    return true;
                }

                return false;
            }

            default:
                return false;
        }
    }

    /// <summary>
    /// Загружает подсети для правила по списку.
    /// </summary>
    public void LoadIpSet(IReadOnlyList<string> cidrs)
    {
        if (Match != MatchKind.IpSet)
            throw new InvalidOperationException($"Правило #{Ordinal} не ссылается на список адресов.");

        IpSetCidrs = cidrs;
        _cidrSet = cidrs.Select(IpCidrRange.Parse).ToList();
    }

    public override string ToString() => $"#{Ordinal} {Match.ToString().ToLowerInvariant()}:{Value} -> {Mode.ToString().ToLowerInvariant()}";
}

/// <summary>
/// Минимальный разбор CIDR.
/// </summary>
/// <remarks>
/// Не <see cref="System.Net.IPNetwork"/> намеренно: тот появился только в .NET 8
/// и требует, чтобы адрес был точным началом подсети, тогда как в конфиге удобно
/// писать <c>192.168.1.10/24</c>. Заодно здесь принимается голый адрес без маски.
/// Имя отличается от системного типа, чтобы не приходилось разрешать конфликт
/// в каждом файле с <c>using System.Net;</c>.
/// </remarks>
public sealed class IpCidrRange
{
    private readonly byte[] _prefix;
    private readonly int _prefixLength;
    private readonly AddressFamily _family;

    private IpCidrRange(byte[] prefix, int prefixLength, AddressFamily family)
    {
        _prefix = prefix;
        _prefixLength = prefixLength;
        _family = family;
    }

    public static IpCidrRange Parse(string value)
    {
        var text = value.Trim();
        var slash = text.IndexOf('/');

        string addressPart = slash < 0 ? text : text[..slash];
        if (!IPAddress.TryParse(addressPart, out var address))
            throw new RuleConfigurationException($"'{value}' не разбирается как IP или CIDR.");

        int maxBits = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
        int prefixLength = maxBits;

        if (slash >= 0)
        {
            if (!int.TryParse(text[(slash + 1)..], out prefixLength) || prefixLength < 0 || prefixLength > maxBits)
                throw new RuleConfigurationException($"'{value}': длина префикса должна быть в диапазоне 0..{maxBits}.");
        }

        return new IpCidrRange(address.GetAddressBytes(), prefixLength, address.AddressFamily);
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != _family)
            return false;

        var bytes = address.GetAddressBytes();
        int fullBytes = _prefixLength / 8;
        int remainingBits = _prefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (bytes[i] != _prefix[i])
                return false;
        }

        if (remainingBits == 0)
            return true;

        int mask = 0xFF << (8 - remainingBits);
        return (bytes[fullBytes] & mask) == (_prefix[fullBytes] & mask);
    }
}
