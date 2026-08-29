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
/// вроде адреса для превью Roblox должны доезжать.
/// </para>
/// </remarks>
public static class UpdateInstaller
{
    /// <summary>
    /// Файлы, которые обновление не перезаписывает ни при каких условиях.
    /// </summary>
    /// <remarks>
    /// Подписка — пароль, правила — работа человека. Всё прочее в архиве
    /// принадлежит программе, и его замена и есть обновление.
    /// </remarks>
    public static IReadOnlyList<string> Preserved { get; } =
    [
        Path.Combine("config", "netzapret.json"),
        Path.Combine("config", "rules.user.yaml"),
        Path.Combine("config", "addresses.yaml"),
    ];

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
    /// <remarks>
    /// Размер сверяется с обещанным. Оборванная закачка даёт архив, который
    /// распакуется частично и заменит половину файлов — состояние хуже,
    /// чем обе версии по отдельности.
    /// </remarks>
    public static async Task<UpdatePlan> StageAsync(
        ReleaseInfo release,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var staging = Path.GetFullPath(StagingDirectory);

        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);

        Directory.CreateDirectory(staging);

        var archive = Path.Combine(staging, "release.zip");

        await DownloadAsync(release, archive, progress, cancellationToken);

        var actual = new FileInfo(archive).Length;

        if (release.ArchiveSize > 0 && actual != release.ArchiveSize)
        {
            throw new InvalidOperationException(
                $"Скачано {actual} Б вместо обещанных {release.ArchiveSize} Б — закачка оборвалась.");
        }

        var unpacked = Path.Combine(staging, "files");
        ZipFile.ExtractToDirectory(archive, unpacked);
        File.Delete(archive);

        // Архив разворачивается в папку NetZapret\; нам нужно её содержимое,
        // а не она сама, иначе при замене получится NetZapret\NetZapret\.
        var root = Directory.GetDirectories(unpacked).Length == 1
            && Directory.GetFiles(unpacked).Length == 0
                ? Directory.GetDirectories(unpacked)[0]
                : unpacked;

        return new UpdatePlan
        {
            StagedAt = root,
            Files = Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length,
            Kept = Preserved.Where(File.Exists).ToList(),
        };
    }

    private static async Task DownloadAsync(
        ReleaseInfo release,
        string destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add("User-Agent", "NetZapret");

        using var response = await http.GetAsync(
            release.ArchiveUrl,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? release.ArchiveSize;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long done = 0;

        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);

            if (read == 0)
                break;

            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            done += read;

            if (total > 0)
                progress?.Report((double)done / total);
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
    public static string WriteApplyScript(UpdatePlan plan, string installedAt)
    {
        var script = Path.Combine(Path.GetFullPath(StagingDirectory), "apply.cmd");

        var lines = new List<string>
        {
            "@echo off",
            "rem Written by NetZapret to replace its own files after it exits.",
            "rem ASCII only: cmd.exe reads batch files in the OEM code page.",
            "setlocal",
            "",
            $"set \"SOURCE={plan.StagedAt}\"",
            $"set \"TARGET={installedAt.TrimEnd('\\')}\"",
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
            "echo Updating...",
        };

        // Настройки исключаются из копирования поимённо. /XF по имени файла
        // надёжнее, чем надежда на то, что их не окажется в архиве: архив
        // собирается другим сценарием, и связывать их молчаливым уговором
        // означает однажды его нарушить.
        var exclude = string.Join(' ', Preserved.Select(p => $"\"{Path.GetFileName(p)}\""));

        lines.Add($"robocopy \"%SOURCE%\" \"%TARGET%\" /E /R:3 /W:2 /NJH /NJS /NP /NDL /NFL /XF {exclude} >nul");
        lines.Add("if errorlevel 8 (");
        lines.Add("    echo Update failed. The previous version is untouched.");
        lines.Add("    pause");
        lines.Add("    exit /b 1");
        lines.Add(")");
        lines.Add("");
        lines.Add("echo Done.");
        lines.Add("start \"\" \"%TARGET%\\netzapret.exe\"");
        lines.Add("");

        // Удаляется только распакованное. Сам сценарий лежит уровнем выше
        // и стереть свой каталог не может: cmd.exe держит файл открытым,
        // пока читает его построчно, — попытка привела бы к молчаливому
        // отказу и мусору вместо обещанной чистоты. Остаток убирает
        // программа при следующем запуске, когда никто его уже не держит.
        lines.Add($"rd /s /q \"{plan.StagedAt}\" 2>nul");

        File.WriteAllLines(script, lines, System.Text.Encoding.ASCII);
        return script;
    }
}
