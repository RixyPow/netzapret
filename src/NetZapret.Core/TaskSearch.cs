namespace NetZapret.Core;

/// <summary>
/// Поиск «Что нужно сделать?» в окне: задачи разделов по словам человека.
/// </summary>
/// <remarks>
/// <para>
/// По образцу стенда разработчика (11.11): там тот же поиск по задачам
/// разделов владелец попросил перенести в окно программы. Каждое слово
/// запроса должно найтись — по началу слова, без учёта регистра и «ё»,
/// с грубым срезом окончаний: «подписку» находит «подписка», «серверы» —
/// «сервер». Точный разбор слов для полусотни задач не нужен.
/// </para>
/// <para>
/// Слова, которыми люди называют вещи иначе, чем окно («ютуб», «впн»,
/// «тг»), — группами синонимов: слово запроса подходит, если подходит
/// любое слово его группы.
/// </para>
/// </remarks>
public static class TaskSearch
{
    /// <summary>
    /// Подходящие элементы: сперва те, у кого больше слов запроса нашлось
    /// в самом тексте задачи, а не в ключевых словах; при равенстве — как шли.
    /// </summary>
    /// <param name="text">Текст задачи — то, что видит человек.</param>
    /// <param name="extra">Ключевые слова и название раздела — ищутся, но весят меньше.</param>
    public static IReadOnlyList<T> Find<T>(IEnumerable<T> items, Func<T, string> text, Func<T, string> extra, string? query)
    {
        var all = items.ToList();

        // Однобуквенные — предлоги и союзы («в», «и»): каждый подходит к половине
        // задач и ничего не сужает, а отсечь может — у задачи без слова на эту букву.
        var words = Words(query).Where(w => w.Length > 1).ToArray();

        if (words.Length == 0)
            return all;

        var found = new List<(T Item, int Score, int Index)>();

        for (int i = 0; i < all.Count; i++)
        {
            var own = Words(text(all[i]));
            var more = Words(extra(all[i]));

            if (!words.All(w => Matches(w, own) || Matches(w, more)))
                continue;

            found.Add((all[i], words.Count(w => Matches(w, own)), i));
        }

        return found
            .OrderByDescending(f => f.Score)
            .ThenBy(f => f.Index)
            .Select(f => f.Item)
            .ToList();
    }

    /// <summary>Слово запроса подходит, если с его основы (или основы синонима) начинается слово текста.</summary>
    public static bool Matches(string word, IReadOnlyCollection<string> hay)
    {
        foreach (var form in Forms(word))
        {
            var stem = Stem(form);

            if (hay.Any(h => h.StartsWith(stem, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>Слова текста: строчными, «ё» как «е», без знаков.</summary>
    public static string[] Words(string? text) => (text ?? string.Empty)
        .ToLowerInvariant()
        .Replace('ё', 'е')
        .Split([' ', ',', '.', ':', ';', '«', '»', '(', ')', '/', '\\', '-', '—', '–', '?', '!', '"', '\'', '+'],
            StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Основа: длинное слово без двух последних букв, но не короче четырёх.
    /// </summary>
    /// <remarks>
    /// «подписку» → «подпис», «серверы» → «сервер»; короткие («dns», «vpn»,
    /// «тема») остаются целыми — у них срезать нечего.
    /// </remarks>
    private static string Stem(string word) => word.Length > 4 ? word[..Math.Max(4, word.Length - 2)] : word;

    /// <summary>Само слово и его синонимы.</summary>
    private static IEnumerable<string> Forms(string word)
    {
        yield return word;

        foreach (var group in Synonyms)
        {
            if (group.Any(s => s == word || (word.Length >= 4 && s.StartsWith(Stem(word), StringComparison.Ordinal))))
            {
                foreach (var other in group)
                {
                    if (other != word)
                        yield return other;
                }
            }
        }
    }

    /// <summary>
    /// Как люди называют то, что в окне зовётся иначе. Пишется строчными и с «е» вместо «ё».
    /// </summary>
    private static readonly string[][] Synonyms =
    [
        ["ютуб", "ютюб", "youtube", "yt", "видео"],
        ["впн", "vpn", "туннель", "тунель"],
        ["десинк", "дпи", "dpi", "запрет", "zapret", "winws"],
        ["тг", "tg", "телеграм", "телега", "telegram"],
        ["дискорд", "discord", "дс"],
        ["днс", "dns", "резолвер"],
        ["хост", "hosts", "хосты"],
        ["лог", "логи", "журнал", "журналы"],
        ["репорт", "отчет", "report", "zip"],
        ["тема", "темы", "оформление", "цвет"],
        ["скорость", "спидтест", "speedtest"],
        ["блокчек", "блокировки", "блокировок", "закрыто"],
        ["пинг", "задержка", "ping"],
        ["сайт", "домен", "адрес"],
        ["пресет", "стратегия", "стратегии"],
        ["подписка", "ключ", "конфиг", "vless"],
        ["обновление", "обновить", "версия", "апдейт"],
        ["автозапуск", "автостарт", "автозагрузка"],
    ];
}
