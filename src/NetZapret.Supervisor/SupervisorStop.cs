namespace NetZapret.Supervisor;

/// <summary>
/// Просьба к супервизору остановиться самому — именованным событием Windows.
/// </summary>
/// <remarks>
/// <para>
/// Окно гасило супервизор <c>Kill</c> всем деревом, а сам он работал
/// с <c>CancellationToken.None</c>: штатно остановиться ему было нечем.
/// Так мимо проходила и штатная остановка sing-box (<see cref="ConsoleInterrupt"/>,
/// <c>dc3d17e</c>), ради которой та и делалась: движок умирал вместе
/// с супервизором, не дописав файл кэша, — проверка 03.11 по журналу
/// владельца, перезапуск из окна в 15:47 после сборки с этой правкой.
/// </para>
/// <para>
/// Теперь окно сперва поднимает событие; супервизор отменяет свою работу,
/// и <see cref="ProcessSupervisor.RunAsync"/> гасит службы своим порядком.
/// Нет события (супервизор прежней сборки) или он не вышел в срок — окно
/// убивает его, как раньше.
/// </para>
/// </remarks>
public static class SupervisorStop
{
    /// <summary>Держится полем: событие живёт, пока жив процесс, иначе его некому поднять.</summary>
    private static EventWaitHandle? _listening;

    private static string Name(int processId) => $@"Local\NetZapret.Supervisor.Stop.{processId}";

    /// <summary>Токен, который отменится, когда этот процесс попросят остановиться.</summary>
    public static CancellationToken Listen()
    {
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, Name(Environment.ProcessId));
        var source = new CancellationTokenSource();

        ThreadPool.RegisterWaitForSingleObject(stop, (_, _) => source.Cancel(), null, Timeout.Infinite, executeOnlyOnce: true);
        _listening = stop;

        return source.Token;
    }

    /// <summary>Просит процесс остановиться; <c>false</c> — он не слушает (или спросить не вышло).</summary>
    public static bool Ask(int processId)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Name(processId), out var stop))
                return false;

            using (stop)
                return stop.Set();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
