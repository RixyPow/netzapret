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

    /// <summary>
    /// Кто вернул файл к своему умолчанию сразу после записи; <c>null</c> — блок на месте.
    /// </summary>
    /// <remarks>
    /// Запись удаётся, ошибки нет, а через мгновение защитник откатывает
    /// hosts — и сообщить «прибито» значило бы сообщить об успехе того,
    /// чего больше нет. Проверяла это только консоль; окно до 23.09
    /// сообщало успех не глядя.
    /// </remarks>
    public string? RevertedBy { get; init; }

    /// <summary>Что сказать человеку, если блок откатили; <c>null</c> — нечего.</summary>
    public string? Reverted => RevertedBy is null
        ? null
        : $"Записал — и записи уже нет: {RevertedBy} вернул hosts к своему умолчанию. "
          + "Пин на этой машине не живёт, пока файл hosts не внесён в доверенные.";
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
/// Отсюда правило, которое не менялось ни разу: перед всякой правкой делается
/// копия рядом с файлом — не «на всякий случай», а потому что единственная
/// ошибка здесь стоит работающей сети. Выключение при этом обратимо своими
/// силами, удаление — только из копии, и спрашивать перед ним обязательно.
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
    /// Удаляет названные строки из файла.
    /// </summary>
    /// <param name="lines">Номера строк из <see cref="HostsEntry.Line"/>.</param>
    /// <returns>Путь к копии, сделанной перед правкой.</returns>
    /// <remarks>
    /// <para>
    /// Здесь долго стояло «только выключение»: чужую запись ведёт кто-то ещё,
    /// и стереть её значит сломать то, что чинить будут не у нас. Владелец
    /// решил иначе 17.09 — в живом файле накопилось семьсот восемьдесят две
    /// чужие записи, половина от программ, которых на машине давно нет,
    /// и выключение оставляет их лежать всё теми же строками.
    /// </para>
    /// <para>
    /// Копия перед правкой обязательна и делается всегда. Это единственное,
    /// что отличает удаление в общем системном файле от потери: отменить
    /// нажатием уже нельзя, а вернуть файл целиком — можно.
    /// </para>
    /// <para>
    /// Удаляются ровно указанные строки. Остальной файл переписывается байт
    /// в байт, включая чужие комментарии, пустые строки и порядок: переписать
    /// «как мы понимаем формат» значит стереть то, чего мы не поняли.
    /// </para>
    /// </remarks>
    public static string Remove(IReadOnlyCollection<int> lines, string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;
        var content = File.ReadAllLines(target);
        var backup = Backup(target);

        // По номерам, а не по содержимому: две строки могут совпадать
        // дословно, и удалять «такую же» значило бы снять не ту.
        var drop = lines.Where(i => i >= 0 && i < content.Length).ToHashSet();

        if (drop.Count == 0)
            return backup;

        var kept = new List<string>(content.Length - drop.Count);

        for (int i = 0; i < content.Length; i++)
        {
            if (!drop.Contains(i))
                kept.Add(content[i]);
        }

        Write(target, [.. kept]);
        return backup;
    }

    /// <summary>
    /// Убирает названные имена из названных строк — и наших, и чужих.
    /// </summary>
    /// <param name="targets">Номер строки из <see cref="HostsEntry.Line"/> и имя в ней.</param>
    /// <returns>Путь к копии и сколько имён ушло на деле.</returns>
    /// <remarks>
    /// <para>
    /// Заведено по просьбе владельца 26.09: поиск по «jetbrains» нашёл
    /// сто двадцать две чужие записи, и все они вели на мёртвый прокси —
    /// удалять такое по одной с вопросом на каждую нельзя. Прежде здесь
    /// стояло «разом не чистим»; то правило писалось против кнопки
    /// «почистить всё», а удаляется только отобранное поиском.
    /// </para>
    /// <para>
    /// Имя, а не строка, потому что в строке их бывает несколько:
    /// «149.154.167.220 t.me api.telegram.org». Поиск по «t.me» снял бы
    /// заодно api.telegram.org, которого человек не выбирал. Строка уходит
    /// целиком, только когда в ней не осталось имён; иначе переписывается
    /// с оставшимися — с тем же адресом, выключенностью и подписью.
    /// </para>
    /// <para>
    /// Одним проходом и одной копией, наши и чужие вместе. Двумя вызовами
    /// не выйдет: копия названа с точностью до секунды, и вторая затёрла
    /// бы первую уже правленым файлом — прежний пропал бы молча.
    /// </para>
    /// <para>
    /// Имя, которого в строке уже нет, пропускается: между показом и нажатием
    /// файл мог переписать кто угодно, и номер строки стал бы чужим.
    /// </para>
    /// </remarks>
    public static (string Backup, int Removed) RemoveNames(
        IReadOnlyCollection<(int Line, string Name)> targets,
        string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;
        var content = File.ReadAllLines(target).ToList();
        var backup = Backup(target);

        var entries = Parse(target).ToDictionary(e => e.Line);
        int removed = 0;

        // С конца, чтобы удалённая строка не сдвигала номера ещё не тронутых.
        foreach (var group in targets.GroupBy(t => t.Line).OrderByDescending(g => g.Key))
        {
            if (!entries.TryGetValue(group.Key, out var entry))
                continue;

            var drop = group.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var left = entry.Names.Where(n => !drop.Contains(n)).ToList();
            int gone = entry.Names.Count - left.Count;

            if (gone == 0)
                continue;

            removed += gone;

            if (left.Count == 0)
            {
                content.RemoveAt(group.Key);
                continue;
            }

            content[group.Key] = (entry.Enabled ? string.Empty : "# ")
                + entry.Address + " " + string.Join(' ', left)
                + (entry.Note is { } note ? " # " + note : string.Empty);
        }

        // Опустевший наш блок уходит вместе с отметками — как и у Unpin:
        // иначе в файле копятся следы от сервисов, которых давно нет.
        var (start, end) = FindBlock(content);

        if (start >= 0 && !content.Skip(start + 1).Take(end - start - 1).Any(l => Split(l) is not null))
            content.RemoveRange(start, end - start + 1);

        Write(target, content);
        return (backup, removed);
    }

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

        return PinMany(many, path, note);
    }

    private static PinResult PinMany(
        IReadOnlyDictionary<string, IReadOnlyList<string>> entries,
        string? path,
        string? note)
    {
        var target = path ?? HostsFile.DefaultPath;
        var backup = File.Exists(target) ? Backup(target) : null;
        var lines = File.Exists(target) ? File.ReadAllLines(target).ToList() : [];

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

            // Закрепление заменяет прежний адрес имени: человек выбрал
            // новый набор взамен старого.
            var list = kept[name] = [];

            foreach (var address in addresses)
            {
                if (!list.Contains(address, StringComparer.OrdinalIgnoreCase))
                    list.Add(address);
            }
        }

        var block = new List<string> { BlockBegin };

        if (!string.IsNullOrWhiteSpace(note))
            block.Add("# " + note);

        block.Add("# Записи ведёт NetZapret. Правьте их в окне, «Файл hosts»: при следующей");
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

            // Перечитываем своими глазами: см. PinResult.RevertedBy.
            RevertedBy = BlockSurvived(target) ? null : WhoReplaced(target) ?? "защитник",
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

    /// <summary>
    /// Следы того, что файл hosts переписал за нас кто-то ещё.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Найдено на живой машине: Kaspersky считает изменённый hosts признаком
    /// заражения и заменяет файл своим — от него остаются четыре строки,
    /// <c>localhost</c> и объяснение в комментарии. Всё прибитое исчезает
    /// молча, вместе с нашим блоком и с чужими записями.
    /// </para>
    /// <para>
    /// Для программы это худший из возможных исходов: <see cref="Pins"/>
    /// честно возвращает пустоту, отчёт пишет «прибитых имён нет», и человек
    /// видит средство, которое отрицает работу, только что им проделанную.
    /// Сказать «файл переписали» можно лишь по оставленной записке, поэтому
    /// её и ищем.
    /// </para>
    /// </remarks>
    /// <returns>Кто переписал; <c>null</c> — следов нет.</returns>
    public static string? WhoReplaced(string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;

        if (!File.Exists(target))
            return null;

        string[] lines;

        try
        {
            lines = File.ReadAllLines(target);
        }
        catch (IOException)
        {
            return null;
        }

        foreach (var raw in lines.Take(20))
        {
            var line = raw.Trim();

            if (!line.StartsWith('#'))
                continue;

            // Совпадение по обоим признакам сразу: одно упоминание Касперского
            // ничего не значит — мало ли что человек записал в комментарий, —
            // а вот вместе с «заменён на версию по умолчанию» это его записка.
            bool replaced = line.Contains("replaced", StringComparison.OrdinalIgnoreCase)
                || line.Contains("заменён", StringComparison.OrdinalIgnoreCase)
                || line.Contains("заменен", StringComparison.OrdinalIgnoreCase);

            if (!replaced)
                continue;

            if (line.Contains("Kaspersky", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Касперск", StringComparison.OrdinalIgnoreCase))
            {
                return "Kaspersky";
            }

            // Иные защитники поступают так же и оставляют такую же записку.
            // Имени не знаем, но сам факт замены сказать обязаны.
            if (line.Contains("default version", StringComparison.OrdinalIgnoreCase)
                || line.Contains("версией по умолчанию", StringComparison.OrdinalIgnoreCase))
            {
                return "антивирус";
            }
        }

        return null;
    }

    /// <summary>
    /// Проверяет, что записанный блок и вправду лежит в файле.
    /// </summary>
    /// <remarks>
    /// Не паранойя, а измеренный случай. Запись в hosts удаётся, ошибки нет,
    /// а через мгновение защитник возвращает файл к своему умолчанию — и мы
    /// сообщаем об успехе того, чего больше не существует. Перечитываем
    /// и смотрим своими глазами.
    /// </remarks>
    public static bool BlockSurvived(string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;

        if (!File.Exists(target))
            return false;

        try
        {
            return FindBlock(File.ReadAllLines(target)).Start >= 0;
        }
        catch (IOException)
        {
            return false;
        }
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

    /// <summary>
    /// Наши прибитые имена, лежащие под зонами, — те, что перебьют маршрут.
    /// </summary>
    /// <remarks>
    /// Пин бьёт любое разрешение имени, в том числе наше: прибитое имя
    /// не получает адрес туннеля и уходит мимо VPN, какой маршрут ни выбери.
    /// 23.09 так и выходило с crunchyroll: «через VPN» выбран, а имена
    /// прибиты с прошлой попытки, и 1009 оставался. Окно спрашивает об этом
    /// в миг выбора VPN — отсюда оно узнаёт, о каких именах спрашивать.
    /// </remarks>
    public static IReadOnlyList<string> PinnedUnder(IEnumerable<string> zones, string? path = null)
    {
        var bare = zones.Select(z => z.Trim().TrimStart('*', '.')).Where(z => z.Length > 0).ToList();

        if (bare.Count == 0)
            return [];

        return Pins(path).Keys
            .Where(name => bare.Any(zone => string.Equals(name, zone, StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
