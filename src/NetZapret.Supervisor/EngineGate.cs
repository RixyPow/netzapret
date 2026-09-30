namespace NetZapret.Supervisor;

/// <summary>
/// Очередь запусков и остановок движков: по одному за раз.
/// </summary>
/// <remarks>
/// <para>
/// Отчёт reaass, 01.10. Запуск ждёт подписку до двенадцати секунд, и всё это
/// время кнопки живы. «Запустить», нажатое посреди перезапуска, подняло второй
/// супервизор; «Выход» в трее посреди перезапуска погасил прежний, а запуск,
/// дождавшись подписки, поднял новый уже после выхода — и тот жил без окна.
/// </para>
/// <para>
/// Отсюда три правила. Операции идут по одной. Запуск, дождавшийся очереди
/// за другим удавшимся запуском, лишний — его не повторяют (<see cref="Turn.Superseded"/>).
/// Остановка отменяет запуски, попросившие очереди раньше неё
/// (<see cref="Turn.Token"/>): человек, нажавший «Остановить» или «Выход»
/// после «Запустить», хочет остановленные движки, а не поднятые следом.
/// </para>
/// </remarks>
public sealed class EngineGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private CancellationTokenSource _stop = new();
    private int _starts;

    /// <summary>Очередь на запуск.</summary>
    public async Task<Turn> StartAsync(CancellationToken cancellationToken)
    {
        int seen = Volatile.Read(ref _starts);
        var stop = StopToken();

        await _gate.WaitAsync(cancellationToken);

        return new Turn(this, stop, cancellationToken, superseded: Volatile.Read(ref _starts) != seen);
    }

    /// <summary>Очередь на перезапуск: как запуск, но не лишний никогда — он несёт новые настройки.</summary>
    public async Task<Turn> RestartAsync(CancellationToken cancellationToken)
    {
        var stop = StopToken();

        await _gate.WaitAsync(cancellationToken);

        return new Turn(this, stop, cancellationToken, superseded: false);
    }

    /// <summary>Очередь на остановку; запуски, попросившие очереди раньше, отменяются.</summary>
    public async Task<Turn> StopAsync(CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _stop.Cancel();
            _stop = new CancellationTokenSource();
        }

        await _gate.WaitAsync(cancellationToken);

        return new Turn(this, CancellationToken.None, cancellationToken, superseded: false);
    }

    /// <summary>Запуск поднял супервизор: ждущие за ним запуски лишние.</summary>
    public void Started() => Interlocked.Increment(ref _starts);

    private CancellationToken StopToken()
    {
        lock (_sync)
            return _stop.Token;
    }

    /// <summary>Своя очередь; освобождается <see cref="Dispose"/>.</summary>
    public sealed class Turn : IDisposable
    {
        private readonly EngineGate _owner;
        private readonly CancellationTokenSource _linked;
        private int _released;

        internal Turn(EngineGate owner, CancellationToken stop, CancellationToken caller, bool superseded)
        {
            _owner = owner;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(stop, caller);
            Superseded = superseded;
        }

        /// <summary>Отменяется остановкой, попросившей очереди позже, либо самим вызывающим.</summary>
        public CancellationToken Token => _linked.Token;

        /// <summary>Пока ждали очереди, движки поднял другой запуск.</summary>
        public bool Superseded { get; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            _linked.Dispose();
            _owner._gate.Release();
        }
    }
}
