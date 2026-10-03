using System.Text.RegularExpressions;

namespace NetZapret.Zapret;

/// <summary>
/// Порядок пресетов в списках: самые новые сверху.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 03.10: «самые новые всегда сверху». Прежде порядок задавался
/// перетаскиванием и хранился в config\preset-order.json, а незнакомые
/// пресеты уходили в конец: V11 Lite, ставший пресетом по умолчанию,
/// стоял у владельца ниже V5 и диагностических копий. Порядок руками
/// и «всегда сверху» вместе не живут — остался один, по новизне.
/// </para>
/// <para>
/// Новизна — номер версии в имени («Universal V11 Lite» — 11,
/// «Default v5 (game filter)» — 5): так пресеты и называют их авторы.
/// Версия из шапки (<c># BuiltinVersion:</c>) — вторым ключом: это версия
/// Zapret GUI, для которой пресет собран, и у наших V5–V11 она одна, 2.26.
/// Дата файла не годится: её переписывает и распаковка архива, и git.
/// Пресеты без номера в имени идут после пронумерованных.
/// </para>
/// </remarks>
public static class PresetNewness
{
    /// <summary>Номер версии в имени: «V11» — 11; нет такого — <c>null</c>.</summary>
    public static int? Number(string name)
    {
        // Буква v отдельным словом или в начале имени: «Universal V11», «v5»,
        // но не «Dev2» и не «multisplit_v2tcp» внутри слова.
        var match = Regex.Match(name, @"(?<![\p{L}\d_])[Vv](\d+)");

        return match.Success && int.TryParse(match.Groups[1].Value, out int number) ? number : null;
    }

    /// <summary>Раскладывает самые новые вперёд.</summary>
    /// <param name="name">Имя пресета — имя файла без расширения.</param>
    /// <param name="builtinVersion">Версия из шапки пресета.</param>
    public static IReadOnlyList<T> Order<T>(
        IEnumerable<T> items,
        Func<T, string> name,
        Func<T, string?> builtinVersion) =>
        items
            .OrderByDescending(item => Number(name(item)) ?? -1)
            .ThenByDescending(item => Version.TryParse(builtinVersion(item), out var version) ? version : null)
            .ThenBy(name, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
