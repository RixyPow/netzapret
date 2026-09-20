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

    /// <summary>
    /// Сколько проверок подряд не прошло; ноль — последняя удалась.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Считается затем, чтобы отличить разовый отказ от смерти. Разовый
    /// случается и у исправного сервера — сеть моргнула, узел перегружен, —
    /// и выводить его из автоподбора по одной неудаче значило бы
    /// разбрасываться теми немногими, что ещё живы.
    /// </para>
    /// <para>
    /// Ведётся самим кэшем, а не тем, кто пишет замер: мест записи три —
    /// меню консоли, команда probe и раздел «VPN», — и считать streak
    /// в каждом порознь значило бы завести три расходящихся счётчика.
    /// </para>
    /// </remarks>
    public int Failures { get; init; }

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

    /// <summary>
    /// Записывает замер, продолжая счёт неудач подряд.
    /// </summary>
    /// <remarks>
    /// Счёт ведётся здесь, а не у вызывающего: мест записи три, и считать
    /// его в каждом порознь значило бы завести три расходящихся счётчика.
    /// Пришедшее значение <see cref="ServerHealth.Failures"/> игнорируется
    /// намеренно — оно выводится из прежнего состояния, а не задаётся.
    /// </remarks>
    public void Set(ServerHealth health)
    {
        int before = _entries.GetValueOrDefault(health.Tag)?.Failures ?? 0;

        _entries[health.Tag] = health with
        {
            Failures = health.Success ? 0 : before + 1,
        };
    }

    /// <summary>
    /// Теги, которые не отвечали <paramref name="times"/> проверок подряд.
    /// </summary>
    /// <remarks>
    /// Нужны сборке конфига: мёртвый сервер в группе автоподбора не бесплатен.
    /// Движок опрашивает его наравне с живыми, а селектор может на нём осесть
    /// и молчать до следующего замера. У владельца 20.09 таких было семеро
    /// из девяти — подписка работала, но еле-еле.
    /// </remarks>
    public IReadOnlyCollection<string> Dead(int times) =>
        _entries.Values
            .Where(e => !e.Success && e.Failures >= times)
            .Select(e => e.Tag)
            .ToList();

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
