namespace NetZapret.Core.Connections;

/// <summary>Что изменилось с прошлого взгляда.</summary>
public enum PresenceChange
{
    None,

    /// <summary>Запустилась первая из программ.</summary>
    Appeared,

    /// <summary>Ни одной не видно дольше запаса.</summary>
    Gone,
}

/// <summary>
/// Запущена ли хоть одна программа из маршрутов с автопополнением.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 19.10: наблюдение шло весь день — окно начинало его при каждом
/// запуске, если у какой-то программы включено автопополнение (у него —
/// REPO.exe), хотя сама игра могла не запускаться вовсе: за утро 11,7 тыс.
/// соединений в журнале. Он спросил, кто его включает. Теперь наблюдение
/// ради автопополнения идёт, пока программа запущена.
/// </para>
/// <para>
/// Выход считается не сразу, а через <see cref="Grace"/>: игра перезапускается
/// после обновления, лаунчер передаёт место клиенту, и каждое такое мгновение
/// без процесса обрывало бы наблюдение посреди игры.
/// </para>
/// </remarks>
public sealed class ProgramPresence
{
    /// <summary>Сколько программы может не быть, прежде чем счесть её закрытой.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;

    /// <summary>Видна ли сейчас хоть одна (с учётом запаса).</summary>
    public bool Present { get; private set; }

    /// <summary>Какая из программ видна последней — для слов «запущена REPO.exe».</summary>
    public string? Seen { get; private set; }

    /// <summary>
    /// Сверяет запущенное с нужным.
    /// </summary>
    /// <param name="running">Имена запущенных процессов, как их даёт Windows: без «.exe».</param>
    /// <param name="wanted">Программы маршрутов с автопополнением: «REPO.exe».</param>
    public PresenceChange Update(IEnumerable<string> running, IReadOnlyCollection<string> wanted, DateTimeOffset now)
    {
        var names = new HashSet<string>(running.Select(r => r + ".exe"), StringComparer.OrdinalIgnoreCase);
        var found = wanted.FirstOrDefault(names.Contains);

        if (found is not null)
        {
            _lastSeen = now;
            Seen = found;

            if (Present)
                return PresenceChange.None;

            Present = true;
            return PresenceChange.Appeared;
        }

        if (Present && now - _lastSeen >= Grace)
        {
            Present = false;
            return PresenceChange.Gone;
        }

        return PresenceChange.None;
    }
}
