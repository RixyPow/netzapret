using System.Windows;
using NetZapret.Proxy;

namespace NetZapret.Gui.Views;

/// <summary>
/// Выбрали «через VPN», а имена прибиты в hosts, — предложить снять пин.
/// </summary>
/// <remarks>
/// <para>
/// Пин бьёт любое разрешение имени, и прибитое имя не получает адрес туннеля:
/// маршрут «через VPN» на нём молча не действует. 23.09 владелец так
/// и споткнулся на crunchyroll — VPN выбран, а 1009 остаётся, потому что имена
/// прибиты с прошлой попытки. Узнать это можно было только разбором.
/// </para>
/// <para>
/// Спрашиваем, а не снимаем сами: пин мог быть поставлен нарочно, и снять его
/// значит поменять, куда пойдёт имя при выключенном туннеле. Решение владельца
/// 23.09 — предлагать в миг выбора VPN.
/// </para>
/// </remarks>
internal static class PinConflict
{
    /// <summary>
    /// Спрашивает о прибитых под зонами именах; возвращает, что сказать в строке
    /// состояния, или <c>null</c>, если спрашивать было не о чем.
    /// </summary>
    public static string? Offer(Window? owner, IEnumerable<string> zones)
    {
        IReadOnlyList<string> pinned;

        try
        {
            pinned = HostsEditor.PinnedUnder(zones);
        }
        catch (Exception)
        {
            return null;
        }

        if (pinned.Count == 0)
            return null;

        var shown = string.Join(", ", pinned.Take(5)) + (pinned.Count > 5 ? $" и ещё {pinned.Count - 5}" : string.Empty);

        var answer = MessageBox.Show(
            owner ?? Application.Current.MainWindow,
            $"В файле hosts прибиты имена: {shown}.\n\n"
            + "Пин перебивает любой маршрут: прибитое имя не получит адрес туннеля "
            + "и уйдёт мимо VPN.\n\nСнять пин с них?",
            "Пин мешает VPN",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.Yes);

        if (answer != MessageBoxResult.Yes)
        {
            return $"Пин оставлен: {pinned.Count} имён пойдут по прибитому адресу, "
                + "а не через VPN. Снять можно в «Файле hosts».";
        }

        try
        {
            var result = HostsEditor.Unpin(pinned);
            HostsEditor.FlushDns();

            return result.Reverted ?? $"Пин снят с {pinned.Count} имён — теперь они пойдут через VPN.";
        }
        catch (Exception ex)
        {
            return "Пин снять не удалось: " + ex.GetBaseException().Message;
        }
    }
}
