using System.Text;

namespace NetZapret.Subscriptions;

/// <summary>
/// Вытаскивает код страны из названия сервера.
/// </summary>
/// <remarks>
/// <para>
/// Панели подписок ставят в тег флаг эмодзи — пару региональных
/// букв вроде U+1F1E9 U+1F1EA для Германии. Windows их не рисует: в Segoe UI
/// Emoji флагов стран нет вовсе, и на экране получается «de», влипшее
/// в название. Это не наша оплошность и шрифтом не лечится — Microsoft
/// не поставляет флаги намеренно.
/// </para>
/// <para>
/// Раз нарисовать нельзя, обходимся тем, что есть: пара превращается
/// в обычные заглавные буквы и выносится в значок рядом. Опознавательный
/// знак вместо опечатки.
/// </para>
/// <para>
/// Флаг ищется во всём имени, а не только в начале (12.10): у подписки
/// владельца на 403 сервера он в конце — «tg:VLESSFORU 🇵🇱 ⚡», и в строке
/// стояли буквы «PL» без картинки. Флагов несколько — цепочка вида
/// «🇷🇺 → 🇩🇪» — страной берётся последний: это выход. Остальные
/// остаются в имени как были.
/// </para>
/// </remarks>
public static class CountryTag
{
    /// <summary>Первая и последняя региональные буквы.</summary>
    private const int FirstIndicator = 0x1F1E6;
    private const int LastIndicator = 0x1F1FF;

    /// <summary>Делит тег на код страны и остальное имя.</summary>
    public static (string Country, string Name) Split(string tag)
    {
        // Пары региональных букв: начало пары в тексте и сами буквы.
        int at = -1;
        string country = string.Empty;

        for (int i = 0; i + 3 < tag.Length; i++)
        {
            if (Indicator(tag, i) is not { } first || Indicator(tag, i + 2) is not { } second)
                continue;

            at = i;
            country = new string([first, second]);

            // Пара занимает четыре символа UTF-16; следующую ищем за ней,
            // иначе вторая буква этой пары сошла бы за первую следующей.
            i += 3;
        }

        // Пара, а не одна буква: одиночная региональная буква кодом страны
        // не является, и показывать её значком означало бы выдумать страну.
        if (at < 0)
            return (string.Empty, tag.Trim());

        return (country, Tidy(tag.Remove(at, 4)));
    }

    /// <summary>Региональная буква, начинающаяся с этого места, — как A–Z.</summary>
    private static char? Indicator(string text, int index)
    {
        if (index + 1 >= text.Length || !char.IsSurrogatePair(text[index], text[index + 1]))
            return null;

        int code = char.ConvertToUtf32(text[index], text[index + 1]);

        return code is >= FirstIndicator and <= LastIndicator
            ? (char)('A' + code - FirstIndicator)
            : null;
    }

    /// <summary>Двойные пробелы, оставшиеся на месте флага, — в один.</summary>
    private static string Tidy(string name)
    {
        var builder = new StringBuilder(name.Length);

        foreach (var c in name.Trim())
        {
            if (c == ' ' && builder.Length > 0 && builder[^1] == ' ')
                continue;

            builder.Append(c);
        }

        return builder.ToString();
    }
}
