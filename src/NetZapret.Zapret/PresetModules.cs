namespace NetZapret.Zapret;

/// <summary>
/// Подключение модуля Lua к пресету.
/// </summary>
/// <remarks>
/// <para>
/// Каталог рецептов умеет сказать, какого модуля не хватает. Без этого
/// класса на том бы и кончалось: рецепт показан, причина названа, сделать
/// нельзя ничего. Правка — одна строка <c>--lua-init</c> в шапке, и она
/// той же природы, что «Открыть в редакторе» рядом, только без риска
/// задеть при этом что-то ещё.
/// </para>
/// <para>
/// Порядок строк значим. Модули подключаются по очереди, и тот, что
/// опирается на zapret-lib.lua, обязан идти после него. Поэтому новая
/// строка встаёт последней среди <c>--lua-init</c>, а не первой и не куда
/// придётся: всё, что уже есть, к этому месту подключено.
/// </para>
/// </remarks>
public static class PresetModules
{
    /// <summary>Чем кончилась попытка.</summary>
    public enum Result
    {
        /// <summary>Строка дописана.</summary>
        Connected,

        /// <summary>Модуль уже был подключён — файл не тронут.</summary>
        Already,

        /// <summary>В шапке нет ни одного <c>--lua-init</c> — вставлять не к чему.</summary>
        NoPlace,
    }

    /// <summary>
    /// Дописать <c>--lua-init</c> для модуля.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Файл переписывается целиком, но меняется в нём ровно одна строка:
    /// остальные возвращаются как прочитаны. Пресеты правятся и Zapret GUI,
    /// и рукой, и переформатировать чужой файл под свой вкус значило бы
    /// устроить владельцу непрошеные отличия во всём файле вместо одной
    /// понятной строки.
    /// </para>
    /// <para>
    /// Перевод строки берётся тот, что в файле уже преобладает. Пресеты
    /// приходят из Zapret с CRLF, и приписанная к ним строка с одним LF
    /// делает файл смешанным — такой разбирается, но выглядит в чужом
    /// редакторе испорченным.
    /// </para>
    /// </remarks>
    public static Result Connect(string presetPath, string module)
    {
        var text = File.ReadAllText(presetPath);
        var ending = Ending(text);
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        // Последний пустой кусок — это хвост после завершающего перевода
        // строки, а не строка файла. Оставь его — и каждое подключение
        // дописывало бы пресету по лишней пустой строке.
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        var wanted = $"--lua-init=@lua/{module}";

        int last = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            if (!line.StartsWith("--lua-init=", StringComparison.Ordinal))
                continue;

            last = i;

            if (line.EndsWith("/" + module, StringComparison.OrdinalIgnoreCase)
                || line.EndsWith("=" + module, StringComparison.OrdinalIgnoreCase)
                || line.EndsWith("@" + module, StringComparison.OrdinalIgnoreCase))
            {
                return Result.Already;
            }
        }

        if (last < 0)
            return Result.NoPlace;

        lines.Insert(last + 1, wanted);
        File.WriteAllText(presetPath, string.Join(ending, lines) + ending);

        return Result.Connected;
    }

    /// <summary>Перевод строки, преобладающий в файле.</summary>
    private static string Ending(string text)
    {
        int crlf = 0, at = 0;

        while ((at = text.IndexOf("\r\n", at, StringComparison.Ordinal)) >= 0)
        {
            crlf++;
            at += 2;
        }

        int lf = text.Count(c => c == '\n');

        return crlf * 2 >= lf ? "\r\n" : "\n";
    }
}
