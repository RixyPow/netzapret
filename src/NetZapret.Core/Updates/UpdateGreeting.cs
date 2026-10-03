namespace NetZapret.Core.Updates;

/// <summary>
/// Уведомление после обновления: звезда на GitHub и канал в Telegram.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 03.10: всплывать после каждого обновления — но только у тех, кто
/// скрыл карточку с той же просьбой на «Главной» (01.10). Пока карточка
/// стоит, просьба и так перед глазами, и уведомление рядом было бы ею же
/// дважды. Скрывшему — напоминание один раз на каждую новую версию.
/// </para>
/// <para>
/// Сравниваются три числа версии (<see cref="UpdateCheck.Current"/>), без
/// номера сборки: своя пересборка обновлением не считается. Не показывается
/// после мастера первого запуска — человек только поставил программу, это
/// не обновление; запуск при этом всё равно запоминается, иначе следующий
/// запуск той же версии принял бы её за новую.
/// </para>
/// </remarks>
public static class UpdateGreeting
{
    /// <summary>Показать ли уведомление при этом запуске.</summary>
    /// <param name="current">Версия, запущенная сейчас.</param>
    public static bool Due(AppSettings settings, string current) =>
        settings.OnboardingDone
        && settings.SupportCardHidden
        && !string.Equals(settings.LastRunVersion, current, StringComparison.Ordinal);

    /// <summary>Настройки с запомненной версией — после показа или его пропуска.</summary>
    public static AppSettings Seen(AppSettings settings, string current) =>
        settings with { LastRunVersion = current };
}
