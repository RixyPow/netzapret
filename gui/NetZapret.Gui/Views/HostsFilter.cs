namespace NetZapret.Gui.Views;

/// <summary>
/// Отбор строк файла hosts по строке поиска.
/// </summary>
/// <remarks>
/// <para>
/// Заведён по просьбе владельца 21.09. На его машине в файле сто три наши
/// записи и семьсот восемьдесят две чужие — глазами такое не перебрать,
/// а искать в hosts приходится ровно тогда, когда что-то сломалось
/// и времени нет.
/// </para>
/// <para>
/// Ищется и по имени, и по адресу. Второе не прихоть: вопрос «кто у меня
/// прибит на 163.70.151.174» возникает не реже обратного — так ловятся
/// устаревшие адреса, оставшиеся от давно сменившейся сети доставки.
/// </para>
/// <para>
/// Отдельно от вкладки затем, что это её единственная чистая часть.
/// Проверить отбор можно без окна, а обработчик ввода — нет.
/// </para>
/// </remarks>
internal static class HostsFilter
{
    /// <summary>Подходит ли строка под запрос.</summary>
    /// <remarks>
    /// Пустой запрос подходит всем: это не «ничего не найдено», а «не ищем».
    /// </remarks>
    public static bool Matches(PinRow row, string? needle)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return true;

        var wanted = needle.Trim();

        return Has(row.Name, wanted)
            || Has(row.Detail, wanted)
            || Has(row.Note, wanted);
    }

    public static IReadOnlyList<PinRow> Apply(IEnumerable<PinRow> rows, string? needle) =>
        rows.Where(row => Matches(row, needle)).ToList();

    /// <summary>
    /// Что удалит «Удалить найденные»: строка файла и имя в ней.
    /// </summary>
    /// <remarks>
    /// <para>
    /// По свежему разбору файла, а не по строкам на экране: те собраны
    /// при показе, и номера в них могли устареть.
    /// </para>
    /// <para>
    /// Тем же правилом, что и показ, — по имени, адресу и подписи. Совпал
    /// адрес — уходят все имена строки: искали «кто прибит на этот адрес».
    /// Совпало имя — только оно: соседи по строке не искались.
    /// </para>
    /// <para>
    /// Пустой запрос не отбирает ничего. У показа он значит «все», но здесь
    /// это была бы та самая кнопка «почистить всё», которой быть не должно.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<(int Line, string Name)> Targets(
        IEnumerable<NetZapret.Proxy.HostsEntry> entries, string? needle)
    {
        if (!Searching(needle))
            return [];

        var wanted = needle!.Trim();

        return entries
            .SelectMany(e => e.Names
                .Where(n => Has(n, wanted) || Has(e.Address, wanted) || Has(e.Note, wanted))
                .Select(n => (e.Line, n)))
            .ToList();
    }

    /// <summary>
    /// Ищем ли мы вообще.
    /// </summary>
    /// <remarks>
    /// Пробелы не в счёт. Иначе случайный пробел в поле прятал бы весь
    /// список и выглядел бы поломкой вкладки.
    /// </remarks>
    public static bool Searching(string? needle) => !string.IsNullOrWhiteSpace(needle);

    private static bool Has(string? where, string what) =>
        where is not null && where.Contains(what, StringComparison.OrdinalIgnoreCase);
}
