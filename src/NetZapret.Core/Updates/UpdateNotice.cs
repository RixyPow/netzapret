using System.Text.RegularExpressions;

namespace NetZapret.Core.Updates;

/// <summary>
/// Найденное при запуске обновление — для карточки на «Главной» и точки у «Ещё».
/// </summary>
/// <remarks>
/// <para>
/// Выключатель «Искать обновления при запуске» с удаления консоли 23.09
/// не делал ничего: проверяла при запуске она, окно — только по кнопке
/// в «Ещё». Человек на 0.7.1 узнал бы о 0.7.2, лишь зайдя туда сам.
/// </para>
/// <para>
/// Решение владельца 23.09: карточка на «Главной» и точка у пункта «Ещё»,
/// без уведомлений Windows — выпуски выходят часто, и звать о каждом
/// из трея было бы назойливо. Установка — по-прежнему отдельным действием.
/// </para>
/// </remarks>
public static class UpdateNotice
{
    private static ReleaseInfo? _available;

    /// <summary>Новее установленной; <c>null</c> — не искали или ставить нечего.</summary>
    public static ReleaseInfo? Available
    {
        get => _available;
        private set
        {
            _available = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Нашлось или пропало обновление. Зовётся не из окна — переходить в его поток самим.</summary>
    public static event Action? Changed;

    /// <summary>Спрашивает GitHub, если это разрешено настройками.</summary>
    public static async Task CheckAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.CheckForUpdates)
            return;

        Remember(await UpdateCheck.LatestAsync(cancellationToken));
    }

    /// <summary>
    /// Запоминает ответ GitHub — и проверки при запуске, и кнопки «Проверить»:
    /// точка у «Ещё» и карточка должны знать о находке, откуда бы та ни пришла.
    /// </summary>
    /// <remarks>Не ответил — прежняя находка остаётся: молчание сети не отменяет выпуск.</remarks>
    public static void Remember(ReleaseInfo? release)
    {
        if (release is null)
            return;

        Available = UpdateCheck.IsNewer(release.Version, UpdateCheck.Current) ? release : null;
    }

    /// <summary>Показывать ли карточку: есть новее и о нём не сказали «не сейчас».</summary>
    public static bool ShouldOffer(ReleaseInfo? release, AppSettings settings) =>
        release is not null
        && !string.Equals(release.Version, settings.DismissedUpdate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Главное из «Что нового» — заголовки первых пунктов раздела «Новое».
    /// </summary>
    /// <remarks>
    /// Чейнджлог пишется так, что каждый пункт начинается с полужирной
    /// фразы (CLAUDE.md, «Чейнджлог»): её одной хватает, чтобы решить,
    /// стоит ли обновляться, а целиком примечания открываются по ссылке.
    /// Нет раздела «Новое» — берутся первые полужирные фразы вообще.
    /// </remarks>
    public static IReadOnlyList<string> Highlights(string? notes, int count = 3)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return [];

        var text = notes;
        var fresh = text.IndexOf("## Новое", StringComparison.OrdinalIgnoreCase);

        if (fresh >= 0)
        {
            text = text[fresh..];
            var next = text.IndexOf("\n## ", 3, StringComparison.Ordinal);

            if (next > 0)
                text = text[..next];
        }

        return Regex.Matches(text, @"\*\*(.+?)\*\*")
            .Select(m => m.Groups[1].Value.Trim().TrimEnd('.', ':'))
            .Where(s => s.Length > 0)
            .Take(count)
            .ToList();
    }

    /// <summary>Страница выпуска на GitHub — примечания целиком.</summary>
    public static string PageOf(ReleaseInfo release) =>
        $"https://github.com/{UpdateCheck.Repository}/releases/tag/{release.Tag}";
}
