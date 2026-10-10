using System.Runtime.CompilerServices;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Etw;
using NetZapret.Zapret;

namespace NetZapret.Supervisor;

/// <summary>
/// Сеанс наблюдения: события ядра о соединениях, решение правил по каждому
/// и запись в журнал <c>runtime\watch.log</c>.
/// </summary>
/// <remarks>
/// <para>
/// Одно на окно и <c>nz watch</c>. Прежде всё это жило в разделе окна,
/// журнала не было, а в nz наблюдения не было вовсе — консоль с ним
/// удалена 23.09 (владелец 10.10: «добавь в nz наблюдение», «пусть
/// попадает» в отчёт).
/// </para>
/// <para>
/// В журнал идёт каждое соединение, и прямое тоже, — отбор окна («только
/// туннель и десинк», одна программа) касается только показа: разбирают
/// журнал потом, и что окажется нужным, заранее не знает никто.
/// </para>
/// <para>
/// Нужны права администратора: сессия ETW ядра без них не создаётся.
/// </para>
/// </remarks>
public sealed class ConnectionWatch : IAsyncDisposable
{
    /// <summary>Журнал наблюдения относительно корня установки.</summary>
    public static readonly string DefaultJournal = Path.Combine("runtime", "watch.log");

    /// <summary>
    /// Предел журнала: 4 МБ и одно прошлое поколение.
    /// </summary>
    /// <remarks>
    /// Строка — около 120 байт, так что это ~35 тысяч соединений в файле,
    /// около часа браузера с десятком вкладок; вдвое — с прошлым поколением.
    /// Оба файла едут в отчёт целиком.
    /// </remarks>
    private const long JournalBytes = 4 * 1024 * 1024;

    private readonly EtwConnectionSource _source;
    private readonly RuleEngine _engine;
    private readonly RollingLog? _journal;

    private long _total;
    private long _matched;
    private bool _disposed;

    private ConnectionWatch(EtwConnectionSource source, RuleEngine engine, RollingLog? journal, string mode)
    {
        _source = source;
        _engine = engine;
        _journal = journal;
        Mode = mode;
    }

    /// <summary>Режим работы словами — на момент начала.</summary>
    public string Mode { get; }

    /// <summary>Сколько правил смотрится.</summary>
    public int RuleCount => _engine.RuleSet.Rules.Count;

    public long Total => Interlocked.Read(ref _total);

    public long Matched => Interlocked.Read(ref _matched);

    /// <summary>Сколько пар «адрес — имя» узнано из DNS.</summary>
    public int NamesKnown => _source.DnsNames.Count;

    /// <summary>Сколько событий потеряно при переполнении очереди.</summary>
    public long Dropped => _source.DroppedCount;

    /// <summary>
    /// Начинает наблюдение по тем же правилам, по которым собирается конфиг.
    /// </summary>
    /// <param name="journal">Куда писать; <c>null</c> — не писать.</param>
    /// <exception cref="Exception">Сессия ETW не создалась — обычно нет прав администратора.</exception>
    public static ConnectionWatch Start(AppSettings settings, string? journal)
    {
        // Оба слоя правил, как у сборки конфига: наблюдатель показывает, какое
        // правило применилось бы, и читать он обязан ровно то же. Иначе он
        // не диагностика, а источник ложных выводов.
        var engine = RuleSetLoader.LoadFor(settings);

        RuleSetExpander.Expand(engine.RuleSet, ZapretPaths.Discover()?.Root);

        var source = new EtwConnectionSource(new EtwConnectionSourceOptions
        {
            SkipLoopback = true,
            ObserveDns = true,
        });

        try
        {
            source.Start();
        }
        catch
        {
            source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        var log = journal is null ? null : new RollingLog(journal, JournalBytes, generations: 1);
        var watch = new ConnectionWatch(source, engine, log, settings.DescribeMode());

        log?.AppendLine(WatchEntry.Session(DateTimeOffset.Now,
            $"{WatchEntry.Started}: режим «{watch.Mode}», правил {watch.RuleCount}"));

        return watch;
    }

    /// <summary>Соединения по мере появления, с решением правил; каждое — в журнал.</summary>
    public async IAsyncEnumerable<WatchEntry> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IAsyncEnumerator<ConnectionEvent> events = _source.ReadEventsAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                try
                {
                    if (!await events.MoveNextAsync())
                        yield break;
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }

                var connection = events.Current;
                var decision = _engine.Evaluate(connection);

                Interlocked.Increment(ref _total);

                if (decision.Rule is not null)
                    Interlocked.Increment(ref _matched);

                var entry = WatchEntry.From(connection, decision);

                _journal?.AppendLine(entry.ToLine());

                yield return entry;
            }
        }
        finally
        {
            await events.DisposeAsync();
        }
    }

    /// <summary>
    /// Останавливает сессию — синхронно и до конца.
    /// </summary>
    /// <remarks>
    /// Сессия ETW переживает процесс, и брошенная она останется в системе
    /// до перезагрузки.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _source.DisposeAsync();

        _journal?.AppendLine(WatchEntry.Session(DateTimeOffset.Now,
            $"наблюдение остановлено: соединений {Total}, под правило {Matched}, потеряно {Dropped}"));
        _journal?.Dispose();
    }
}
