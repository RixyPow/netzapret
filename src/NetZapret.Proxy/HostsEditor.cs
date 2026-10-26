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

            content[group.Key] = Format(entry.Enabled, entry.Address, left, entry.Note);
        }

        // Опустевший наш блок уходит вместе с отметками — как и у Unpin:
        // иначе в файле копятся следы от сервисов, которых давно нет.
        var (start, end) = FindBlock(content);

        if (start >= 0 && !Own(content, start, end).Any(l => (Split(l) ?? SplitDisabled(l)) is not null))
            DropBlock(content, start, end);

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
    /// <param name="progress">Каждый проверенный адрес — сразу, как ответил или промолчал.</param>
    /// <remarks>
    /// По восемь адресов разом (19.10): раздел проверяет теперь весь файл,
    /// у владельца это 43 адреса, и молчащий ждёт 4 с — по одному вышло бы
    /// под три минуты. Адреса разные, так что залпом в один узел это не бьёт.
    /// </remarks>
    public static async Task<IReadOnlyList<PinHealth>> CheckAsync(
        IReadOnlyList<HostsEntry> entries,
        CancellationToken cancellationToken,
        IProgress<PinHealth>? progress = null)
    {
        var byAddress = entries
            .Where(e => e.Enabled)
            .GroupBy(e => e.Address, StringComparer.Ordinal)
            .ToList();

        var result = new List<PinHealth>();

        // Спрашивается один раз: без IPv6 в сети всякий адрес IPv6 молчит,
        // и без этой поправки проверка объявила бы мёртвыми рабочие записи.
        bool haveIpV6 = await HasIpV6Async(cancellationToken);

        using var gate = new SemaphoreSlim(8);

        async Task CheckOne(IGrouping<string, HostsEntry> group)
        {
            // Петля и нули ставят, чтобы имя не открывалось. Это не мёртвый
            // адрес, а намеренная заглушка, и тревожить о ней незачем.
            if (IPAddress.TryParse(group.Key, out var parsed) && BlockCheck.IsStub(parsed))
                return;

            int names = group.Sum(g => g.Names.Count);
            PinHealth health;

            if (!haveIpV6 && parsed?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                health = new PinHealth
                {
                    Address = group.Key,
                    Names = names,
                    Alive = false,
                    Tested = false,
                    Skipped = "в этой сети нет IPv6",
                };
            }
            else
            {
                await gate.WaitAsync(cancellationToken);

                try
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    bool alive = await AnswersAsync(group.Key, cancellationToken);

                    health = new PinHealth
                    {
                        Address = group.Key,
                        Names = names,
                        Alive = alive,
                        Elapsed = watch.Elapsed,
                    };
                }
                finally
                {
                    gate.Release();
                }
            }

            lock (result)
                result.Add(health);

            progress?.Report(health);
        }

        await Task.WhenAll(byAddress.Select(CheckOne));

        return result.OrderByDescending(r => r.Names).ToList();
    }

    /// <summary>Есть ли в этой сети IPv6 вообще.</summary>
    /// <remarks>
    /// Проверяется соединением к публичному резолверу Google по IPv6.
    /// Наличие адреса на адаптере не годится: Windows раздаёт себе локальные
    /// адреса и без всякой связи наружу. Открыт и решениям с пином
    /// (<see cref="PinSolutions"/>): адрес IPv6 в hosts без IPv6 в сети — это
    /// лишняя попытка соединения перед каждым IPv4, а решение, живущее только
    /// на IPv6, не работает вовсе.
    /// </remarks>
    public static async Task<bool> HasIpV6Async(CancellationToken cancellationToken)
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
    /// Пометка строки нашего блока: этому пину нужен десинк, щит его не берёт.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Пины бывают двух родов, и отличить их по самому файлу нельзя. Пин
    /// на посредника (XBOX DNS, Comss) — чужой узел; рецепт, выверенный
    /// на настоящей сети доставки, рвёт с ним соединение, и щит такой пин
    /// держит правильно. Пин на собственный адрес сервиса (раздел «Напрямую»
    /// Zapret GUI: Meta, Twitter, Google) только выбирает незакрытый адрес,
    /// а имя в приветствии TLS провайдер режет и там — без десинка такой пин
    /// мёртв, и щит его убивает.
    /// </para>
    /// <para>
    /// Замер 06.10, A/B/A через дыру в щите на работающем winws2: chatgpt.com
    /// и notion.so на прокси XBOX — со щитом 20 из 20, под рецептом своей
    /// секции 0 из 10; www.instagram.com, scontent.cdninstagram.com
    /// и abs-0.twimg.com на адресах из каталога — со щитом 0 из 22, под
    /// рецептом 6 из 6. До 06.10 щит брал любой пин (у JohnnyTargo, №18,
    /// Instagram на адресах Meta не открывался ни одним рецептом).
    /// </para>
    /// <para>
    /// Род пина пишется в сам файл, а не в настройки: так его видят все,
    /// кто читает hosts, — щит, «Куда пойдёт соединение», проверка
    /// блокировок, — и пометка не расходится с записью, к которой относится.
    /// Windows комментарий в строке hosts пропускает.
    /// </para>
    /// </remarks>
    public const string DesyncMark = "nz:desync";

    /// <summary>
    /// Прибивает имена к адресам самого сервиса — с пометкой <see cref="DesyncMark"/>.
    /// </summary>
    public static PinResult PinWithDesync(
        IReadOnlyDictionary<string, IReadOnlyList<string>> entries,
        string? path = null,
        string? note = null) => PinMany(entries, path, note, desync: true);

    /// <summary>
    /// Имена нашего блока с пометкой <see cref="DesyncMark"/> — их пины десинку открыты.
    /// </summary>
    /// <remarks>
    /// Только из нашего блока: чужую строку мы не ставили и о её адресе
    /// ничего не знаем.
    /// </remarks>
    public static IReadOnlySet<string> DesyncPins(string? path = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var target = path ?? HostsFile.DefaultPath;

        try
        {
            if (!File.Exists(target))
                return result;

            var lines = File.ReadAllLines(target);
            var (start, end) = FindBlock(lines);

            if (start < 0)
                return result;

            foreach (var line in Own(lines, start, end))
            {
                if (Split(line) is { } pair && Marked(line))
                    result.Add(pair.Name);
            }
        }
        catch (IOException)
        {
            // Не прочитался — значит пометок не видно, и щит поступит
            // по-прежнему: возьмёт пин. Это безопасная сторона.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return result;
    }

    /// <summary>Несёт ли строка пометку <see cref="DesyncMark"/> в комментарии.</summary>
    private static bool Marked(string line)
    {
        int hash = line.IndexOf('#');

        return hash >= 0
            && line[(hash + 1)..].Contains(DesyncMark, StringComparison.OrdinalIgnoreCase);
    }

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

    /// <summary>Прибивает имена сразу к нескольким адресам: Windows пробует их по очереди.</summary>
    public static PinResult Repin(
        IReadOnlyDictionary<string, IReadOnlyList<string>> entries,
        string? path = null,
        string? note = null) => PinMany(entries, path, note);

    /// <summary>Наши имена со всеми их адресами; пусто, если блока нет.</summary>
    /// <remarks>
    /// <see cref="Pins"/> отдаёт по одному адресу на имя — последнему. Посредник
    /// XBOX DNS раздаёт по два (.201 и .203, 28.09), и судить о живости имени
    /// по одному значило бы менять пин, у которого второй адрес работает.
    /// </remarks>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> PinsAll(string? path = null)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var target = path ?? HostsFile.DefaultPath;

        if (!File.Exists(target))
            return new Dictionary<string, IReadOnlyList<string>>();

        var lines = File.ReadAllLines(target).ToList();
        var (start, end) = FindBlock(lines);

        if (start >= 0)
        {
            foreach (var line in Own(lines, start, end))
            {
                if (Split(line) is not { } pair)
                    continue;

                if (!result.TryGetValue(pair.Name, out var list))
                    result[pair.Name] = list = [];

                if (!list.Contains(pair.Address, StringComparer.OrdinalIgnoreCase))
                    list.Add(pair.Address);
            }
        }

        return result.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="desync">
    /// Род пина у имён из <paramref name="entries"/>: <c>true</c> — адрес самого
    /// сервиса, строка получает <see cref="DesyncMark"/>. У прочих имён блока
    /// пометка остаётся, какой была.
    /// </param>
    private static PinResult PinMany(
        IReadOnlyDictionary<string, IReadOnlyList<string>> entries,
        string? path,
        string? note,
        bool desync = false)
    {
        var target = path ?? HostsFile.DefaultPath;
        var backup = File.Exists(target) ? Backup(target) : null;
        var lines = File.Exists(target) ? File.ReadAllLines(target).ToList() : [];

        var (start, end) = FindBlock(lines);
        var kept = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Выключенные в окне строки блока (SetEnabled) переживают перезапись
        // как есть: без этого первое же закрепление стирало бы их молча.
        var disabled = new List<(string Name, string Line)>();

        // Без новой подписи блок сохраняет прежнюю: её пишет окно при закреплении
        // («Claude — XBOX DNS»), а само обновление пинов (PinRefresh) ничего
        // нового о происхождении блока не знает.
        if (note is null && start >= 0 && start + 1 < end
            && lines[start + 1].StartsWith("# ", StringComparison.Ordinal)
            && !lines[start + 1].StartsWith("# Записи ведёт", StringComparison.Ordinal))
        {
            note = lines[start + 1][2..];
        }

        // Уже прибитое нами сохраняется: закрепляют по одному сервису,
        // и переписывать блок целиком значило бы снимать все прежние.
        var nested = new List<string>();
        var at = -1;

        if (start >= 0)
        {
            foreach (var line in Own(lines, start, end))
            {
                if (SplitDisabled(line) is { } off)
                {
                    disabled.Add((off.Name, line.Trim()));
                    continue;
                }

                if (Split(line) is not { } pair)
                    continue;

                if (!kept.TryGetValue(pair.Name, out var list))
                    kept[pair.Name] = list = [];

                if (!list.Contains(pair.Address, StringComparer.OrdinalIgnoreCase))
                    list.Add(pair.Address);

                if (Marked(line))
                    marked.Add(pair.Name);
            }

            // Чужой блок внутри нашего — не наш: см. Nested.
            nested = Nested(lines, start, end);
            at = start;
            lines.RemoveRange(start, end - start + 1);
        }

        foreach (var (rawName, addresses) in entries)
        {
            var name = rawName.TrimStart('*', '.');

            // Закрепление заменяет прежний адрес имени: человек выбрал
            // новый набор взамен старого. Род пина — тоже: посредник,
            // прибитый поверх адреса сервиса, пометку терять обязан.
            var list = kept[name] = [];

            if (desync)
                marked.Add(name);
            else
                marked.Remove(name);

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
            var mark = marked.Contains(name) ? " # " + DesyncMark : string.Empty;

            foreach (var address in addresses)
                block.Add($"{address} {name}{mark}");
        }

        // Прибитое заново заменяет и выключенную строку того же имени.
        foreach (var (name, line) in disabled)
        {
            if (!entries.Keys.Any(n => string.Equals(n.TrimStart('*', '.'), name, StringComparison.OrdinalIgnoreCase)))
                block.Add(line);
        }

        block.Add(BlockEnd);
        block.Add(string.Empty);

        // Блок встаёт на прежнее место, а впервые — в начало, после шапки
        // из комментариев: она объясняет формат файла, и вытеснять её вниз
        // незачем. Вложенные чужие блоки — прямо перед нашим: их строки
        // стояли выше наших записей и выше и остаются, так что разрешение
        // имён от нашей перезаписи не меняется.
        if (nested.Count > 0)
            block.InsertRange(0, nested);

        int place = at >= 0 ? at : HeaderLength(lines);

        // Пустая строка после блока уже есть — вторую не добавляем, иначе
        // каждая перезапись на прежнем месте копила бы их по одной.
        if (place < lines.Count && lines[place].Trim().Length == 0)
            block.RemoveAt(block.Count - 1);

        lines.InsertRange(place, block);

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
        var pinned = 0;

        var foreign = NestedLines(lines, start, end);

        // Выключенные строки блока тоже наши: снимаются по имени вместе
        // с действующими и держат блок, пока они в нём есть. Строки
        // вложенного чужого блока не наши и не трогаются.
        for (int i = end - 1; i > start; i--)
        {
            if (foreign.Contains(i))
                continue;

            bool on = Split(lines[i]) is not null;

            if ((Split(lines[i]) ?? SplitDisabled(lines[i])) is not { } pair)
                continue;

            if (drop.Contains(pair.Name))
            {
                lines.RemoveAt(i);
                continue;
            }

            left++;

            if (on)
                pinned++;
        }

        // Пустой блок убирается целиком — иначе в файле копятся наши следы
        // от сервисов, которых давно нет.
        if (left == 0)
        {
            (start, end) = FindBlock(lines);

            if (start >= 0)
                DropBlock(lines, start, end);
        }

        Write(target, lines);

        return new PinResult { Backup = backup, Pinned = pinned, Shadowed = [] };
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

        foreach (var line in Own(lines, start, end))
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
    /// Строки нашего блока без вложенных чужих блоков.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Zapret GUI ставит свой блок «перед первой строкой-записью»
    /// (<c>_insert_managed_hosts_block</c> в <c>src/hosts/hosts.py</c>,
    /// прочитано 19.10). Наш блок стоит первым, первая запись файла — наша,
    /// и всякий раз, как Zapret GUI применяет пины, его блок встаёт внутрь
    /// нашего: так у владельца 19.10, строки 136–190 внутри 131–275.
    /// Помешать этому мы не можем, значит, обязаны так жить.
    /// </para>
    /// <para>
    /// До 19.10 всё между нашими отметками считалось нашим: 82 записи Zapret GUI
    /// числились нашими пинами, а первое же закрепление стирало его разметку
    /// и переписывало его записи в наш блок как свои.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Own(IReadOnlyList<string> lines, int start, int end)
    {
        var nested = NestedLines(lines, start, end);

        for (int i = start + 1; i < end; i++)
        {
            if (!nested.Contains(i))
                yield return lines[i];
        }
    }

    /// <summary>Номера строк вложенных чужих блоков — вместе с их отметками.</summary>
    private static HashSet<int> NestedLines(IReadOnlyList<string> lines, int start, int end)
    {
        var result = new HashSet<int>();
        int depth = 0;

        for (int i = start + 1; i < end; i++)
        {
            var text = lines[i].Trim();

            if (text.StartsWith("# >>>", StringComparison.Ordinal))
                depth++;

            if (depth > 0)
                result.Add(i);

            if (depth > 0 && text.StartsWith("# <<<", StringComparison.Ordinal))
                depth--;
        }

        return result;
    }

    /// <summary>Вложенные чужие блоки дословно, в прежнем порядке.</summary>
    private static List<string> Nested(IReadOnlyList<string> lines, int start, int end) =>
        NestedLines(lines, start, end).Order().Select(i => lines[i]).ToList();

    /// <summary>
    /// Убирает наш блок, оставляя на его месте вложенные чужие.
    /// </summary>
    /// <remarks>
    /// Опустевший наш блок уходит, но блок Zapret GUI внутри него — не наш,
    /// и стереть его значило бы снять чужие пины молча.
    /// </remarks>
    private static void DropBlock(List<string> lines, int start, int end)
    {
        var nested = Nested(lines, start, end);

        lines.RemoveRange(start, end - start + 1);
        lines.InsertRange(start, nested);
    }

    /// <summary>Открывает ли строка чужой блок или раздел (<c># &gt;&gt;&gt;</c>, <c># --- … ---</c>).</summary>
    private static bool OpensForeign(string text) =>
        text.StartsWith("# >>>", StringComparison.Ordinal)
        || (text.StartsWith("# ---", StringComparison.Ordinal) && text.EndsWith("---", StringComparison.Ordinal));

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

            // Чужой блок или раздел — уже не шапка: встав после его
            // открывающей строки, наш блок оказался бы внутри чужого.
            if (OpensForeign(text))
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
        var nested = start >= 0 ? NestedLines(lines, start, end) : [];
        var found = new List<string>();

        for (int i = 0; i < lines.Count; i++)
        {
            // Вложенный чужой блок — чужие строки, хоть и внутри нашего.
            if (start >= 0 && i >= start && i <= end && !nested.Contains(i))
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

    /// <summary>Адрес и первое имя выключенной строки; <c>null</c> — не выключенная запись.</summary>
    /// <remarks>
    /// Под решёткой должны стоять адрес и имя, иначе это настоящий комментарий —
    /// заголовок нашего блока, подпись сервиса.
    /// </remarks>
    private static (string Address, string Name)? SplitDisabled(string line)
    {
        var text = line.Trim().TrimStart('﻿');

        return text.StartsWith('#') ? Split(text.TrimStart('#')) : null;
    }

    /// <summary>
    /// Включает или выключает имена в названных строках — решёткой в начале строки.
    /// </summary>
    /// <param name="targets">Номер строки из <see cref="HostsEntry.Line"/> и имя в ней.</param>
    /// <returns>Путь к копии и сколько имён сменило состояние.</returns>
    /// <remarks>
    /// <para>
    /// Заведено 19.10 под редизайн раздела: выключенная запись лежит в файле
    /// и возвращается одним нажатием — так пин снимают на время, для замера
    /// A/B/A, не набирая потом адрес заново.
    /// </para>
    /// <para>
    /// Имя, а не строка: в строке их бывает несколько, и выключить
    /// <c>t.me</c> не значит выключить соседний <c>api.telegram.org</c>.
    /// Выбранные имена тогда уходят отдельной строкой сразу под прежней,
    /// с тем же адресом и подписью.
    /// </para>
    /// <para>
    /// Строка правится по свежему разбору; имя, которого в ней уже нет, пропускается —
    /// между показом и нажатием файл мог переписать кто угодно.
    /// </para>
    /// </remarks>
    public static (string Backup, int Changed) SetEnabled(
        IReadOnlyCollection<(int Line, string Name)> targets,
        bool enabled,
        string? path = null)
    {
        var target = path ?? HostsFile.DefaultPath;
        var content = File.ReadAllLines(target).ToList();
        var backup = Backup(target);

        var entries = Parse(target).ToDictionary(e => e.Line);
        int changed = 0;

        // С конца: вставленная строка не должна сдвигать ещё не тронутые.
        foreach (var group in targets.GroupBy(t => t.Line).OrderByDescending(g => g.Key))
        {
            if (!entries.TryGetValue(group.Key, out var entry) || entry.Enabled == enabled)
                continue;

            var wanted = group.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var picked = entry.Names.Where(wanted.Contains).ToList();
            var rest = entry.Names.Where(n => !wanted.Contains(n)).ToList();

            if (picked.Count == 0)
                continue;

            changed += picked.Count;

            if (rest.Count == 0)
            {
                content[group.Key] = Format(enabled, entry.Address, entry.Names, entry.Note);
                continue;
            }

            content[group.Key] = Format(entry.Enabled, entry.Address, rest, entry.Note);
            content.Insert(group.Key + 1, Format(enabled, entry.Address, picked, entry.Note));
        }

        Write(target, content);
        return (backup, changed);
    }

    private static string Format(bool enabled, string address, IEnumerable<string> names, string? note) =>
        (enabled ? string.Empty : "# ") + address + " " + string.Join(' ', names)
        + (note is { } text ? " # " + text : string.Empty);

    private static string Backup(string path)
    {
        var backup = $"{path}.netzapret-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
        File.Copy(path, backup, overwrite: true);
        PruneBackups(path);
        return backup;
    }

    /// <summary>Сколько последних копий hosts хранить сверх самой первой.</summary>
    internal const int BackupsKept = 3;

    /// <summary>
    /// Убирает лишние копии hosts: остаются самая первая и
    /// <see cref="BackupsKept"/> последних.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Копия делается перед каждой записью, а убирать их было некому: каждый
    /// поставленный или снятый пин добавлял файл в системную папку. 07.10
    /// пользователь нашёл рядом с hosts шесть копий за 11 секунд — «кто все
    /// эти хосты».
    /// </para>
    /// <para>
    /// Первая — hosts до первой правки NetZapret, единственная, по которой
    /// можно вернуть всё как было, поэтому она остаётся. Последние — откат
    /// недавней правки. Промежуточные не нужны никому. Порядок — по имени:
    /// метка времени в нём идёт от года к секунде, и порядок строк совпадает
    /// с порядком времени. Удаляются только файлы ровно нашего вида, с меткой
    /// времени: маска «hosts.netzapret-*» захватила бы любой файл с таким
    /// началом, а папка системная.
    /// </para>
    /// <para>
    /// Не удалось удалить — не беда: запись hosts важнее уборки.
    /// </para>
    /// </remarks>
    internal static void PruneBackups(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var ours = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(name) + @"\.netzapret-\d{8}-\d{6}\.bak$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var copies = Directory.GetFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, name + ".netzapret-*")
                .Where(file => ours.IsMatch(Path.GetFileName(file)))
                .OrderBy(file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var stale in copies.Skip(1).SkipLast(BackupsKept))
            {
                try
                {
                    File.Delete(stale);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
        // На телефоне сбрасывать нечего и нечем: пины читает движок из своего
        // файла и сам замечает правку (не чаще раза в 5 с), ipconfig там нет.
        if (!OperatingSystem.IsWindows())
            return true;

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
