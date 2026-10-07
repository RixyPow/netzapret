using System.Text.RegularExpressions;

namespace NetZapret.Core.Updates;

/// <summary>Раздел программы, к которому относится часть чейнджлога.</summary>
/// <param name="Key">Тот же ключ, что у пункта бокового меню окна (<c>Tag</c>), — по нему окно берёт значок.</param>
/// <param name="Name">Как раздел называется в меню.</param>
/// <param name="Aliases">Другие написания заголовка, которые тоже его значат.</param>
public sealed record NotesCategory(string Key, string Name, IReadOnlyList<string> Aliases);

/// <summary>Что за изменения под заголовком чейнджлога.</summary>
public enum NotesChange
{
    /// <summary>Заголовок не о виде изменений: раздел программы или подвал.</summary>
    None,

    /// <summary>«Новое».</summary>
    Added,

    /// <summary>«Исправления».</summary>
    Fixed,

    /// <summary>«Удаления».</summary>
    Removed,
}

/// <summary>
/// Разделы чейнджлога — пункты бокового меню окна.
/// </summary>
/// <remarks>
/// <para>
/// С 0.14.0 чейнджлог делится по разделам программы, а внутри — на «Новое»,
/// «Исправления» и «Удаления» (владелец 07.10: «пускай чейнджлоги теперь
/// делятся и по категориям где что-то изменилось», разделы — по меню окна,
/// значки — его же). Человек ищет изменения там, где ими пользуется.
/// </para>
/// <para>
/// Список здесь, а не в окне: по нему разбираются и окно обновления,
/// и счёт пунктов в ленте версий. Что ключи и названия совпадают с меню,
/// сверяет тест окна.
/// </para>
/// </remarks>
public static class NotesCategories
{
    /// <summary>В порядке меню; «Общее» — для того, чего в меню нет: обновление, трей, мастер.</summary>
    public static IReadOnlyList<NotesCategory> All { get; } =
    [
        new("status", "Главная", []),
        new("vpn", "VPN", ["ВПН", "Туннель"]),
        new("desync", "Десинк", []),
        new("routes", "Маршруты", []),
        new("tgproxy", "TG Proxy", ["Прокси Telegram", "Telegram"]),
        new("check", "Проверка блокировок", ["Проверка"]),
        new("speed", "Замер скорости", []),
        new("dns", "DNS", []),
        new("hosts", "Файл hosts", ["hosts"]),
        new("watch", "Наблюдение", []),
        // Журнал с 07.10 — окно из «Диагностики», а не пункт меню.
        new("doctor", "Диагностика", ["Журнал"]),
        new("look", "Оформление", []),
        new("more", "Ещё", []),
        new("general", "Общее", []),
    ];

    /// <summary>Раздел по заголовку; <c>null</c> — заголовок не раздел программы.</summary>
    public static NotesCategory? Find(string heading)
    {
        var name = Clean(heading);

        return All.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            || c.Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Вид изменений по заголовку: «Новое», «Исправления», «Удаления».</summary>
    public static NotesChange ChangeOf(string heading)
    {
        var name = Clean(heading);

        return name.StartsWith("Нов", StringComparison.OrdinalIgnoreCase) ? NotesChange.Added
            : name.StartsWith("Исправ", StringComparison.OrdinalIgnoreCase) ? NotesChange.Fixed
            : name.StartsWith("Удал", StringComparison.OrdinalIgnoreCase) ? NotesChange.Removed
            : NotesChange.None;
    }

    /// <summary>
    /// Заголовок без нумерации и двоеточия: «1.1 Новое:» — «Новое».
    /// </summary>
    /// <remarks>Так владелец набросал образец 07.10; номер в заголовке не мешает его узнать.</remarks>
    public static string Clean(string heading) =>
        Regex.Replace(heading.Trim(), @"^\d+(\.\d+)*\.?\s+", string.Empty).TrimEnd(':').Trim();
}
