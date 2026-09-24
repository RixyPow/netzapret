using System.Threading;

namespace NetZapret.Gui;

/// <summary>
/// Держит интерфейс в одном экземпляре.
/// </summary>
/// <remarks>
/// <para>
/// Без этого автозапуск и запуск руками дают две иконки в трее и два окна,
/// каждое со своим опросом состояния. Второй экземпляр вместо своего окна
/// показывает окно первого и уходит.
/// </para>
/// <para>
/// Роль супервизора сюда не входит намеренно: это та же программа с ключом
/// <see cref="SupervisorHost.Switch"/>, и попади она под ту же проверку —
/// движки не поднялись бы, пока открыто окно.
/// </para>
/// </remarks>
internal static class SingleInstance
{
    // Local, а не Global: сеанс у интерфейса и так один — он работает
    // от администратора того же вошедшего человека.
    private const string Held = @"Local\NetZapret.Gui.interface";
    private const string Wake = @"Local\NetZapret.Gui.interface.show";
    private const string Leave = @"Local\NetZapret.Gui.interface.quit";

    /// <summary>
    /// Ключ: погасить движки и закрыть работающий интерфейс вместе с треем.
    /// </summary>
    /// <remarks>
    /// Для <c>build.cmd</c>. <c>--stop</c> гасит только движки, а окно,
    /// спрятанное крестиком в трей, держит выложенные сборки — и сборка
    /// каждый раз останавливалась на «NetZapret is still running», пока
    /// владелец не закроет трей руками (жалоба 24.09). Отдельным ключом,
    /// а не расширением <c>--stop</c>: погасить движки для разбора, не трогая
    /// открытое окно, по-прежнему законное желание.
    /// </remarks>
    public const string QuitSwitch = "--quit";

    private static Mutex? _mutex;
    private static EventWaitHandle? _wake;
    private static EventWaitHandle? _leave;
    private static RegisteredWaitHandle? _watch;
    private static RegisteredWaitHandle? _leaveWatch;

    /// <summary>
    /// Занимает место единственного интерфейса.
    /// </summary>
    /// <returns><c>false</c> — место уже занято другим экземпляром.</returns>
    public static bool Claim()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, Held, out var first);

            if (!first)
                return false;

            _wake = new EventWaitHandle(false, EventResetMode.AutoReset, Wake);
            _leave = new EventWaitHandle(false, EventResetMode.AutoReset, Leave);
            return true;
        }
        catch (Exception)
        {
            // Не вышло завести имя — не повод не запуститься. Худшее,
            // что случится, — два окна, и это лучше, чем ни одного.
            return true;
        }
    }

    /// <summary>Просит уже работающий экземпляр показать своё окно.</summary>
    public static void RequestShow()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Wake, out var wake))
                return;

            wake.Set();
            wake.Dispose();
        }
        catch (Exception)
        {
            // Первый экземпляр мог уйти между проверкой и сигналом.
        }
    }

    /// <summary>Отзывается, когда второй экземпляр просит показать окно.</summary>
    public static void OnShowRequested(Action show)
    {
        if (_wake is null)
            return;

        _watch = ThreadPool.RegisterWaitForSingleObject(
            _wake,
            (_, _) => show(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>
    /// Просит работающий интерфейс выйти и ждёт, пока он отпустит своё место.
    /// </summary>
    /// <returns><c>true</c> — интерфейса нет или он вышел; <c>false</c> — не дождались.</returns>
    /// <remarks>
    /// Ждём по мьютексу, а не по времени: интерфейс держит его до самого
    /// выхода, и отпущенный — единственный честный знак, что он ушёл. Брошенный
    /// (процесс умер, не отпустив) — тоже знак, и тоже ушёл.
    /// </remarks>
    public static bool RequestQuit(TimeSpan wait)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(Leave, out var leave))
                return true;

            leave.Set();
            leave.Dispose();

            if (!Mutex.TryOpenExisting(Held, out var held))
                return true;

            using (held)
            {
                bool owned;

                try
                {
                    owned = held.WaitOne(wait);
                }
                catch (AbandonedMutexException)
                {
                    owned = true;
                }

                if (owned)
                    held.ReleaseMutex();

                return owned;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Отзывается, когда кто-то просит интерфейс выйти.</summary>
    public static void OnQuitRequested(Action quit)
    {
        if (_leave is null)
            return;

        _leaveWatch = ThreadPool.RegisterWaitForSingleObject(
            _leave,
            (_, _) => quit(),
            state: null,
            Timeout.Infinite,
            executeOnlyOnce: true);
    }

    public static void Release()
    {
        _watch?.Unregister(null);
        _leaveWatch?.Unregister(null);
        _wake?.Dispose();
        _leave?.Dispose();

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch (Exception)
        {
            // Мы им и не владели — например, когда место было занято.
        }

        _mutex?.Dispose();
    }
}
