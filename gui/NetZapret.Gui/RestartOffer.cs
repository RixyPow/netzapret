using System.Windows;

namespace NetZapret.Gui;

/// <summary>
/// Предложить перезапуск движков после того, что выучено в фоне, — так, чтобы его увидели.
/// </summary>
/// <remarks>
/// <para>
/// Адреса игры из «Наблюдения» (<see cref="ProgramRouteNotice"/>) и голоса
/// Discord (<see cref="DiscordVoiceWatch"/>) дописываются, пока человек играет
/// или говорит, — то есть когда окна NetZapret он не видит. Плашка в окне
/// гаснет через несколько секунд, а в режиме <c>--tray</c> окна нет вовсе,
/// и предложение пропадало молча. 14.10 в чате игрок в Rainbow Six спросил,
/// нельзя ли без перезапуска, — он перезапускал руками.
/// </para>
/// <para>
/// Окно на экране и в фокусе — плашка в нём, как прежде. Иначе — уведомление
/// от значка в трее (<see cref="TrayIcon.OfferRestart"/>), нажатие перезапускает;
/// плашка в видимом, но не активном окне ставится тоже — увидит, когда вернётся.
/// Без перезапуска на Windows никак: адреса TUN движок берёт при запуске
/// (route_address_set у sing-box — только Linux).
/// </para>
/// </remarks>
internal static class RestartOffer
{
    public static void Show(string what) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        var window = Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
        bool seen = window is { IsVisible: true, IsActive: true } && window.WindowState != WindowState.Minimized;

        if (window is { IsVisible: true } && window.WindowState != WindowState.Minimized)
            window.OfferRestart(what);

        if (!seen)
            TrayIcon.Current?.OfferRestart(what);
    });
}
