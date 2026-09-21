using System.Text;

namespace NetZapret.Core.Rules;

/// <summary>
/// Чтение и запись книги маршрутов.
/// </summary>
/// <remarks>
/// <para>
/// Формат нарочно беден: «имя: маршрут», по строке на запись, плюс раздел
/// своих групп. Его будут править руками и пересылать друг другу, и всякая
/// возможность записать одно и то же двумя способами обернётся вопросом
/// «а так тоже можно?».
/// </para>
/// <para>
/// Свой разборщик, а не YAML целиком. Полный YAML умеет якоря, ссылки,
/// многострочные блоки и восемь способов написать «да» — ничего из этого
/// здесь не нужно, а ошибки в таком файле объяснять человеку тяжело.
/// Разбор по двоеточию укладывается в сотню строк и ошибается понятно.
/// </para>
/// </remarks>
public static class RouteBookFile
{
    public const string DefaultPath = "config/routes.yaml";

    /// <summary>Как маршрут пишется в файле.</summary>
    public static string NameOf(RouteChoice choice) => choice switch
    {
        RouteChoice.Desync => "desync",
        RouteChoice.Vpn => "vpn",
        RouteChoice.Pin => "pin",
        _ => "direct",
    };

    /// <summary>
    /// Разбирает маршрут; <c>null</c> — слово незнакомое.
    /// </summary>
    /// <remarks>
    /// Русские написания понимаются тоже, и это уступка руке: человек,
    /// правящий файл после окна, напишет «десинк» раньше, чем вспомнит
    /// про латиницу. Пишем мы всегда латиницей — файл для обмена.
    /// </remarks>
    public static RouteChoice? ChoiceOf(string word) => word.Trim().ToLowerInvariant() switch
    {
        "direct" or "напрямую" or "прямо" => RouteChoice.Direct,
        "desync" or "десинк" => RouteChoice.Desync,
        "vpn" or "proxy" or "впн" or "туннель" => RouteChoice.Vpn,
        "pin" or "пин" => RouteChoice.Pin,
        _ => null,
    };

    /// <summary>
    /// Разбирает книгу из текста.
    /// </summary>
    /// <remarks>
    /// Непонятая строка отбрасывается молча, а не роняет разбор целиком:
    /// файл переносят между версиями, и запись, которой эта версия ещё
    /// не знает, не повод отказаться от остальных восьмидесяти семи.
    /// </remarks>
    public static RouteBook Parse(string text)
    {
        var entries = new List<RouteEntry>();
        var groups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        string? group = null;
        bool inGroups = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var body = Strip(line);

            if (body.Length == 0)
                continue;

            // Раздел групп открывается строкой без отступа.
            if (!char.IsWhiteSpace(line[0]))
            {
                inGroups = body.StartsWith("groups:", StringComparison.OrdinalIgnoreCase)
                    || body.StartsWith("группы:", StringComparison.OrdinalIgnoreCase);

                if (inGroups)
                {
                    group = null;
                    continue;
                }

                if (body.StartsWith("routes:", StringComparison.OrdinalIgnoreCase)
                    || body.StartsWith("маршруты:", StringComparison.OrdinalIgnoreCase))
                {
                    group = null;
                    continue;
                }
            }

            if (inGroups)
            {
                // Имя домена в группе: «  - example.com».
                if (body.StartsWith('-'))
                {
                    if (group is null)
                        continue;

                    var domain = Clean(body[1..]);

                    if (domain.Length == 0)
                        continue;

                    var into = (List<string>)groups[group];
                    into.Add(domain);
                    continue;
                }

                // Заголовок своей группы: «  work:».
                if (body.EndsWith(':'))
                {
                    group = Clean(body[..^1]);

                    if (group.Length > 0 && !groups.ContainsKey(group))
                        groups[group] = new List<string>();

                    continue;
                }

                continue;
            }

            if (Entry(body) is { } entry)
                entries.Add(entry);
        }

        return new RouteBook
        {
            Entries = entries,
            Groups = groups.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToList(),
                StringComparer.OrdinalIgnoreCase),

            Clashes = RouteClashes.Find(entries),
        };
    }

    /// <summary>
    /// Разбирает одну строку «имя: маршрут» или «имя: desync recipe».
    /// </summary>
    /// <remarks>
    /// Рецепт пишется через пробел после маршрута. Отдельным полем он
    /// потребовал бы вложенности, а вложенность — единственное, чего
    /// в этом формате нет вовсе.
    /// </remarks>
    private static RouteEntry? Entry(string body)
    {
        int colon = body.IndexOf(':');

        if (colon <= 0)
            return null;

        var name = Clean(body[..colon]);
        var rest = body[(colon + 1)..].Trim();

        if (name.Length == 0 || rest.Length == 0)
            return null;

        var parts = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (ChoiceOf(parts[0]) is not { } choice)
            return null;

        return new RouteEntry
        {
            Name = name,
            Choice = choice,

            // Рецепт берём только у десинка: у прочих маршрутов он смысла
            // не имеет, и сохранённый там обещал бы несделанное.
            Recipe = choice == RouteChoice.Desync && parts.Length > 1
                ? Clean(parts[1])
                : null,
        };
    }

    /// <summary>Убирает примечание и обрамляющие пробелы.</summary>
    private static string Strip(string line)
    {
        int hash = line.IndexOf('#');

        return (hash >= 0 ? line[..hash] : line).Trim();
    }

    /// <summary>Снимает кавычки и звёздочку зоны.</summary>
    private static string Clean(string value) =>
        value.Trim().Trim('"', '\'').TrimStart('*', '.').Trim();

    /// <summary>
    /// Записывает книгу.
    /// </summary>
    /// <remarks>
    /// Всегда латиницей, как бы ни было записано прочитанное: файл
    /// предназначен для обмена, и отданный другому он должен читаться
    /// одинаково.
    /// </remarks>
    public static string Write(RouteBook book)
    {
        var text = new StringBuilder();

        text.AppendLine("# Книга маршрутов NetZapret.");
        text.AppendLine("#");
        text.AppendLine("# Строка «имя: маршрут». Маршруты: direct, desync, vpn, pin.");
        text.AppendLine("# Имя без точки — группа, с точкой — отдельный домен.");
        text.AppendLine("#");
        text.AppendLine("# Файл переносимый: он не трогает ни пресет winws2, ни конфиг");
        text.AppendLine("# туннеля, и работает с любым из них. Его можно отдать другому.");
        text.AppendLine("#");
        text.AppendLine("# «pin» — рекомендация прибить адрес в hosts, а не сама правка:");
        text.AppendLine("# адрес на другой машине будет свой, и файл его не хранит.");
        text.AppendLine();

        if (book.Groups.Count > 0)
        {
            text.AppendLine("groups:");

            foreach (var (name, domains) in book.Groups.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                text.AppendLine($"  {name}:");

                foreach (var domain in domains)
                    text.AppendLine($"    - {domain}");
            }

            text.AppendLine();
        }

        text.AppendLine("routes:");

        // Пины — в конец, отдельной кучей. Вперемешку с маршрутами они
        // читаются как маршруты, и файл выходит непонятным: у владельца
        // сто двенадцать пинов на восемьдесят четыре маршрута, и сверху
        // «chatgpt: direct», а на сотню строк ниже «chatgpt.com: pin».
        //
        // Противоречия тут нет — группа и отдельное имя, «мимо всего»
        // и «адрес закреплён», — но чтобы это увидеть, надо знать
        // устройство файла. Порядок объясняет его без слов.
        //
        // Раздел не заводится: разбор от этого усложнился бы, а строка
        // «pin» и так говорит о себе всё. Разделяет примечание, которое
        // разборщик и так отбрасывает.
        var pins = book.Entries.Where(e => e.Choice == RouteChoice.Pin).ToList();

        foreach (var entry in book.Entries.Where(e => e.Choice != RouteChoice.Pin))
            text.AppendLine(Line(entry));

        if (pins.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("  # Прибитые адреса. Это не маршрут, а добавочное: имя");
            text.AppendLine("  # получает закреплённый адрес, и потому идёт мимо туннеля");
            text.AppendLine("  # и мимо десинка. С «direct» уживается — оба означают");
            text.AppendLine("  # «мимо всего»; с «vpn» и «desync» спорит.");

            foreach (var entry in pins)
                text.AppendLine(Line(entry));
        }

        return text.ToString();
    }

    private static string Line(RouteEntry entry)
    {
        var line = $"  {entry.Name}: {NameOf(entry.Choice)}";

        return entry.Recipe is { Length: > 0 } recipe ? line + " " + recipe : line;
    }
}
