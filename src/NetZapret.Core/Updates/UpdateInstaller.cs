using System.IO.Compression;

namespace NetZapret.Core.Updates;

/// <summary>Чем закончилась подготовка обновления.</summary>
public sealed record UpdatePlan
{
    /// <summary>Куда распакован новый выпуск.</summary>
    public required string StagedAt { get; init; }

    /// <summary>Сколько файлов будет заменено.</summary>
    public required int Files { get; init; }

    /// <summary>Что останется нетронутым.</summary>
    public required IReadOnlyList<string> Kept { get; init; }

    /// <summary>Дописанное человеком в наши списки и перенесённое в новые: файл → строк.</summary>
    public IReadOnlyDictionary<string, int> CarriedLists { get; init; } = new Dictionary<string, int>();
}

/// <summary>
/// Скачивает выпуск и готовит подмену.
/// </summary>
/// <remarks>
/// <para>
/// Подменить себя на ходу нельзя: Windows держит запущенный exe и переписать
/// его не даст. Поэтому новое кладётся рядом, а замена происходит при
/// следующем запуске — и это не обход ограничения, а единственный честный
/// путь. Перезапуск всё равно нужен: движки держат драйвер, и без него
/// обновление не вступит в силу.
/// </para>
/// <para>
/// Настройки не трогаются. Не «стараемся не трогать», а перечислены поимённо:
/// обновление, стирающее подписку, хуже отсутствия обновления. Свои списки
/// в <c>config\lists\</c>, наоборот, обновляются — их ведём мы, и починки
/// вроде адреса для превью Roblox должны доезжать. Дописанное в них человеком
/// с 03.10 переносится в новые (<see cref="ListCarry"/>).
/// </para>
/// </remarks>
public static class UpdateInstaller
{
    /// <summary>
    /// Файлы, которые обновление не перезаписывает ни при каких условиях.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Подписка — пароль, правила — работа человека. Всё прочее в архиве
    /// принадлежит программе, и его замена и есть обновление.
    /// </para>
    /// <para>
    /// Каталог адресов сюда попал позже прочих и по той же причине: мы сами
    /// зовём в него дописывать сервисы, для которых нашёлся рабочий адрес.
    /// Стереть это обновлением значило бы забрать назад то, что человек
    /// добыл перебором адресов.
    /// </para>
    /// <para>
    /// У сохранения есть цена, и она известна: записи, добавленные в каталог
    /// нами, до тех, кто уже обновлялся, не дойдут — их файл останется своим.
    /// Из двух зол это меньшее: наши записи можно перенести руками, чужую
    /// работу не вернуть ничем.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Preserved { get; } =
    [
        Path.Combine("config", "netzapret.json"),
        Path.Combine("config", "rules.user.yaml"),
        Path.Combine("config", "addresses.yaml"),
        Path.Combine("config", "catalog.yaml"),
    ];

    /// <summary>Как программа зовётся в поставке.</summary>
    /// <remarks>
    /// Нужна ради переименования: до 0.5.3 окно звалось NetZapret.Gui.exe,
    /// и обновившийся получил бы в папке оба файла — новый рядом со старым,
    /// который никто не убирал.
    /// </remarks>
    private const string Canonical = "NetZapret.exe";

    /// <summary>Куда складываем распакованное до перезапуска.</summary>
    public static string StagingDirectory => Path.Combine("runtime", "update");

    /// <summary>
    /// Убирает остатки прошлого обновления.
    /// </summary>
    /// <remarks>
    /// Сценарий подмены не может стереть каталог, из которого сам исполняется:
    /// cmd.exe держит файл открытым, пока читает его построчно. Поэтому остаток
    /// убирается здесь — при следующем запуске, когда держать его уже некому.
    /// </remarks>
    public static void CleanUp()
    {
        try
        {
            var staging = Path.GetFullPath(StagingDirectory);

            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        catch (Exception)
        {
            // Не убралось — не беда: следующая попытка начнётся с очистки,
            // а место под один архив мы переживём.
        }
    }

    /// <summary>
    /// Скачивает архив и распаковывает его в отстойник.
    /// </summary>
    /// <param name="progress">Доля скачанного, от нуля до единицы.</param>
    /// <param name="tunnel">
    /// Вход в туннель (health-in движка) — если напрямую медленно; <c>null</c> — только напрямую.
    /// </param>
    /// <param name="note">Что сказать человеку, когда закачка ушла в туннель.</param>
    /// <remarks>
    /// <para>
    /// Размер сверяется с обещанным. Оборванная закачка даёт архив, который
    /// распакуется частично и заменит половину файлов — состояние хуже,
    /// чем обе версии по отдельности.
    /// </para>
    /// <para>
    /// Сперва напрямую: так не тратится трафик подписки, а у большинства
    /// GitHub отдаёт быстро. Медленнее <see cref="SlowBytesPerSecond"/> за первые
    /// <see cref="SlowJudgeAfter"/> — заново через туннель. 04.10 у Максима
    /// (Telegram) обновление 0.12.0 → 0.12.2 за 5–10 минут дошло до 2 %:
    /// его оператор режет файлы GitHub, а GitHub у нас по умолчанию идёт мимо
    /// туннеля.
    /// </para>
    /// </remarks>
    public static async Task<UpdatePlan> StageAsync(
        ReleaseInfo release,
        IProgress<double>? progress,
        CancellationToken cancellationToken,
        System.Net.IWebProxy? tunnel = null,
        IProgress<string>? note = null)
    {
        var staging = PrepareStaging(StagingDirectory);

        // Имя своё на каждую попытку: архив прошлой мог остаться в отстойнике
        // занятым (см. PrepareStaging), и писать поверх него было бы нельзя.
        var archive = Path.Combine(staging, $"release-{Guid.NewGuid():N}.zip");

        bool fast = await DownloadAsync(release, archive, progress, proxy: null, giveUpIfSlow: tunnel is not null, cancellationToken);

        if (!fast)
        {
            note?.Report("напрямую GitHub отдаёт медленно — качаю через туннель");
            progress?.Report(0);

            // Недокачанный остаётся в отстойнике — его уберёт следующая уборка.
            archive = Path.Combine(staging, $"release-{Guid.NewGuid():N}.zip");

            await DownloadAsync(release, archive, progress, tunnel, giveUpIfSlow: false, cancellationToken);
        }

        var actual = new FileInfo(archive).Length;

        if (release.ArchiveSize > 0 && actual != release.ArchiveSize)
        {
            throw new InvalidOperationException(
                $"Скачано {actual} Б вместо обещанных {release.ArchiveSize} Б — закачка оборвалась.");
        }

        var root = await UnpackAsync(archive, staging, cancellationToken);

        // Дописанное человеком в наши списки — в списки новой версии, пока
        // подмена их не перезаписала (ListCarry). Не вышло — обновляемся
        // как прежде: правки пропадут, но обновление не встанет.
        IReadOnlyDictionary<string, int> carried;

        try
        {
            carried = CarryListEdits(Path.GetFullPath("."), root);
        }
        catch (Exception)
        {
            carried = new Dictionary<string, int>();
        }

        return new UpdatePlan
        {
            StagedAt = root,
            Files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length,
            Kept = Preserved.Where(File.Exists).ToList(),
            CarriedLists = carried,
        };
    }

    /// <summary>Сколько ждать, пока чужой процесс отпустит скачанный архив.</summary>
    internal static TimeSpan ArchivePatience { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Готовит отстойник: убирает остатки прошлой попытки, сколько выйдет.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Не падает на занятом файле (жалоба 03.10: «Обновиться не вышло: The process
    /// cannot access the file 'release.zip' because it is being used by another
    /// process» — с коротким именем файла, такое .NET пишет при рекурсивном удалении
    /// каталога). Свежий архив на сотню мегабайт держат антивирус и индексатор,
    /// пока проверяют, а повторное нажатие «Обновить» начиналось с удаления
    /// отстойника целиком — и обновление вставало, хотя скачать и распаковать
    /// было можно.
    /// </para>
    /// <para>
    /// Занятое остаётся лежать: новая попытка пишет архив под своим именем
    /// и распаковывает в свой каталог, а остаток уберёт CleanUp при следующем
    /// запуске.
    /// </para>
    /// </remarks>
    internal static string PrepareStaging(string directory)
    {
        var staging = Path.GetFullPath(directory);

        try
        {
            if (Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
            {
                try
                {
                    if (Directory.Exists(entry))
                        Directory.Delete(entry, recursive: true);
                    else
                        File.Delete(entry);
                }
                catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
                {
                    // Держит кто-то другой — пусть лежит.
                }
            }
        }

        Directory.CreateDirectory(staging);
        return staging;
    }

    /// <summary>
    /// Распаковывает архив в свой каталог отстойника; возвращает корень новой версии.
    /// </summary>
    /// <remarks>
    /// Открыть архив пробует несколько раз: антивирус, проверяющий свежий файл,
    /// держит его считанные секунды, и отказ с первого раза ронял обновление
    /// без нужды. Сам архив после распаковки удаляется по возможности — занятый
    /// остаётся до CleanUp.
    /// </remarks>
    internal static async Task<string> UnpackAsync(string archive, string staging, CancellationToken cancellationToken)
    {
        var unpacked = Path.Combine(staging, $"files-{Guid.NewGuid():N}");
        var deadline = DateTime.UtcNow + ArchivePatience;

        while (true)
        {
            try
            {
                ZipFile.ExtractToDirectory(archive, unpacked);
                break;
            }
            catch (IOException) when (DateTime.UtcNow < deadline && IsHeld(archive))
            {
                // Частично распакованное — убрать: следующая попытка пишет туда же.
                try
                {
                    if (Directory.Exists(unpacked))
                        Directory.Delete(unpacked, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    unpacked = Path.Combine(staging, $"files-{Guid.NewGuid():N}");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }

        try
        {
            File.Delete(archive);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Занят — уберёт CleanUp при следующем запуске.
        }

        // Архив разворачивается в папку NetZapret\; нам нужно её содержимое,
        // а не она сама, иначе при замене получится NetZapret\NetZapret\.
        return Directory.GetDirectories(unpacked).Length == 1
            && Directory.GetFiles(unpacked).Length == 0
                ? Directory.GetDirectories(unpacked)[0]
                : unpacked;
    }

    /// <summary>Держит ли архив кто-то: открыть на чтение не выходит.</summary>
    private static bool IsHeld(string path)
    {
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// Переносит дописанное в наши списки из установки в распакованную новую версию.
    /// </summary>
    /// <param name="installedAt">Корень установки: там <c>config\lists</c> и <c>engines\zapret\lists</c>.</param>
    /// <param name="stagedAt">Корень распакованной новой версии.</param>
    public static IReadOnlyDictionary<string, int> CarryListEdits(string installedAt, string stagedAt) =>
        ListCarry.Apply(
            ListCarry.Additions(
                Path.Combine(installedAt, "config", "lists"),
                Path.Combine(installedAt, "engines", "zapret", "lists")),
            Path.Combine(stagedAt, "config", "lists"));

    /// <summary>Когда судить о скорости прямой закачки.</summary>
    public static readonly TimeSpan SlowJudgeAfter = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Медленнее этого напрямую — уходим в туннель.
    /// </summary>
    /// <remarks>
    /// Архив около 110 МБ: на 200 КБ/с это девять минут, медленнее — дольше,
    /// чем человек согласен ждать с кнопкой «Обновить».
    /// </remarks>
    public const long SlowBytesPerSecond = 200 * 1024;

    /// <summary>Скачанное — общее для потока закачки и сторожа скорости.</summary>
    private sealed class Counter
    {
        public long Bytes;
    }

    /// <returns><c>false</c> — бросили: напрямую медленнее <see cref="SlowBytesPerSecond"/>.</returns>
    private static async Task<bool> DownloadAsync(
        ReleaseInfo release,
        string destination,
        IProgress<double>? progress,
        System.Net.IWebProxy? proxy,
        bool giveUpIfSlow,
        CancellationToken cancellationToken)
    {
        using var handler = proxy is null ? new HttpClientHandler() : new HttpClientHandler { Proxy = proxy, UseProxy = true };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add("User-Agent", "NetZapret");

        // Сторож скорости — таймером, а не в цикле чтения: на заморозке
        // чтение не возвращается вовсе, и судить в цикле было бы некому.
        using var slow = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var done = new Counter();

        if (giveUpIfSlow)
        {
            _ = Task.Delay(SlowJudgeAfter, slow.Token).ContinueWith(
                waited =>
                {
                    if (!waited.IsCanceled
                        && Interlocked.Read(ref done.Bytes) < SlowBytesPerSecond * (long)SlowJudgeAfter.TotalSeconds)
                        slow.Cancel();
                },
                TaskScheduler.Default);
        }

        try
        {
            using var response = await http.GetAsync(
                release.ArchiveUrl,
                HttpCompletionOption.ResponseHeadersRead,
                slow.Token);

            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.ArchiveSize;

            await using var source = await response.Content.ReadAsStreamAsync(slow.Token);
            await using var target = File.Create(destination);

            var buffer = new byte[81920];

            while (true)
            {
                int read = await source.ReadAsync(buffer, slow.Token);

                if (read == 0)
                    break;

                await target.WriteAsync(buffer.AsMemory(0, read), slow.Token);
                long now = Interlocked.Add(ref done.Bytes, read);

                if (total > 0)
                    progress?.Report((double)now / total);
            }

            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException
            && giveUpIfSlow && slow.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>
    /// Пишет сценарий, который заменит файлы после выхода программы.
    /// </summary>
    /// <returns>Путь к сценарию.</returns>
    /// <remarks>
    /// <para>
    /// Сценарий, а не наш же код: заменить нужно и сам exe, а он к тому
    /// времени должен быть закрыт. Кто-то обязан пережить наше завершение,
    /// и внешний процесс здесь — не костыль, а единственный вариант.
    /// </para>
    /// <para>
    /// Ждёт выхода по идентификатору процесса, а не по времени. Пауза «на пять
    /// секунд» иногда истекает раньше, чем отпущен файл, и подмена срывается
    /// ровно тогда, когда её труднее всего заметить.
    /// </para>
    /// </remarks>
    /// <param name="relaunch">
    /// Что запустить после подмены. <c>null</c> — та же программа, которая
    /// обновлялась.
    /// </param>
    public static string WriteApplyScript(UpdatePlan plan, string installedAt, string? relaunch = null)
    {
        var script = Path.Combine(Path.GetFullPath(StagingDirectory), "apply.cmd");

        // Имя берётся у себя, а не зашито в код. Здесь стояло netzapret.exe,
        // и после перехода на окно обновление возвращало бы человека к файлу,
        // которого в поставке больше нет: подмена прошла бы, а программа
        // не открылась — худший исход из возможных, потому что выглядит
        // как «обновление сломало всё».
        var executable = relaunch
            ?? Path.GetFileName(Environment.ProcessPath)
            ?? Canonical;

        var lines = new List<string>
        {
            "@echo off",
            "rem Written by NetZapret to replace its own files after it exits.",
            "rem ASCII only: cmd.exe reads batch files in the OEM code page.",
            "setlocal",
            "",
            $"set \"SOURCE={plan.StagedAt}\"",
            $"set \"TARGET={installedAt.TrimEnd('\\')}\"",
            $"set \"LAUNCH={executable}\"",
            $"set \"LOG=%TARGET%\\{LogFile}\"",
            "",
            "rem Wait for the program to release its own executable. By process id",
            "rem rather than a fixed pause: a pause can expire while the file is",
            "rem still held, and the copy then fails at the least visible moment.",
            $"echo Waiting for NetZapret to exit...",
            $":wait",
            $"tasklist /fi \"pid eq %1\" 2>nul | find \"%1\" >nul",
            "if not errorlevel 1 (",
            "    timeout /t 1 /nobreak >nul",
            "    goto wait",
            ")",
            "",

            // Окно — не единственный, кто держит файлы папки: надзор — та же
            // программа, рядом winws2 и sing-box, и пережившая остановку их
            // копия держит свой файл до конца. Гасится всё, что запущено
            // именно отсюда, по пути, а не по имени: чужой winws2 из Zapret
            // GUI здесь ни при чём. Драйвер WinDivert держит свой .sys, пока
            // загружен, — та же выгрузка, что у остановки движков.
            "echo Stopping what is left running from this folder...",
            "powershell -NoProfile -ExecutionPolicy Bypass -Command \"Get-Process | Where-Object { $_.Path -and $_.Path.StartsWith($env:TARGET + '\\', [StringComparison]::OrdinalIgnoreCase) } | Stop-Process -Force -ErrorAction SilentlyContinue\" >nul 2>&1",
        };

        foreach (var service in DriverServices)
            lines.Add($"sc stop {service} >nul 2>&1");

        lines.Add("timeout /t 2 /nobreak >nul");
        lines.Add("");
        lines.Add("echo Updating...");

        // Настройки исключаются из копирования поимённо. /XF по имени файла
        // надёжнее, чем надежда на то, что их не окажется в архиве: архив
        // собирается другим сценарием, и связывать их молчаливым уговором
        // означает однажды его нарушить.
        var exclude = string.Join(' ', Preserved.Select(p => $"\"{Path.GetFileName(p)}\""));

        // Журнал robocopy — в файл и на экран (/TEE). До 06.10 вывод уходил
        // в nul, и у пользователя с «Update failed» не осталось ни слова о том,
        // какой файл не заменился и почему: занят, отказано, исчез из отстойника.
        // Попыток десять, а не три: файл отпускают секундами, а не мгновенно.
        lines.Add($"robocopy \"%SOURCE%\" \"%TARGET%\" /E /R:10 /W:2 /NJH /NP /NDL /NFL /XF {exclude} /UNILOG:\"%LOG%\" /TEE");
        lines.Add("if errorlevel 8 goto failed");
        lines.Add("");
        lines.Add("echo Done.");

        // Окно звалось NetZapret.Gui.exe до 0.5.3. robocopy добавляет файлы,
        // но не убирает, поэтому прежний остаётся лежать рядом — и перезапуск
        // поднял бы именно его, то есть прежнюю версию сразу после удачного
        // обновления. Со стороны: обновились, а версия та же.
        //
        // Двумя отдельными строками, а не одним блоком: без отложенного
        // раскрытия переменная внутри скобок читается до присваивания.
        if (!string.Equals(executable, Canonical, StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"if exist \"%TARGET%\\{Canonical}\" del /q \"%TARGET%\\{executable}\" >nul 2>&1");
            lines.Add($"if exist \"%TARGET%\\{Canonical}\" set \"LAUNCH={Canonical}\"");
        }

        lines.Add("start \"\" \"%TARGET%\\%LAUNCH%\"");
        lines.Add("");

        // Удаляется только распакованное. Сам сценарий лежит уровнем выше
        // и стереть свой каталог не может: cmd.exe держит файл открытым,
        // пока читает его построчно, — попытка привела бы к молчаливому
        // отказу и мусору вместо обещанной чистоты. Остаток убирает
        // программа при следующем запуске, когда никто его уже не держит.
        lines.Add($"rd /s /q \"{plan.StagedAt}\" 2>nul");
        lines.Add("exit /b 0");
        lines.Add("");

        // Прежде здесь стояло «The previous version is untouched» — неправда:
        // robocopy заменяет файл за файлом, и всё, что успело до сбоя, уже
        // новое. И программа не открывалась вовсе: человек оставался без
        // обхода, пока не запустит её сам. Теперь — честно и с запуском.
        lines.Add(":failed");
        lines.Add("echo.");
        lines.Add("echo Update failed: some files could not be replaced.");
        lines.Add("echo Files copied before the error are new already, the rest are old.");
        lines.Add("echo Details: %LOG%");
        lines.Add("echo NetZapret starts again. Restart Windows and update once more.");
        lines.Add("start \"\" \"%TARGET%\\%LAUNCH%\"");
        lines.Add("pause");
        lines.Add("exit /b 1");

        File.WriteAllLines(script, lines, System.Text.Encoding.ASCII);
        return script;
    }

    /// <summary>Журнал подмены в корне установки — для разбора, едет в отчёт.</summary>
    public static string LogFile => Path.Combine("runtime", "update.log");

    /// <summary>Журнал неудачной подмены, отложенный окном после записи в свой журнал.</summary>
    public static string FailedLogFile => Path.Combine("runtime", "update-failed.log");

    /// <summary>Службы драйвера WinDivert, выгружаемые перед подменой (как в WinDivertDriver).</summary>
    internal static IReadOnlyList<string> DriverServices { get; } = ["Monkey", "WinDivert"];

    /// <summary>
    /// Что не заменилось при прошлой подмене — строки ошибок из её журнала; пусто, если всё встало.
    /// </summary>
    /// <remarks>
    /// Окно зовёт это при запуске и пишет найденное в журнал: человек видит
    /// «Update failed» в чёрном окне и закрывает его, а нам при разборе
    /// отчёта нужна строка с именем файла и кодом ошибки.
    /// </remarks>
    public static IReadOnlyList<string> FailedFiles(string? root = null)
    {
        var path = Path.Combine(root ?? ".", LogFile);

        try
        {
            if (!File.Exists(path))
                return [];

            return File.ReadAllLines(path)
                .Where(l => l.Contains("ERROR ", StringComparison.Ordinal))
                .Select(l => l[l.IndexOf("ERROR ", StringComparison.Ordinal)..].Trim())
                .Distinct()
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }
}
