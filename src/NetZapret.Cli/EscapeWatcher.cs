namespace NetZapret.Cli;

/// <summary>
/// Следит за клавишей Esc, чтобы долгую работу можно было прервать.
/// </summary>
/// <remarks>
/// <para>
/// Ctrl+C для этого не годится: он валит программу целиком, а из меню это
/// означает потерю и меню, и всего, что проверка успела набрать. Нужна
/// остановка, после которой можно показать итог.
/// </para>
/// <para>
/// Отдельным потоком, а не опросом внутри цикла: цикл почти всё время ждёт
/// ответа от сети, и вставлять в него проверку клавиатуры значит реагировать
/// на нажатие через несколько секунд после него.
/// </para>
/// </remarks>
internal sealed class EscapeWatcher : IDisposable
{
    private readonly CancellationTokenSource _own = new();
    private Thread? _thread;
    private bool _disposed;

    /// <summary>Работает ли слежение — есть ли вообще у кого спрашивать.</summary>
    public bool Armed { get; private init; }

    private EscapeWatcher()
    {
    }

    public static EscapeWatcher Watch(CancellationTokenSource stop)
    {
        // Без консоли читать нечего: перенаправленный ввод отдаст EOF сразу,
        // и слежение превратилось бы в мгновенную отмену на ровном месте.
        if (Console.IsInputRedirected)
            return new EscapeWatcher { Armed = false };

        var watcher = new EscapeWatcher { Armed = true };

        var thread = new Thread(() =>
        {
            try
            {
                while (!watcher._own.IsCancellationRequested && !stop.IsCancellationRequested)
                {
                    if (!Console.KeyAvailable)
                    {
                        // Полсекунды: достаточно, чтобы нажатие ощущалось
                        // мгновенным, и достаточно редко, чтобы не жечь ядро.
                        watcher._own.Token.WaitHandle.WaitOne(500);
                        continue;
                    }

                    if (Console.ReadKey(intercept: true).Key == ConsoleKey.Escape)
                    {
                        stop.Cancel();
                        return;
                    }
                }
            }
            catch (Exception)
            {
                // Консоль могли отобрать, например при перенаправлении на ходу.
                // Тогда прерывать нечем — но и падать не из-за чего.
            }
        })
        {
            IsBackground = true,
            Name = "esc-watch",
        };

        thread.Start();
        watcher._thread = thread;
        return watcher;
    }

    /// <summary>
    /// Останавливает слежение и дожидается, пока поток отпустит клавиатуру.
    /// </summary>
    /// <remarks>
    /// Ожидание обязательно. Без него поток успевает прочитать ещё одну
    /// клавишу уже после того, как мы решили, что он остановлен, — и она
    /// пропадает из ответа на следующий вопрос. Ждём недолго: цикл просыпается
    /// дважды в секунду, и если не уложился, значит застрял в чтении, а тогда
    /// ждать дольше бессмысленно — он всё равно фоновый и умрёт с программой.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _own.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _own.Dispose();
    }
}
