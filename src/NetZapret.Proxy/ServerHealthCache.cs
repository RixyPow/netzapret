using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NetZapret.Proxy;

/// <summary>
/// Запоминает результаты проверки серверов между заходами в меню.
/// </summary>
/// <remarks>
/// <para>
/// Проверка четырнадцати серверов идёт десятки секунд, из которых почти всё
/// время занимают мёртвые: живые отвечают за сотни миллисекунд, мёртвые
/// выбирают таймаут целиком. Повторять это при каждом заходе в список,
/// чтобы получить те же цифры, — плата ни за что.
/// </para>
/// <para>
/// Записи стареют: сервер мог лечь или подняться, и вчерашний замер выдавать
/// за нынешний нельзя. Поэтому у каждой стоит время, а меню показывает,
/// насколько она свежа, и позволяет перепроверить.
/// </para>
/// </remarks>
public sealed record ServerHealth
{
    public required string Tag { get; init; }

    public required bool Success { get; init; }

    /// <summary>Отклик в миллисекундах; <c>null</c> — не измерен.</summary>
    public double? LatencyMs { get; init; }

    public required DateTimeOffset CheckedAt { get; init; }

    public TimeSpan Age => DateTimeOffset.Now - CheckedAt;
}

public sealed class ServerHealthCache
{
    private readonly Dictionary<string, ServerHealth> _entries;

    private ServerHealthCache(Dictionary<string, ServerHealth> entries)
    {
        _entries = entries;
    }

    public static string DefaultPath => Path.Combine("runtime", "server-health.json");

    public IReadOnlyDictionary<string, ServerHealth> Entries => _entries;

    public int Count => _entries.Count;

    /// <summary>Возраст самой старой записи — по ней и судят о свежести.</summary>
    /// <remarks>
    /// По старейшей, а не по средней: пока хоть один сервер помнится
    /// со вчера, показывать список как свежий нельзя.
    /// </remarks>
    public TimeSpan? OldestAge => _entries.Count == 0
        ? null
        : _entries.Values.Max(e => e.Age);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static ServerHealthCache Load(string? path = null)
    {
        var target = path ?? DefaultPath;

        if (!File.Exists(target))
            return new ServerHealthCache([]);

        try
        {
            var list = JsonSerializer.Deserialize<List<ServerHealth>>(File.ReadAllText(target), Options);

            return new ServerHealthCache(
                list?.ToDictionary(e => e.Tag, StringComparer.Ordinal) ?? []);
        }
        catch (Exception)
        {
            // Испорченный или несовместимый файл — не повод падать:
            // это всего лишь запомненные цифры, они соберутся заново.
            return new ServerHealthCache([]);
        }
    }

    public ServerHealth? Find(string tag) => _entries.GetValueOrDefault(tag);

    public void Set(ServerHealth health) => _entries[health.Tag] = health;

    /// <summary>Оставляет только те записи, что относятся к нынешним серверам.</summary>
    /// <remarks>
    /// Подписка меняется, и без чистки файл копил бы записи об исчезнувших
    /// серверах, а по ним потом судили бы о свежести всего списка.
    /// </remarks>
    public void KeepOnly(IEnumerable<string> tags)
    {
        var keep = new HashSet<string>(tags, StringComparer.Ordinal);

        foreach (var tag in _entries.Keys.Where(t => !keep.Contains(t)).ToList())
            _entries.Remove(tag);
    }

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(target));

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                target,
                JsonSerializer.Serialize(_entries.Values.ToList(), Options),
                new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Не сохранилось — переживём: в следующий раз проверим заново.
        }
    }
}
