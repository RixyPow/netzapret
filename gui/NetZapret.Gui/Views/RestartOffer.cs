using System.Windows;
using System.Windows.Controls;

namespace NetZapret.Gui.Views;

/// <summary>
/// Короткий путь до уведомления «перезапустить движки».
/// </summary>
/// <remarks>
/// Разделы не знают про главное окно и знать не должны; здесь одна строка,
/// которая молча ничего не делает, если окна нет — например, когда раздел
/// создан вне его.
/// </remarks>
public static class RestartOffer
{
    public static void Offer(this UserControl view, string what) =>
        (Window.GetWindow(view) as MainWindow
            // Раздел в своём окне (журнал, 07.10) — предложение показывает главное.
            ?? Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault())
        ?.OfferRestart(what);
}
