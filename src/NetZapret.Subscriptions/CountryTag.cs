namespace NetZapret.Subscriptions;

/// <summary>
/// Вытаскивает код страны из названия сервера.
/// </summary>
/// <remarks>
/// <para>
/// Панели подписок ставят в начало тега флаг эмодзи — пару региональных
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
/// </remarks>
public static class CountryTag
{
    /// <summary>Первая и последняя региональные буквы.</summary>
    private const int FirstIndicator = 0x1F1E6;
    private const int LastIndicator = 0x1F1FF;

    /// <summary>Делит тег на код страны и остальное имя.</summary>
    public static (string Country, string Name) Split(string tag)
    {
        var letters = new List<char>();
        int i = 0;

        while (i < tag.Length)
        {
            if (!char.IsHighSurrogate(tag[i]) || i + 1 >= tag.Length)
                break;

            int code = char.ConvertToUtf32(tag[i], tag[i + 1]);

            if (code is < FirstIndicator or > LastIndicator)
                break;

            letters.Add((char)('A' + code - FirstIndicator));
            i += 2;
        }

        // Пара, а не одна буква: одиночная региональная буква кодом страны
        // не является, и показывать её значком означало бы выдумать страну.
        return letters.Count == 2
            ? (new string(letters.ToArray()), tag[i..].Trim())
            : (string.Empty, tag.Trim());
    }
}
