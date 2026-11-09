using System.Windows;

namespace NetZapret.Gui;

/// <summary>
/// Говорит элементу, на экране ли он: в окне, окно показано и не свёрнуто.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UIElement.IsVisible"/> одного этого не знает: у свёрнутого окна он
/// остаётся <c>true</c> — меняется только когда окно прячут (<c>Hide</c>). Замер
/// 09.11: волна «Главной» (<see cref="TunnelWave"/>) следила за <c>IsVisible</c>
/// и в свёрнутом окне рисовалась как в открытом — 15 % ядра на стенде
/// (11 % — поток окна), у владельца в работающей программе 7 % (окно свёрнуто,
/// 1354 с процессора за 4 ч 40 мин). Спрятанному в трей окну доставалось 0.
/// </para>
/// <para>
/// Перекрыто ли окно другим — не смотрит: Windows этого просто не сообщает,
/// а на втором мониторе окно может быть и не активным, и на виду.
/// </para>
/// </remarks>
internal static class OnScreen
{
    /// <summary>
    /// Зовёт <paramref name="changed"/> всякий раз, когда элемент появляется
    /// на экране или уходит с него; первое состояние — «не на экране».
    /// </summary>
    public static void Watch(FrameworkElement element, Action<bool> changed)
    {
        Window? window = null;
        bool seen = false;

        void Tell()
        {
            bool now = element.IsLoaded && element.IsVisible && window is not { WindowState: WindowState.Minimized };

            if (now == seen)
                return;

            seen = now;
            changed(now);
        }

        void OnState(object? sender, EventArgs e) => Tell();

        void Leave()
        {
            if (window is not null)
                window.StateChanged -= OnState;

            window = null;
        }

        element.Loaded += (_, _) =>
        {
            // Loaded приходит и повторно, без Unloaded между ними (смена темы):
            // вторая подписка на то же окно звала бы дважды.
            Leave();
            window = Window.GetWindow(element);

            if (window is not null)
                window.StateChanged += OnState;

            Tell();
        };

        element.Unloaded += (_, _) =>
        {
            Leave();
            Tell();
        };

        element.IsVisibleChanged += (_, _) => Tell();
    }
}
