namespace NetZapret.Gui.Views;

/// <summary>
/// Набор шагов рецепта — для показа.
/// </summary>
/// <remarks>
/// <para>
/// Шаг вроде <c>hostfakesplit:host=ozon.ru:tcp_ts=-1000:tcp_md5:repeats=4</c>
/// не содержит ни одного пробела, и перенос по словам ему не поможет:
/// WPF, не найдя где разорвать, рвёт посимвольно. В узком окне строка
/// вставала столбцом в пять букв — владелец прислал снимок 21.09.
/// </para>
/// <para>
/// Лечится невидимым разделителем U+200B после двоеточий и запятых:
/// он даёт разрыву место, не добавляя ничего видимого. Проверено
/// отрисовкой — WPF его чтит и ломает строку по нему, а не посреди
/// <c>ozon.ru</c>.
/// </para>
/// <para>
/// Не пробелом и не переносом строки: набор копируют в пресет, и лишний
/// пробел там сделает его негодным молча. U+200B при копировании тоже
/// уедет, но winws2 отбрасывает его как пустое место — а пробел разделил
/// бы аргумент надвое.
/// </para>
/// </remarks>
internal static class StepText
{
    /// <summary>Невидимое место, где можно перенести.</summary>
    private const string Break = "​";

    /// <summary>Чем разделяются шаги в одной строке.</summary>
    private const string Between = "   ·   ";

    public static string Of(IEnumerable<string> steps) =>
        string.Join(Between, steps.Select(Breakable));

    /// <summary>Расставляет места переноса внутри одного шага.</summary>
    public static string Breakable(string step) =>
        step.Replace(":", ":" + Break).Replace(",", "," + Break);
}
