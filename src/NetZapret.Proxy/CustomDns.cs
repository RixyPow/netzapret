using System.Net;
using System.Text;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>
/// Свои резолверы — те, которых нет во встроенном списке.
/// </summary>
/// <remarks>
/// <para>
/// Просьба владельца 26.09, после отзыва в обсуждении #7: человек знает
/// резолвер, который у него работает, а выбрать его было нельзя — только
/// из восемнадцати вписанных в код.
/// </para>
/// <para>
/// Лежат в <c>config/dns.user.json</c>: файл личный, как rules.user.yaml,
/// в git и в поставку не идёт. Встают в тот же список, что и встроенные
/// (<see cref="DnsSurvey.All"/>): их меряет обзор, их видит автовыбор,
/// их можно выбрать апстримом туннеля — если у них есть DoH.
/// </para>
/// <para>
/// Апстрим sing-box — только DoH по адресу, и имя в сертификате обязательно:
/// без него проверка сертификата по адресу не пройдёт. Резолвер без DoH
/// добавить можно — обзор его измерит, — но выбрать для туннеля нельзя,
/// и окно говорит об этом прямо, а не прячет строку.
/// </para>
/// </remarks>
public static class CustomDns
{
    public static string DefaultPath => Path.Combine("config", "dns.user.json");

    /// <summary>Свои резолверы; пусто, если файла нет или он не читается.</summary>
    public static IReadOnlyList<DnsProvider> Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            if (!File.Exists(target))
                return [];

            var raw = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(target), Json) ?? [];

            return raw
                .Select(e => Build(e.Name, e.Address, e.DohName, e.DohPath, e.Secondary).Provider)
                .OfType<DnsProvider>()
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Испорченный файл не должен ронять вкладку и обзор: без своих
            // резолверов всё работает, как работало до них.
            return [];
        }
    }

    /// <summary>
    /// Собирает резолвер из введённого; <c>Problem</c> — почему не вышло.
    /// </summary>
    /// <param name="address">Адрес резолвера — IPv4 или IPv6.</param>
    /// <param name="dohName">Имя в сертификате DoH; пусто — DoH нет.</param>
    /// <param name="dohPath">Путь DoH; пусто — <c>/dns-query</c>.</param>
    /// <param name="secondary">Второй адрес для обычного DNS; пусто — нет.</param>
    public static (DnsProvider? Provider, string? Problem) Build(
        string? name,
        string? address,
        string? dohName,
        string? dohPath,
        string? secondary = null)
    {
        var title = name?.Trim() ?? string.Empty;

        if (!IPAddress.TryParse(address?.Trim(), out var ip))
            return (null, "Адрес нужен цифрами, например 111.88.96.50: имя резолвера туннелю не подходит — его самого нечем разрешить.");

        var udp = new List<string> { ip.ToString() };

        if (!string.IsNullOrWhiteSpace(secondary))
        {
            if (!IPAddress.TryParse(secondary.Trim(), out var second))
                return (null, "Второй адрес — тоже цифрами, либо оставьте поле пустым.");

            if (!second.Equals(ip))
                udp.Add(second.ToString());
        }

        if (title.Length == 0)
            title = ip.ToString();

        var host = dohName?.Trim().Trim('/').ToLowerInvariant();

        // Люди вставляют ссылку целиком — https://dns.example.com/dns-query.
        // Путь из неё берётся, если отдельно не задан.
        if (host is { Length: > 0 } && host.Contains("://"))
        {
            if (!Uri.TryCreate(host, UriKind.Absolute, out var link))
                return (null, "Ссылка DoH не разбирается — впишите одно имя, например dns.example.com.");

            host = link.Host;

            if (string.IsNullOrWhiteSpace(dohPath) && link.AbsolutePath.Length > 1)
                dohPath = link.AbsolutePath;
        }

        if (host is { Length: > 0 } && (!host.Contains('.') || host.Contains(' ')))
            return (null, "Имя для DoH — вида dns.example.com, без пути и пробелов.");

        var route = string.IsNullOrWhiteSpace(dohPath) ? "/dns-query" : "/" + dohPath.Trim().TrimStart('/');

        return (new DnsProvider(
            title,
            udp,
            TlsAddress: host is { Length: > 0 } ? ip.ToString() : null,
            TlsName: host is { Length: > 0 } ? host : null,
            DohPath: host is { Length: > 0 } ? route : null,
            Note: host is { Length: > 0 } ? "свой" : "свой · без DoH — только для обзора",
            Own: true), null);
    }

    /// <summary>
    /// Добавляет резолвер; с тем же адресом — заменяет.
    /// </summary>
    /// <remarks>
    /// По адресу, а не по названию: два названия одного адреса — одна строка
    /// в выборе, и отличить их можно было бы только по подписи.
    /// </remarks>
    public static void Save(DnsProvider provider, string? path = null)
    {
        var target = path ?? DefaultPath;
        var entries = Read(target);

        entries.RemoveAll(e => string.Equals(e.Address, provider.Udp[0], StringComparison.Ordinal));
        entries.Add(new Entry
        {
            Name = provider.Name,
            Address = provider.Udp[0],
            Secondary = provider.Udp.Count > 1 ? provider.Udp[1] : null,
            DohName = provider.TlsName,
            DohPath = provider.TlsName is null ? null : provider.DohPath,
        });

        Write(target, entries);
    }

    /// <summary>Убирает свой резолвер по адресу; встроенные не трогаются по построению.</summary>
    public static bool Remove(string address, string? path = null)
    {
        var target = path ?? DefaultPath;
        var entries = Read(target);

        if (entries.RemoveAll(e => string.Equals(e.Address, address, StringComparison.Ordinal)) == 0)
            return false;

        Write(target, entries);
        return true;
    }

    private static List<Entry> Read(string target)
    {
        if (!File.Exists(target))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(target), Json) ?? [];
        }
        catch (JsonException)
        {
            // Не разобрался — отодвигаем, а не затираем: в нём могла быть
            // правка руками, и следующая запись стёрла бы её молча.
            File.Move(target, target + ".broken", overwrite: true);
            return [];
        }
    }

    private static void Write(string target, List<Entry> entries)
    {
        var directory = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(target, JsonSerializer.Serialize(entries, Json), new UTF8Encoding(false));
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class Entry
    {
        public string? Name { get; set; }
        public string? Address { get; set; }
        public string? Secondary { get; set; }
        public string? DohName { get; set; }
        public string? DohPath { get; set; }
    }
}
