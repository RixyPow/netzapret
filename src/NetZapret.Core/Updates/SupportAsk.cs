namespace NetZapret.Core.Updates;

/// <summary>
/// Карточка «Нравится NetZapret?» вверху «Главной» — только когда её включает владелец.
/// </summary>
/// <remarks>
/// <para>
/// С 01.10 по 08.11 она стояла у всех после мастера, пока человек её не скроет.
/// С 08.11 (решение владельца) просьба о звезде и донате — уведомлением сбоку
/// после каждого обновления (<see cref="UpdateGreeting"/>), а карточка встаёт
/// только по его слову, к определённому выпуску.
/// </para>
/// <para>
/// Включить — вписать в <see cref="Campaign"/> имя показа, например номер выпуска:
/// карточка встанет у всех, кто не скрыл именно этот показ, — скрывшие прежний
/// тоже её увидят. «Скрыть» запоминает имя показа (<see cref="AppSettings.SupportCardHiddenFor"/>).
/// </para>
/// </remarks>
public static class SupportAsk
{
    /// <summary>Включённый показ карточки; <c>null</c> — карточки нет ни у кого.</summary>
    public const string? Campaign = null;

    /// <summary>Стоит ли карточка на «Главной» при включённом сейчас показе.</summary>
    public static bool Shown(AppSettings settings) => Shown(settings, Campaign);

    /// <summary>Стоит ли карточка при показе <paramref name="campaign"/>.</summary>
    public static bool Shown(AppSettings settings, string? campaign) =>
        campaign is not null
        && settings.OnboardingDone
        && !string.Equals(settings.SupportCardHiddenFor, campaign, StringComparison.Ordinal);

    /// <summary>Настройки после «Скрыть» — до следующего показа.</summary>
    public static AppSettings Hide(AppSettings settings) => Hide(settings, Campaign);

    /// <summary>Настройки после «Скрыть» при показе <paramref name="campaign"/>.</summary>
    public static AppSettings Hide(AppSettings settings, string? campaign) =>
        settings with { SupportCardHidden = true, SupportCardHiddenFor = campaign };
}
