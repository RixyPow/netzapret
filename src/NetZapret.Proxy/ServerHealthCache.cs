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
    /// Ведётся самим кэшем, а не тем, кто пишет замер: пишут двое — вкладка
    /// VPN и сторож надзора (с 30.09), — и считать streak в каждом порознь
    /// значило бы завести расходящиеся счётчики.
    /// </para>
    /// </remarks>
    public int Failures { get; init; }

    /// <summary>Исходы последних проверок, новые в конце; не больше <see cref="ServerHealthCache.RecentSize"/>.</summary>
    /// <remarks>
    /// Нужны, чтобы узнать «мигающий» сервер. Счёт промахов подряд его не ловит:
    /// ОБС у SecureWay (замер 29.09) отвечали 2–3 раза из 5 вперемешку, и три
    /// промаха подряд у них не случались.
    /// </remarks>
    public IReadOnlyList<bool> Recent { get; init; } = Array.Empty<bool>();

    /// <summary>
    /// Отвечает через раз: из последних проверок (не меньше пяти) удачных меньше 80 %.
    /// </summary>
    public bool Flaky => Recent.Count >= 5 && Recent.Count(ok => ok) * 5 < Recent.Count * 4;

    public TimeSpan Age => DateTimeOffset.Now - CheckedAt;
}

/// <remarks>
/// <para>
/// Пишут в файл двое: окно (замеры вкладки VPN) и надзор (проверки сторожа,
/// с 30.09). Каждый держит свою копию в памяти, и прежде сохранение писало
/// её целиком — чужие записи, сделанные после чтения, пропадали молча.
/// Поэтому копия помнит не только итог, но и что в неё записали
/// (<see cref="_pending"/>), а <see cref="Save"/> под общей блокировкой
/// перечитывает файл и повторяет свои записи поверх — как журнал, а не как
/// снимок. Запись — через временный файл: читающий не увидит обрезанного.
/// </para>
/// </remarks>
public sealed class ServerHealthCache
{
    private readonly Dictionary<string, ServerHealth> _entries;

    /// <summary>Записи этой копии, ещё не сохранённые, — в порядке поступления.</summary>
    private readonly List<ServerHealth> _pending = [];

    /// <summary>Чем ограничил <see cref="KeepOnly"/>; <c>null</c> — не ограничивал.</summary>
    private HashSet<string>? _keep;

    private ServerHealthCache(Dictionary<string, ServerHealth> entries)
    {
        _entries = entries;
    }

    /// <summary>
    /// Общая для окна и надзора блокировка файла. Local — сессия одна:
    /// надзор запускается окном под тем же пользователем.
    /// </summary>
    private const string GateName = @"Local\NetZapret.ServerHealth";

    /// <summary>
    /// Берёт блокировку; не дождался за пару секунд — работает без неё.
    /// </summary>
    /// <remarks>
    /// Замеры — вспомогательные цифры: лучше изредка потерять одну запись,
    /// чем повесить сторожа или окно на чужой блокировке.
    /// </remarks>
    private static IDisposable Gate()
    {
        var mutex = new Mutex(false, GateName);

        try
        {
            if (mutex.WaitOne(TimeSpan.FromSeconds(2)))
                return new Held(mutex);
        }
        catch (AbandonedMutexException)
        {
            // Прежний владелец умер, держа блокировку, — теперь она наша.
            return new Held(mutex);
        }

        mutex.Dispose();
        return new Held(null);
    }

    private sealed class Held(Mutex? mutex) : IDisposable
    {
        public void Dispose()
        {
            if (mutex is null)
                return;

            mutex.ReleaseMutex();
            mutex.Dispose();
        }
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
        using var gate = Gate();

        return new ServerHealthCache(Read(path ?? DefaultPath));
    }

    private static Dictionary<string, ServerHealth> Read(string target)
    {
        if (!File.Exists(target))
            return new(StringComparer.Ordinal);

        try
        {
            var list = JsonSerializer.Deserialize<List<ServerHealth>>(File.ReadAllText(target), Options);

            return list?.ToDictionary(e => e.Tag, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        }
        catch (Exception)
        {
            // Испорченный или несовместимый файл — не повод падать:
            // это всего лишь запомненные цифры, они соберутся заново.
            return new(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Перечитывает файл, сохраняя свои несохранённые записи поверх.
    /// </summary>
    /// <remarks>
    /// Для вкладки, которая открыта долго: сторож пишет каждую минуту,
    /// и без перечитывания вкладка показывала бы замер часовой давности.
    /// </remarks>
    public void Reload(string? path = null)
    {
        Dictionary<string, ServerHealth> fresh;

        using (Gate())
            fresh = Read(path ?? DefaultPath);

        Replay(fresh);
        Replace(fresh);
    }

    /// <summary>Повторяет свои записи и чистку поверх прочитанного с диска.</summary>
    private void Replay(Dictionary<string, ServerHealth> entries)
    {
        foreach (var health in _pending)
            Apply(entries, health);

        if (_keep is { } keep)
        {
            foreach (var tag in entries.Keys.Where(t => !keep.Contains(t)).ToList())
                entries.Remove(tag);
        }
    }

    private void Replace(Dictionary<string, ServerHealth> entries)
    {
        _entries.Clear();

        foreach (var (tag, health) in entries)
            _entries[tag] = health;
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
        Apply(_entries, health);
        _pending.Add(health);
    }

    private static void Apply(Dictionary<string, ServerHealth> entries, ServerHealth health)
    {
        var previous = entries.GetValueOrDefault(health.Tag);
        int before = previous?.Failures ?? 0;

        var recent = (previous?.Recent ?? Array.Empty<bool>())
            .Append(health.Success)
            .TakeLast(RecentSize)
            .ToList();

        entries[health.Tag] = health with
        {
            Failures = health.Success ? 0 : before + 1,
            Recent = recent,
        };
    }

    /// <summary>Сколько последних исходов помнить.</summary>
    public const int RecentSize = 10;

    /// <summary>Отвечающие через раз (<see cref="ServerHealth.Flaky"/>) — мимо автоподбора, как мёртвые.</summary>
    public IReadOnlyCollection<string> Flaky() =>
        _entries.Values.Where(e => e.Flaky).Select(e => e.Tag).ToList();

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

    /// <summary>Последняя задержка ответивших серверов, мс — по ней автоподбор берёт лучших с входа.</summary>
    public IReadOnlyDictionary<string, double> Latencies() =>
        _entries.Values
            .Where(e => e.Success && e.LatencyMs is not null)
            .ToDictionary(e => e.Tag, e => e.LatencyMs!.Value, StringComparer.Ordinal);

    /// <summary>Оставляет только те записи, что относятся к нынешним серверам.</summary>
    /// <remarks>
    /// Подписка меняется, и без чистки файл копил бы записи об исчезнувших
    /// серверах, а по ним потом судили бы о свежести всего списка.
    /// </remarks>
    public void KeepOnly(IEnumerable<string> tags)
    {
        var keep = new HashSet<string>(tags, StringComparer.Ordinal);

        _keep = _keep is null ? keep : _keep.Intersect(keep).ToHashSet(StringComparer.Ordinal);

        foreach (var tag in _entries.Keys.Where(t => !keep.Contains(t)).ToList())
            _entries.Remove(tag);
    }

    /// <summary>
    /// Сохраняет свои записи поверх того, что в файле сейчас.
    /// </summary>
    /// <remarks>
    /// Не снимок копии, а её записи, повторённые на свежепрочитанном файле:
    /// так проверки сторожа, пришедшие после того, как окно прочитало файл,
    /// не затираются замером вкладки — и наоборот.
    /// </remarks>
    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;

        try
        {
            using var gate = Gate();

            var merged = Read(target);
            Replay(merged);

            var directory = Path.GetDirectoryName(Path.GetFullPath(target));

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Через временный файл: читающий без блокировки (или не дождавшийся
            // её) увидит прежний файл или новый, но не обрезанный.
            var temporary = target + ".tmp";

            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(merged.Values.ToList(), Options),
                new UTF8Encoding(false));

            File.Move(temporary, target, overwrite: true);

            Replace(merged);
            _pending.Clear();
            _keep = null;
        }
        catch (Exception)
        {
            // Не сохранилось — переживём: в следующий раз проверим заново.
            // Свои записи копия помнит и повторит при следующем сохранении.
        }
    }
}
