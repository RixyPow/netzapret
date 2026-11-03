using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetZapret.Supervisor;

/// <summary>
/// Ctrl+C в консоль дочернего процесса — штатная остановка вместо <c>Kill</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ради sing-box: файл кэша он дописывает до конца только при штатном выходе.
/// Пары «подставной адрес — имя» уходят в файл сразу, а указатель следующего
/// адреса — таймером, который каждый новый адрес откладывает заново, или
/// при закрытии. Нет указателя при запуске — таблица стирается
/// (<c>dns/transport/fakeip/store.go</c>), и после перезапуска имена «через VPN»
/// получают другие адреса, а браузер со старыми в кэше упирается в отказ.
/// Замер 20.10 на 2.7.3: <c>Kill</c> через 2 с после трёх имён — раздача
/// с начала, github получил адрес rutracker.
/// </para>
/// <para>
/// Окно без своей консоли послать Ctrl+C чужому процессу может только из его
/// консоли: подсоединиться к ней, послать, отсоединиться. Сигнал получают все
/// процессы консоли, и мы тоже, — на это время стоит свой обработчик, который
/// его глотает. Глушить Ctrl+C пустым обработчиком (<c>null</c>) нельзя: этот
/// признак наследуют дочерние процессы, и следующий sing-box сигнал бы уже
/// не услышал. Процесс со своей консолью (<c>nz</c>, тесты) к чужой не
/// подсоединится — тогда ответ «не вышло», и остановка идёт прежним путём.
/// </para>
/// </remarks>
public static class ConsoleInterrupt
{
    private const uint CtrlCEvent = 0;

    /// <summary>Одна консоль на процесс: два подсоединения разом невозможны.</summary>
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Держится полем: иначе сборщик мусора уберёт делегат, пока Windows его зовёт.</summary>
    private static readonly HandlerRoutine Swallow = _ => true;

    /// <summary>
    /// Шлёт Ctrl+C и ждёт выхода; <c>true</c> — процесс вышел сам.
    /// </summary>
    public static async Task<bool> StopAsync(Process process, TimeSpan wait, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);

        try
        {
            if (!AttachConsole((uint)process.Id))
                return false;

            try
            {
                SetConsoleCtrlHandler(Swallow, true);

                if (!GenerateConsoleCtrlEvent(CtrlCEvent, 0))
                    return false;

                try
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(wait, cancellationToken);
                }
                catch (TimeoutException)
                {
                }

                return process.HasExited;
            }
            finally
            {
                FreeConsole();

                // Сигнал доставляется своим потоком и может прийти к нам чуть
                // позже — снять обработчик раньше значило бы погасить сигналом себя.
                await Task.Delay(200, CancellationToken.None);
                SetConsoleCtrlHandler(Swallow, false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    private delegate bool HandlerRoutine(uint controlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);
}
