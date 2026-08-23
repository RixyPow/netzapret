using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using NetZapret.Core.Connections;

namespace NetZapret.Etw;

/// <summary>
/// Карта «адрес → доменное имя», наполняемая из наблюдений за ответами DNS.
/// </summary>
/// <remarks>
/// <para>
/// Временное решение на период PoC. У него два врождённых недостатка, которых
/// нет у fakeip:
/// </para>
/// <list type="number">
/// <item>Один адрес CDN раздаётся под сотнями имён, и обратное сопоставление
/// становится приблизительным — здесь побеждает последнее увиденное имя.</item>
/// <item>Браузеры с DNS-over-HTTPS резолвят мимо системного клиента, и такие
/// запросы в наблюдение не попадают вовсе.</item>
/// </list>
/// <para>
/// Поэтому доменные правила, проверенные через эту карту, — свидетельство того,
/// что движок правил работает, а не того, что сопоставление надёжно.
/// </para>
/// </remarks>
public sealed class DnsNameCache : IHostnameSource
{
    private readonly ConcurrentDictionary<IPAddress, Entry> _map = new();
    private readonly TimeSpan _lifetime;
    private readonly int _capacity;

    private long _recordedCount;

    public DnsNameCache(TimeSpan? lifetime = null, int capacity = 20_000)
    {
        _lifetime = lifetime ?? TimeSpan.FromMinutes(10);
        _capacity = capacity;
    }

    public int Count => _map.Count;

    /// <summary>Сколько пар «адрес — имя» записано за всё время.</summary>
    public long RecordedCount => Interlocked.Read(ref _recordedCount);

    public void Record(IPAddress address, string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname))
            return;

        // Адреса вида ::ffff:1.2.3.4 приводим к IPv4: соединение придёт
        // с обычным IPv4-адресом, и иначе они не совпадут.
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        _map[address] = new Entry(hostname.Trim().TrimEnd('.'), DateTime.UtcNow);
        Interlocked.Increment(ref _recordedCount);

        if (_map.Count > _capacity)
            Prune();
    }

    public string? Resolve(IPAddress address)
    {
        if (!_map.TryGetValue(address, out var entry))
            return null;

        if (DateTime.UtcNow - entry.RecordedAtUtc > _lifetime)
        {
            _map.TryRemove(address, out _);
            return null;
        }

        return entry.Hostname;
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;

        foreach (var (address, entry) in _map)
        {
            if (now - entry.RecordedAtUtc > _lifetime)
                _map.TryRemove(address, out _);
        }
    }

    /// <summary>
    /// Разбирает поле QueryResults события DNS-клиента.
    /// </summary>
    /// <remarks>
    /// Формат — записи через точку с запятой, где CNAME'ы помечены префиксом
    /// <c>type:</c>, а адреса идут как есть:
    /// <c>type:  5 www-example.l.google.com;142.250.185.78;</c>
    /// </remarks>
    public static IEnumerable<IPAddress> ParseQueryResults(string? queryResults)
    {
        if (string.IsNullOrWhiteSpace(queryResults))
            yield break;

        foreach (var part in queryResults.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();

            if (token.Length == 0 || token.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IPAddress.TryParse(token, out var address))
                continue;

            if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                continue;

            yield return address;
        }
    }

    private readonly record struct Entry(string Hostname, DateTime RecordedAtUtc);
}
