namespace NetZapret.Proxy;

/// <summary>Имя, прибитое в hosts не в первый раз.</summary>
public sealed record HostsRepeat
{
    public required string Name { get; init; }

    /// <summary>Строка повтора, считая с нуля.</summary>
    public required int Line { get; init; }

    public required string Address { get; init; }

    /// <summary>Строка, которая действует: Windows берёт первую запись имени.</summary>
    public required int FirstLine { get; init; }

    public required string FirstAddress { get; init; }

    /// <summary>
    /// Другой адрес, чем у действующей записи.
    /// </summary>
    /// <remarks>
    /// Такой повтор не безвреден, а обманчив: в файле видно «имя → B»,
    /// а работает «имя → A» строкой выше. Снявший первую запись получит
    /// вторую — и решит, что снятие не сработало.
    /// </remarks>
    public bool Conflicts => !string.Equals(Address, FirstAddress, StringComparison.OrdinalIgnoreCase);

    /// <summary>Блок, в котором стоит повтор; <c>null</c> — вне блоков.</summary>
    public string? Block { get; init; }
}

/// <summary>Что нашлось в hosts: повторы, спорящие записи и строки, которые можно убрать.</summary>
public sealed record HostsDuplicateReport
{
    public required IReadOnlyList<HostsRepeat> Repeats { get; init; }

    /// <summary>
    /// Строки, целиком состоящие из повторов с тем же адресом.
    /// </summary>
    /// <remarks>
    /// Только такие убираются без последствий: всё, что в них сказано, уже
    /// сказано строкой выше, и разрешение имён от удаления не изменится.
    /// Строку со спорящим адресом или с новым именем среди повторов
    /// не трогаем — там убрать значит изменить смысл, а это решает человек.
    /// </remarks>
    public required IReadOnlyList<int> Removable { get; init; }

    /// <summary>Чужие блоки, откуда убираются строки: их хозяин может вернуть записи.</summary>
    public required IReadOnlyList<string> ForeignBlocks { get; init; }

    public int Same => Repeats.Count(r => !r.Conflicts);

    public int Conflicting => Repeats.Count(r => r.Conflicts);

    public bool Any => Repeats.Count > 0;
}

/// <summary>
/// Ищет в hosts имена, прибитые больше одного раза.
/// </summary>
/// <remarks>
/// <para>
/// Замер 23.09 на живом файле: все имена Claude прибиты дважды к одному
/// адресу — нашим блоком и блоком ZapretGUI ниже. Windows берёт первую
/// запись имени, так что вторая ничего не делает, но в редакторе выглядит
/// рабочей, и снявший наш пин продолжал бы ходить по чужому.
/// </para>
/// <para>
/// Только чтение. Убирает строки <see cref="HostsEditor.Remove"/> — с копией
/// и после вопроса человеку.
/// </para>
/// </remarks>
public static class HostsDuplicates
{
    public static HostsDuplicateReport Find(string? path = null)
    {
        string[] lines;

        try
        {
            lines = File.ReadAllLines(path ?? HostsFile.DefaultPath);
        }
        catch (Exception)
        {
            lines = [];
        }

        return Find(HostsEditor.Parse(path), lines);
    }

    /// <param name="entries">Разобранные записи — <see cref="HostsEditor.Parse"/>.</param>
    /// <param name="lines">Сам файл: по нему узнаётся, в каком блоке строка.</param>
    public static HostsDuplicateReport Find(IReadOnlyList<HostsEntry> entries, IReadOnlyList<string> lines)
    {
        var blocks = BlocksOf(lines);
        var first = new Dictionary<string, (int Line, string Address)>(StringComparer.OrdinalIgnoreCase);
        var repeats = new List<HostsRepeat>();
        var removable = new List<int>();

        // Выключенные записи не считаются: Windows их не читает, и повтором
        // действующей строки закомментированная не является.
        foreach (var entry in entries.Where(e => e.Enabled).OrderBy(e => e.Line))
        {
            int repeated = 0;
            bool allSame = true;

            foreach (var name in entry.Names)
            {
                if (!first.TryGetValue(name, out var earlier))
                {
                    first[name] = (entry.Line, entry.Address);
                    allSame = false;
                    continue;
                }

                var repeat = new HostsRepeat
                {
                    Name = name,
                    Line = entry.Line,
                    Address = entry.Address,
                    FirstLine = earlier.Line,
                    FirstAddress = earlier.Address,
                    Block = blocks.GetValueOrDefault(entry.Line),
                };

                repeats.Add(repeat);
                repeated++;

                if (repeat.Conflicts)
                    allSame = false;
            }

            // Повтор внутри своего же блока правится перезаписью блока,
            // а не удалением строки: блок ведём мы, и строка в нём —
            // не чужая запись, а наше состояние.
            if (repeated > 0 && allSame && !IsOurs(blocks.GetValueOrDefault(entry.Line)))
                removable.Add(entry.Line);
        }

        var foreign = removable
            .Select(line => blocks.GetValueOrDefault(line))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new HostsDuplicateReport { Repeats = repeats, Removable = removable, ForeignBlocks = foreign };
    }

    /// <summary>
    /// Строка → имя блока, в котором она стоит.
    /// </summary>
    /// <remarks>
    /// Блоком считается всё между строками вида <c># &gt;&gt;&gt; … &gt;&gt;&gt;</c>
    /// и <c># &lt;&lt;&lt; … &lt;&lt;&lt;</c>: так размечают свои записи и мы,
    /// и ZapretGUI. Имя блока — текст открывающей строки без стрелок.
    /// </remarks>
    private static Dictionary<int, string> BlocksOf(IReadOnlyList<string> lines)
    {
        var result = new Dictionary<int, string>();
        string? current = null;

        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();

            if (line.StartsWith("# >>>", StringComparison.Ordinal))
            {
                current = line.Trim('#', ' ', '>');
                continue;
            }

            if (line.StartsWith("# <<<", StringComparison.Ordinal))
            {
                current = null;
                continue;
            }

            if (current is not null)
                result[i] = current;
        }

        return result;
    }

    private static bool IsOurs(string? block) =>
        block is not null && ("# >>> " + block + " >>>").Equals(HostsEditor.BlockBegin, StringComparison.Ordinal);
}
