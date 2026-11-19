using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>
/// Узнаёт и запоминает актуальные адреса DoH-провайдеров по их имени сервера.
/// </summary>
/// <remarks>
/// <para>
/// Заведено 19.11 по образцу Zapret GUI (<c>dns/provider_addresses.py</c>,
/// <c>dns/doh_name_addresses.py</c>, <c>dns/zapret_dns_server.py</c>).
/// У провайдеров вроде Comss.one и Zapret DNS адреса серверов меняются без
/// выхода новой версии программы: 19.11 встроенный адрес Comss.one
/// <c>83.220.169.155</c> отдавал недоверенный сертификат, а его имя
/// <c>dns.comss.one</c> уже вело на живые <c>141.133.184.24</c>,
/// <c>.183.77</c> и <c>155.212.172.101</c>.
/// </para>
/// <para>
/// Каждый адрес, полученный по имени через эталонный DoH, проверяется настоящим
/// запросом DoH с проверкой сертификата для <see cref="DnsProvider.TlsName"/>:
/// подменённый провайдером адрес сертификат не предъявит и в память не попадёт.
/// Если ни один кандидат не ответил, прежний сохранённый адрес не стирается.
/// </para>
/// </remarks>
public static class DnsProviderAddresses
{
    /// <summary>Файл выученных адресов в рабочей папке.</summary>
    public static string DefaultPath => Path.Combine("runtime", "dns-provider-addresses.json");

    /// <summary>Как часто переспрашивать адрес имени, даже когда текущий отвечает.</summary>
    public static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);

    /// <summary>Сколько прежних адресов хранить, чтобы узнавать старый выбор человека.</summary>
    private const int MaxFormer = 8;

    /// <summary>Сколько кандидатов из ответа имени пробовать за раз.</summary>
    private const int MaxCandidates = 6;

    /// <summary>Имя для проверки, что кандидат действительно отвечает на DoH.</summary>
    private const string ProbeDomain = "example.com";

    /// <summary>Запомненная запись об одном DoH-имени.</summary>
    public sealed record LearnedAddress(
        string Address,
        DateTimeOffset VerifiedAt,
        IReadOnlyList<string>? Former = null);

    private static readonly object CacheLock = new();
    private static string? _cachePath;
    private static DateTime _cacheWriteUtc;
    private static Dictionary<string, LearnedAddress>? _cache;

    /// <summary>
    /// Возвращает провайдера с выученным адресом, если для него разрешено
    /// обновление по имени (<see cref="DnsProvider.AddressesByName"/>) и в памяти
    /// есть проверенный адрес.
    /// </summary>
    public static DnsProvider Apply(DnsProvider provider, string? path = null)
    {
        if (!provider.AddressesByName || string.IsNullOrWhiteSpace(provider.TlsName))
            return provider;

        var learned = Load(path);

        if (!learned.TryGetValue(provider.TlsName, out var entry) || !IsUsableIpv4(entry.Address))
            return provider;

        // Прежние адреса: встроенные прежние, встроенный нынешний (если отличается)
        // и история прошлых выученных. По ним ByAddress находит провайдера, даже
        // если в настройках человека остался старый IP.
        var former = new List<string>();

        void AddFormer(string? ip)
        {
            if (string.IsNullOrWhiteSpace(ip)
                || string.Equals(ip, entry.Address, StringComparison.Ordinal)
                || former.Contains(ip, StringComparer.Ordinal))
            {
                return;
            }

            former.Add(ip);
        }

        if (provider.FormerAddresses is not null)
        {
            foreach (var ip in provider.FormerAddresses)
                AddFormer(ip);
        }

        AddFormer(provider.TlsAddress);

        if (entry.Former is not null)
        {
            foreach (var ip in entry.Former)
                AddFormer(ip);
        }

        return provider with
        {
            TlsAddress = entry.Address,
            FormerAddresses = former.Count > 0 ? former : provider.FormerAddresses,
        };
    }

    /// <summary>
    /// Относится ли адрес из настроек к этому провайдеру: совпадает с нынешним
    /// <see cref="DnsProvider.TlsAddress"/> либо с одним из его прежних адресов.
    /// </summary>
    public static bool Matches(DnsProvider provider, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        if (string.Equals(provider.TlsAddress, address, StringComparison.Ordinal))
            return true;

        return provider.FormerAddresses is not null
            && provider.FormerAddresses.Any(f => string.Equals(f, address, StringComparison.Ordinal));
    }

    /// <summary>Пора ли переспросить адрес имени DoH: прошло больше суток или ещё не проверялось.</summary>
    public static bool IsDue(DnsProvider provider, DateTimeOffset now, string? path = null)
    {
        if (!provider.AddressesByName || string.IsNullOrWhiteSpace(provider.TlsName))
            return false;

        var learned = Load(path);

        if (!learned.TryGetValue(provider.TlsName, out var entry))
            return true;

        return now - entry.VerifiedAt >= RefreshEvery || entry.VerifiedAt > now;
    }

    /// <summary>
    /// Спрашивает имя DoH у эталонных резолверов, проверяет кандидатов живым
    /// запросом DoH и запоминает первый ответивший адрес.
    /// </summary>
    /// <returns>
    /// Обновлённый провайдер (если нашёлся живой адрес) и признак, что адрес сменился.
    /// Если ни один кандидат не ответил — исходный провайдер без изменений.
    /// </returns>
    public static Task<(DnsProvider Provider, bool Changed)> RefreshAsync(
        DnsProvider provider,
        int? nic,
        CancellationToken cancellationToken,
        string? path = null) =>
        RefreshAsync(
            provider,
            async (host, ct) =>
            {
                var answer = await DnsSurvey.HonestAsync(host, DnsWire.TypeA, nic, ct);
                return answer is { Code: 0 } ? answer.Addresses.Select(a => a.ToString()).ToList() : [];
            },
            async (ip, tlsName, dohPath, ct) =>
                await DnsSurvey.DohQueryAsync(ip, tlsName, dohPath, ProbeDomain, DnsWire.TypeA, nic, ct) is { Code: 0 },
            DateTimeOffset.Now,
            cancellationToken,
            path);

    /// <summary>
    /// То же с подменой сети и часов — чтобы алгоритм целиком покрывался тестами без сети.
    /// </summary>
    internal static async Task<(DnsProvider Provider, bool Changed)> RefreshAsync(
        DnsProvider provider,
        Func<string, CancellationToken, Task<IReadOnlyList<string>>> resolve,
        Func<string, string, string, CancellationToken, Task<bool>> probeDoh,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        string? path = null)
    {
        if (!provider.AddressesByName
            || string.IsNullOrWhiteSpace(provider.TlsName)
            || string.IsNullOrWhiteSpace(provider.DohPath))
        {
            return (provider, false);
        }

        IReadOnlyList<string> resolved = [];

        try
        {
            resolved = await resolve(provider.TlsName, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Эталонные резолверы не ответили — остаётся встроенный адрес провайдера.
        }

        // Если нынешний адрес по-прежнему в свежем ответе имени, пробуем его первым:
        // DNS перемешивает узлы (round-robin у Comss и Cloudflare), и без этого
        // живой адрес скакал бы между ними при каждой суточной проверке.
        var fresh = resolved.Where(IsUsableIpv4).Distinct(StringComparer.Ordinal).ToList();
        IEnumerable<string> ordered = provider.TlsAddress is { } current
            && fresh.Contains(current, StringComparer.Ordinal)
                ? [current, .. fresh]
                : fresh;

        var candidates = ordered
            .Concat(provider.TlsAddress is not null ? [provider.TlsAddress] : [])
            .Where(IsUsableIpv4)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxCandidates)
            .ToList();

        string? working = null;

        foreach (var candidate in candidates)
        {
            try
            {
                if (await probeDoh(candidate, provider.TlsName, provider.DohPath, cancellationToken))
                {
                    working = candidate;
                    break;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Этот кандидат не ответил — пробуем следующий.
            }
        }

        if (working is null)
            return (provider, false);

        bool changed = !string.Equals(working, provider.TlsAddress, StringComparison.Ordinal);

        Remember(provider.TlsName, working, provider.TlsAddress, now, path);

        return (Apply(provider, path), changed);
    }

    /// <summary>Запоминает проверенный адрес для имени DoH; прежний переносит в историю.</summary>
    internal static void Remember(
        string tlsName,
        string address,
        string? previous,
        DateTimeOffset verifiedAt,
        string? path = null)
    {
        var target = path ?? DefaultPath;

        lock (CacheLock)
        {
            var data = new Dictionary<string, LearnedAddress>(ReadFile(target), StringComparer.OrdinalIgnoreCase);
            var former = new List<string>();

            void AddFormer(string? ip)
            {
                if (string.IsNullOrWhiteSpace(ip)
                    || string.Equals(ip, address, StringComparison.Ordinal)
                    || !IsUsableIpv4(ip)
                    || former.Contains(ip, StringComparer.Ordinal))
                {
                    return;
                }

                former.Add(ip);
            }

            if (data.TryGetValue(tlsName, out var existing))
            {
                AddFormer(existing.Address);

                if (existing.Former is not null)
                {
                    foreach (var ip in existing.Former)
                        AddFormer(ip);
                }
            }

            AddFormer(previous);

            data[tlsName] = new LearnedAddress(
                address,
                verifiedAt,
                former.Count > 0 ? former.Take(MaxFormer).ToList() : null);

            WriteFile(target, data);
            UpdateCacheLocked(target, data);
        }
    }

    /// <summary>Запомненные адреса; файла нет или он битый — пусто.</summary>
    public static IReadOnlyDictionary<string, LearnedAddress> Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        lock (CacheLock)
        {
            var writeUtc = File.Exists(target) ? File.GetLastWriteTimeUtc(target) : DateTime.MinValue;

            if (_cache is not null
                && string.Equals(_cachePath, target, StringComparison.OrdinalIgnoreCase)
                && _cacheWriteUtc == writeUtc)
            {
                return _cache;
            }

            var loaded = ReadFile(target);
            _cachePath = target;
            _cacheWriteUtc = writeUtc;
            _cache = loaded;

            return loaded;
        }
    }

    /// <summary>Сбрасывает кэш в памяти — для тестов.</summary>
    internal static void ClearCache()
    {
        lock (CacheLock)
        {
            _cache = null;
            _cachePath = null;
            _cacheWriteUtc = DateTime.MinValue;
        }
    }

    /// <summary>Настоящий глобальный IPv4: не заглушка и не частная сеть.</summary>
    internal static bool IsUsableIpv4(string? address) =>
        IPAddress.TryParse(address?.Trim(), out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork
        && !BlockCheck.IsStub(ip)
        && !DnsSpoof.IsPrivate(ip);

    private static Dictionary<string, LearnedAddress> ReadFile(string target)
    {
        try
        {
            if (!File.Exists(target))
                return new Dictionary<string, LearnedAddress>(StringComparer.OrdinalIgnoreCase);

            var raw = JsonSerializer.Deserialize<Dictionary<string, LearnedAddress>>(File.ReadAllText(target), Json);

            return raw is null
                ? new Dictionary<string, LearnedAddress>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, LearnedAddress>(raw, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Файл кэша — не критичен: без него работают встроенные адреса.
            return new Dictionary<string, LearnedAddress>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void WriteFile(string target, Dictionary<string, LearnedAddress> data)
    {
        try
        {
            var directory = Path.GetDirectoryName(target);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var temporary = target + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(data, Json), new UTF8Encoding(false));
            File.Move(temporary, target, overwrite: true);
        }
        catch (Exception)
        {
            // Не записалось — в этом запуске адрес всё равно остаётся в кэше памяти.
        }
    }

    private static void UpdateCacheLocked(string target, Dictionary<string, LearnedAddress> data)
    {
        _cachePath = target;
        _cacheWriteUtc = File.Exists(target) ? File.GetLastWriteTimeUtc(target) : DateTime.MinValue;
        _cache = data;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
