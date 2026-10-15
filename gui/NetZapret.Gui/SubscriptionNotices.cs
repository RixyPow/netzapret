using NetZapret.Subscriptions;

namespace NetZapret.Gui;

/// <summary>
/// Напоминание о конце срока подписки — уведомлением от значка в трее.
/// </summary>
/// <remarks>
/// Как в Happ: за три дня до конца и после него, не чаще раза в сутки
/// (<see cref="SubscriptionReminders"/>). Уведомлением, а не плашкой: подписки
/// читаются и при запуске движков, когда окна может не быть вовсе (<c>--tray</c>).
/// Во вкладке VPN тот же срок виден строкой подписки всегда.
/// </remarks>
internal static class SubscriptionNotices
{
    public static void RemindExpiry(string name, string url, SubscriptionInfo info)
    {
        // Без значка показать негде — напоминание не тратится.
        if (TrayIcon.Current is not { } tray || info.ExpiryWarningText(DateTimeOffset.Now) is not { } warning)
            return;

        if (!SubscriptionReminders.Due(url, DateTimeOffset.Now))
            return;

        var link = info.WebPageUrl ?? info.SupportUrl;

        tray.Notify(
            $"«{name}»: {warning}",
            link is not null
                ? "Нажмите, чтобы открыть страницу поставщика и продлить."
                : "Продлите у поставщика, иначе её серверы перестанут пускать.",
            link);

        Journal.Write("подписка", $"«{name}»: {warning}");
    }
}
