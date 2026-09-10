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

    private static Mutex? _mutex;
    private static EventWaitHandle? _wake;
    private static RegisteredWaitHandle? _watch;

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

    public static void Release()
    {
        _watch?.Unregister(null);
        _wake?.Dispose();

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
