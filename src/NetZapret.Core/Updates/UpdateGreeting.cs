namespace NetZapret.Core.Updates;

/// <summary>
/// Уведомление после обновления: звезда на GitHub, канал в Telegram и донат.
/// </summary>
/// <remarks>
/// <para>
/// После каждого обновления — всем (решение владельца 08.11). С 03.10 по 08.11
/// оно всплывало только у тех, кто скрыл карточку с той же просьбой на «Главной»:
/// пока карточка стояла, просьба и так была перед глазами. С 08.11 карточка
/// по умолчанию не показывается вовсе (<see cref="SupportAsk"/>), а уведомление
/// сбоку и есть просьба — в нём и «Поддержать», которого у карточки нет.
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
        && !string.Equals(settings.LastRunVersion, current, StringComparison.Ordinal);

    /// <summary>Настройки с запомненной версией — после показа или его пропуска.</summary>
    public static AppSettings Seen(AppSettings settings, string current) =>
        settings with { LastRunVersion = current };
}
