using System.Net;
using System.Text;

namespace NetZapret.Proxy;

/// <summary>Одна строка файла hosts, как её видит редактор.</summary>
public sealed record HostsEntry
{
    /// <summary>Номер строки в файле, считая с нуля.</summary>
    public required int Line { get; init; }

    public required string Address { get; init; }

    /// <summary>Имена в строке; их бывает несколько.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Действует ли запись, или закомментирована.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Комментарий в конце строки — обычно подпись, кто её поставил.</summary>
    public string? Note { get; init; }

    public string Describe() => $"{Address} → {string.Join(", ", Names)}";
}

/// <summary>Что известно про адрес, к которому прибиты имена.</summary>
public sealed record PinHealth
{
    public required string Address { get; init; }

    /// <summary>Сколько имён на него смотрит.</summary>
    public required int Names { get; init; }

    /// <summary>Отвечает ли он вообще.</summary>
    public required bool Alive { get; init; }

    /// <summary>
    /// Удалось ли вообще проверить.
    /// </summary>
    /// <remarks>
    /// Непроверенное — не молчащее. Адрес IPv6 в сети без IPv6 молчит
    /// не потому, что мёртв, и объявлять его мёртвым значит советовать
    /// выключить рабочую запись.
    /// </remarks>
    public bool Tested { get; init; } = true;

    /// <summary>Почему не проверяли.</summary>
    public string? Skipped { get; init; }

    public TimeSpan Elapsed { get; init; }
}

/// <summary>Чем кончилась запись в файл.</summary>
public sealed record PinResult
{
    /// <summary>Куда сложена копия прежнего файла.</summary>
    public string? Backup { get; init; }

    /// <summary>Сколько имён осталось в нашем блоке.</summary>
    public required int Pinned { get; init; }

    /// <summary>
    /// Чужие строки на те же имена.
    /// </summary>
    /// <remarks>
    /// Наш блок стоит выше и разбирается первым, но чужая запись никуда
    /// не делась. Человек, который снимет наш пин, получит её — и, не зная
    /// о ней, решит, что снятие не сработало.
    /// </remarks>
    public required IReadOnlyList<string> Shadowed { get; init; }
}

/// <summary>
/// Правит файл hosts: включает, выключает и прибивает записи.
/// </summary>
/// <remarks>
/// <para>
/// Осторожность здесь не формальность, а суть. Файл системный, общий для всех
/// программ, и ведём его не только мы: записи ставит редактор из Zapret GUI,
/// ставят руками, ставят чужие наборы для обхода. Стереть чужую строку значит
/// сломать то, что человек настраивал не у нас и чинить будет не у нас.
/// </para>
/// <para>
/// Отсюда два правила. Выключение — это комментарий, а не удаление: запись
/// сохраняется и возвращается тем же движением. И перед всякой записью
/// делается копия рядом с файлом — не «на всякий случай», а потому что
/// единственная ошибка здесь стоит работающей сети.
/// </para>
/// </remarks>
public static class HostsEditor
{
    /// <summary>
    /// Разбирает файл построчно, сохраняя и выключенные записи.
    /// </summary>
    /// <remarks>
    /// В отличие от <see cref="HostsFile.Read"/>, который отвечает на вопрос
    /// «во что разрешится имя», здесь нужен сам файл: номера строк, порядок,
    /// закомментированные записи. Иначе редактировать нечего.
    /// </remarks>
    public static IReadOnlyList<HostsEntry> Parse(string? path = null)
    {
        var entries = new List<HostsEntry>();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path ?? HostsFile.DefaultPath);
        }
        catch (Exception)
        {
            return entries;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();

            if (line.Length == 0)
                continue;

            bool enabled = true;

            // Закомментированная строка может быть выключенной записью,
            // а может быть настоящим комментарием. Отличаем разбором:
            // если после решёток идёт адрес и имя — это запись.
            if (line.StartsWith('#'))
            {
                enabled = false;
                line = line.TrimStart('#').Trim();
            }

            string? note = null;
            int hash = line.IndexOf('#');

            if (hash >= 0)
            {
                note = line[(hash + 1)..].Trim();
                line = line[..hash].Trim();
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || !IPAddress.TryParse(parts[0], out var address))
                continue;

            entries.Add(new HostsEntry
            {
                Line = i,
                Address = address.ToString(),
                Names = parts.Skip(1).ToList(),
                Enabled = enabled,
                Note = string.IsNullOrWhiteSpace(note) ? null : note,
            });
        }

        return entries;
    }

    /// <summary>
    /// Включает или выключает названные строки.
    /// </summary>
    /// <param name="lines">Номера строк из <see cref="HostsEntry.Line"/>.</param>
    /// <returns>Путь к копии, сделанной перед правкой.</returns>
    /// <remarks>
    /// Правит ровно указанные строки и не трогает ничего вокруг: остальной
    /// файл переписывается байт в байт, включая чужие комментарии и порядок.
    /// Переписывание «как мы понимаем формат» стёрло бы то, чего мы не поняли.
    /// </remarks>
    public static string SetEnabled(IReadOnlyCollection<int> lines, bool enabled, string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;
        var content = File.ReadAllLines(target);
        var backup = Backup(target);

        foreach (var index in lines)
        {
            if (index < 0 || index >= content.Length)
                continue;

            var line = content[index];

            if (enabled)
            {
                var bare = line.TrimStart();

                if (bare.StartsWith('#'))
                    content[index] = bare.TrimStart('#').TrimStart();
            }
            else if (!line.TrimStart().StartsWith('#'))
            {
                content[index] = "# " + line;
            }
        }

        Write(target, content);
        return backup;
    }

    // Своих записей в hosts мы не заводим — для этого есть
    // config/addresses.yaml, где адрес подставляет наш резолвер. Разница
    // существенная: правка hosts требует администратора, переживает удаление
    // программы и видна всем приложениям сразу. Здесь только чужие записи
    // и только выключение.

    /// <summary>
    /// Проверяет, отвечают ли адреса, к которым прибиты имена.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ради этого редактор и заводится. Прибитый мёртвый адрес — чистый вред:
    /// он перебивает и наш маршрут, и обычный DNS, а сам не отвечает, и со
    /// стороны это неотличимо от блокировки.
    /// </para>
    /// <para>
    /// Наблюдалось вживую: 670 имён на один адрес, переставший отвечать,
    /// и «умерший» ChatGPT, который не чинился ни десинком, ни туннелем.
    /// </para>
    /// <para>
    /// Стучимся по 443 — это то, ради чего такие адреса и ставят. Живым
    /// считается ответивший хоть чем-то: отказ в соединении тоже ответ,
    /// он означает, что хост есть.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<PinHealth>> CheckAsync(
        IReadOnlyList<HostsEntry> entries,
        CancellationToken cancellationToken)
    {
        var byAddress = entries
            .Where(e => e.Enabled)
            .GroupBy(e => e.Address, StringComparer.Ordinal)
            .ToList();

        var result = new List<PinHealth>();

        // Спрашивается один раз: без IPv6 в сети всякий адрес IPv6 молчит,
        // и без этой поправки проверка объявила бы мёртвыми рабочие записи.
        bool haveIpV6 = await HasIpV6Async(cancellationToken);

        foreach (var group in byAddress)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Петля и нули ставят, чтобы имя не открывалось. Это не мёртвый
            // адрес, а намеренная заглушка, и тревожить о ней незачем.
            if (IPAddress.TryParse(group.Key, out var parsed) && BlockCheck.IsStub(parsed))
                continue;

            int names = group.Sum(g => g.Names.Count);

            if (!haveIpV6 && parsed?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                result.Add(new PinHealth
                {
                    Address = group.Key,
                    Names = names,
                    Alive = false,
                    Tested = false,
                    Skipped = "в этой сети нет IPv6",
                });

                continue;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool alive = await AnswersAsync(group.Key, cancellationToken);

            result.Add(new PinHealth
            {
                Address = group.Key,
                Names = names,
                Alive = alive,
                Elapsed = watch.Elapsed,
            });
        }

        return result.OrderByDescending(r => r.Names).ToList();
    }

    /// <summary>Есть ли в этой сети IPv6 вообще.</summary>
    /// <remarks>
    /// Проверяется соединением к публичному резолверу Google по IPv6.
    /// Наличие адреса на адаптере не годится: Windows раздаёт себе локальные
    /// адреса и без всякой связи наружу.
    /// </remarks>
    private static async Task<bool> HasIpV6Async(CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient(System.Net.Sockets.AddressFamily.InterNetworkV6);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            await client.ConnectAsync(IPAddress.Parse("2001:4860:4860::8888"), 53, timeout.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<bool> AnswersAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));

            await client.ConnectAsync(IPAddress.Parse(address), 443, timeout.Token);
            return true;
        }
        catch (System.Net.Sockets.SocketException ex)
            when (ex.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused)
        {
            // Отказано — значит хост есть и отвечает, просто не слушает порт.
            // Для нашей цели это живой адрес.
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Копия файла перед правкой.
    /// </summary>
    /// <remarks>
    /// Рядом с оригиналом и с отметкой времени: восстановление должно быть
    /// очевидным действием без нашего участия — переименовать и всё.
    /// </remarks>
    /// <summary>Начало блока, который ведём мы.</summary>
    public const string BlockBegin = "# >>> netzapret begin >>>";

    /// <summary>Конец блока, который ведём мы.</summary>
    public const string BlockEnd = "# <<< netzapret end <<<";

    /// <summary>
    /// Прибивает имена к адресам в собственном блоке файла.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Свой блок, и притом в начале файла. Первое — чтобы никогда не тронуть
    /// чужую строку: файл ведут и Zapret GUI, и руками, и стереть там
    /// не своё значит сломать то, что чинить будут не у нас. Второе — потому
    /// что при двух записях на одно имя разбор идёт сверху, и блок, дописанный
    /// в конец, проиграл бы чужому пину молча.
    /// </para>
    /// <para>
    /// Возвращается не только путь к копии, но и список чужих строк на те же
    /// имена. Молчать о них нельзя: пока они на месте, имя разрешается дважды,
    /// и предсказать исход по файлу уже не выйдет.
    /// </para>
    /// </remarks>
    /// <param name="entries">Имя и адрес; имена, что уже в блоке, заменяются.</param>
    public static PinResult Pin(
        IReadOnlyDictionary<string, string> entries,
        string? path = null,
        string? note = null)
    {
        var many = entries.ToDictionary(
            p => p.Key,
            p => (IReadOnlyList<string>)[p.Value],
            StringComparer.OrdinalIgnoreCase);

        return PinMany(many, path, note, absorb: false);
    }

    /// <summary>
    /// Забирает записи файла в наш блок, убирая повторы.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Наводит порядок там, где его вести перестали. В живом файле оказалось
    /// 839 записей на 796 имён — сорок три повтора, — собранных тремя разными
    /// механизмами: каталогом Zapret, отдельным блоком Telegram и руками.
    /// Разобрать такое глазами нельзя, а действует из повторов первый,
    /// и какой именно — по файлу не видно.
    /// </para>
    /// <para>
    /// Все адреса имени сохраняются, а не первый попавшийся: у
    /// <c>instagram.com</c> их три, включая IPv6, у <c>openai.com</c> четыре,
    /// и Windows перебирает их по очереди. Оставить один значило бы
    /// собственноручно урезать запасные пути.
    /// </para>
    /// <para>
    /// Выключенные строки не трогаются вовсе: человек выключил их намеренно,
    /// и втянуть их в наш блок — значит либо потерять, либо включить обратно.
    /// </para>
    /// </remarks>
    public static PinResult Absorb(string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;

        if (!File.Exists(target))
            return new PinResult { Pinned = 0, Shadowed = [] };

        var lines = File.ReadAllLines(target).ToList();
        var (start, end) = FindBlock(lines);
        var gathered = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < lines.Count; i++)
        {
            if (start >= 0 && i >= start && i <= end)
                continue;

            if (Split(lines[i]) is not { } pair)
                continue;

            if (!gathered.TryGetValue(pair.Name, out var list))
                gathered[pair.Name] = list = [];

            if (!list.Contains(pair.Address, StringComparer.OrdinalIgnoreCase))
                list.Add(pair.Address);
        }

        // Строки, ушедшие к нам, убираются с прежних мест — иначе повторы
        // не исчезнут, а удвоятся.
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (start >= 0 && i >= start && i <= end)
                continue;

            if (Split(lines[i]) is not null)
                lines.RemoveAt(i);
        }

        return PinMany(
            gathered.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase),
            path,
            note: null,
            absorb: true,
            prepared: lines);
    }

    private static PinResult PinMany(
        IReadOnlyDictionary<string, IReadOnlyList<string>> entries,
        string? path,
        string? note,
        bool absorb,
        List<string>? prepared = null)
    {
        var target = path ?? HostsFile.DefaultPath;
        var backup = File.Exists(target) ? Backup(target) : null;
        var lines = prepared ?? (File.Exists(target) ? File.ReadAllLines(target).ToList() : []);

        var (start, end) = FindBlock(lines);
        var kept = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Уже прибитое нами сохраняется: закрепляют по одному сервису,
        // и переписывать блок целиком значило бы снимать все прежние.
        if (start >= 0)
        {
            foreach (var line in lines.Skip(start + 1).Take(end - start - 1))
            {
                if (Split(line) is not { } pair)
                    continue;

                if (!kept.TryGetValue(pair.Name, out var list))
                    kept[pair.Name] = list = [];

                if (!list.Contains(pair.Address, StringComparer.OrdinalIgnoreCase))
                    list.Add(pair.Address);
            }

            lines.RemoveRange(start, end - start + 1);
        }

        foreach (var (rawName, addresses) in entries)
        {
            var name = rawName.TrimStart('*', '.');

            // Закрепление заменяет прежний адрес имени, а сбор — дополняет:
            // в первом случае человек выбрал новый набор взамен старого,
            // во втором мы лишь переносим то, что уже есть.
            if (!absorb || !kept.TryGetValue(name, out var list))
                kept[name] = list = [];

            foreach (var address in addresses)
            {
                if (!list.Contains(address, StringComparer.OrdinalIgnoreCase))
                    list.Add(address);
            }
        }

        var block = new List<string> { BlockBegin };

        if (!string.IsNullOrWhiteSpace(note))
            block.Add("# " + note);

        block.Add("# Записи ведёт NetZapret. Правьте их через меню: при следующей");
        block.Add("# записи всё, что дописано сюда руками, будет потеряно.");

        foreach (var (name, addresses) in kept.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            foreach (var address in addresses)
                block.Add($"{address} {name}");
        }

        block.Add(BlockEnd);
        block.Add(string.Empty);

        // В начало, но после шапки из комментариев: она объясняет формат файла,
        // и вытеснять её вниз незачем.
        lines.InsertRange(HeaderLength(lines), block);

        Write(target, lines);

        return new PinResult
        {
            Backup = backup,
            Pinned = kept.Count,
            Shadowed = Foreign(lines, kept.Keys),
        };
    }

    /// <summary>Снимает имена из нашего блока; чужих строк не касается.</summary>
    public static PinResult Unpin(IReadOnlyCollection<string> names, string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;

        if (!File.Exists(target))
            return new PinResult { Pinned = 0, Shadowed = [] };

        var lines = File.ReadAllLines(target).ToList();
        var (start, end) = FindBlock(lines);

        if (start < 0)
            return new PinResult { Pinned = 0, Shadowed = [] };

        var backup = Backup(target);
        var drop = new HashSet<string>(names.Select(n => n.TrimStart('*', '.')), StringComparer.OrdinalIgnoreCase);
        var left = 0;

        for (int i = end - 1; i > start; i--)
        {
            if (Split(lines[i]) is not { } pair)
                continue;

            if (drop.Contains(pair.Name))
                lines.RemoveAt(i);
            else
                left++;
        }

        // Пустой блок убирается целиком — иначе в файле копятся наши следы
        // от сервисов, которых давно нет.
        if (left == 0)
        {
            (start, end) = FindBlock(lines);

            if (start >= 0)
                lines.RemoveRange(start, end - start + 1);
        }

        Write(target, lines);

        return new PinResult { Backup = backup, Pinned = left, Shadowed = [] };
    }

    /// <summary>Имена, которые мы прибили; пусто, если блока нет.</summary>
    public static IReadOnlyDictionary<string, string> Pins(string? path = null)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var target = path ?? HostsFile.DefaultPath;

        if (!File.Exists(target))
            return result;

        var lines = File.ReadAllLines(target).ToList();
        var (start, end) = FindBlock(lines);

        if (start < 0)
            return result;

        foreach (var line in lines.Skip(start + 1).Take(end - start - 1))
        {
            if (Split(line) is { } pair)
                result[pair.Name] = pair.Address;
        }

        return result;
    }

    /// <summary>Границы нашего блока; <c>(-1, -1)</c> — блока нет.</summary>
    private static (int Start, int End) FindBlock(IReadOnlyList<string> lines)
    {
        int start = -1;

        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(BlockBegin, StringComparison.Ordinal))
                start = i;
            else if (start >= 0 && lines[i].StartsWith(BlockEnd, StringComparison.Ordinal))
                return (start, i);
        }

        return (-1, -1);
    }

    /// <summary>
    /// Сколько строк занимает шапка файла.
    /// </summary>
    /// <remarks>
    /// Шапка — это комментарии и пустые строки в самом начале. Первая же
    /// запись её заканчивает: вставлять свой блок после чужого пина значит
    /// отдать ему первенство, ради которого блок и ставится наверх.
    /// </remarks>
    private static int HeaderLength(IReadOnlyList<string> lines)
    {
        int i = 0;

        while (i < lines.Count)
        {
            var text = lines[i].Trim().TrimStart('﻿');

            if (text.Length != 0 && !text.StartsWith('#'))
                break;

            i++;
        }

        return i;
    }

    /// <summary>Чужие действующие строки на те же имена.</summary>
    private static IReadOnlyList<string> Foreign(IReadOnlyList<string> lines, IEnumerable<string> names)
    {
        var watch = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var (start, end) = FindBlock(lines);
        var found = new List<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            if (start >= 0 && i >= start && i <= end)
                continue;

            if (Split(lines[i]) is { } pair && watch.Contains(pair.Name))
                found.Add($"{pair.Address} {pair.Name}");
        }

        return found;
    }

    /// <summary>Адрес и первое имя действующей строки; <c>null</c> — не запись.</summary>
    private static (string Address, string Name)? Split(string line)
    {
        var text = line.Trim().TrimStart('﻿');

        if (text.Length == 0 || text.StartsWith('#'))
            return null;

        var hash = text.IndexOf('#');

        if (hash >= 0)
            text = text[..hash];

        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 2 && IPAddress.TryParse(parts[0], out _)
            ? (parts[0], parts[1])
            : null;
    }

    private static string Backup(string path)
    {
        var backup = $"{path}.netzapret-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(path, backup, overwrite: true);
        return backup;
    }

    /// <summary>
    /// Записывает файл без BOM и с концами строк Windows.
    /// </summary>
    /// <remarks>
    /// BOM в hosts ломает разбор первой строки у части программ, а сам файл
    /// исторически ASCII с CRLF. Отступать от этого нам незачем.
    /// </remarks>
    private static void Write(string path, IReadOnlyList<string> lines)
    {
        var content = string.Join("\r\n", lines) + "\r\n";

        // Метка порядка байтов срезается, а не сохраняется. File.ReadAllLines
        // оставляет её в первой строке как обычный символ, и запись возвращала
        // её на место — перед прежней. В этом файле их накопилось уже
        // восемнадцать: каждая правка добавляла ещё одну.
        File.WriteAllText(path, content.TrimStart('﻿'), new UTF8Encoding(false));
    }

    /// <summary>Сбрасывает кэш DNS; без этого правка не вступит в силу.</summary>
    /// <remarks>
    /// Windows держит разрешённые имена с большим сроком жизни, и записи
    /// из hosts в том числе. Без сброса человек правит файл, ничего
    /// не меняется, и он справедливо решает, что редактор не работает.
    /// </remarks>
    public static bool FlushDns()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ipconfig",
                Arguments = "/flushdns",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            process?.WaitForExit(10_000);
            return process?.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
