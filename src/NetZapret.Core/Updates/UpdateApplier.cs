using System.Diagnostics;
using System.Text;

namespace NetZapret.Core.Updates;

/// <summary>Сколько файлов уже заменено.</summary>
public sealed record ApplyProgress(int Done, int Total, string File);

/// <summary>Файл не заменился: какой, почему и кто его держит.</summary>
public sealed record ApplyFailure(string File, string Reason, IReadOnlyList<FileLocker> Lockers);

/// <summary>
/// Подмена файлов установки новыми — с копией прежних и откатом.
/// </summary>
/// <remarks>
/// <para>
/// Заменяет сценарий apply.cmd (владелец 12.10: своё окно вместо чёрного cmd,
/// «и ещё добавим возможность откатить при неудаче»). Сценарий копировал
/// robocopy: при сбое половина файлов оставалась новой, половина старой,
/// а человек видел «Update failed» по-английски — без имени файла и того,
/// кто его держит. Вернуть прежнюю версию было нечем.
/// </para>
/// <para>
/// Каждый заменяемый файл перед заменой копируется в <c>backup</c>, новые —
/// запоминаются: откат возвращает первые и стирает вторые. Повтор после сбоя
/// продолжает с того же места — заменённое второй раз не трогается, и копия
/// прежнего не перезаписывается новым.
/// </para>
/// <para>
/// Личные файлы (<see cref="UpdateInstaller.Preserved"/>) не трогаются — по
/// имени файла, как исключал их robocopy (<c>/XF</c>).
/// </para>
/// </remarks>
public sealed class UpdateApplier
{
    private readonly string _source;
    private readonly string _target;
    private readonly string _backup;
    private readonly string _log;
    private readonly IReadOnlyList<string> _files;
    private readonly HashSet<string> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _backedUp = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="source">Распакованный новый выпуск.</param>
    /// <param name="target">Корень установки.</param>
    /// <param name="backup">Куда класть прежние файлы.</param>
    public UpdateApplier(string source, string target, string backup)
    {
        _source = Path.GetFullPath(source);
        _target = Path.GetFullPath(target);
        _backup = Path.GetFullPath(backup);
        _log = Path.Combine(_target, UpdateInstaller.LogFile);

        var keep = new HashSet<string>(UpdateInstaller.Preserved.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);

        // Сама программа — последней: при сбое посреди подмены она остаётся
        // прежней и запускается с прежними файлами вокруг, а не новой среди старых.
        _files = Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(_source, f))
            .Where(f => !keep.Contains(Path.GetFileName(f)))
            .OrderBy(f => string.Equals(Path.GetFileName(f), UpdateInstaller.Canonical, StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public int Total => _files.Count;

    public int Done => _done.Count;

    /// <summary>Есть ли что откатывать.</summary>
    public bool CanRollBack => _backedUp.Count > 0 || _created.Count > 0;

    /// <summary>Сколько попыток на файл и пауза между ними: файл отпускают секундами, а не мгновенно.</summary>
    internal static int Attempts { get; set; } = 10;

    internal static TimeSpan Pause { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Ждёт, пока прежняя программа отпустит свой exe.</summary>
    public static async Task<bool> WaitForExitAsync(int processId, TimeSpan limit, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(limit, cancellationToken);
            return true;
        }
        catch (ArgumentException)
        {
            // Уже вышла.
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Гасит всё, что запущено из папки установки, и выгружает драйвер перехвата.
    /// </summary>
    /// <remarks>
    /// Окно — не единственный, кто держит файлы папки: надзор — та же программа,
    /// рядом winws2 и sing-box, и пережившая остановку их копия держит свой
    /// файл до конца. По пути, а не по имени: чужой winws2 из Zapret GUI здесь
    /// ни при чём. Драйвер WinDivert держит свой .sys, пока загружен.
    /// Подменщик сам запущен из runtime\update — его это не касается.
    /// </remarks>
    public void StopLeftovers()
    {
        var prefix = _target.TrimEnd('\\') + "\\";
        int self = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.Id == self)
                    continue;

                var path = process.MainModule?.FileName;

                if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (path.StartsWith(Path.Combine(_target, UpdateInstaller.StagingDirectory) + "\\", StringComparison.OrdinalIgnoreCase))
                    continue;

                Log($"снимаю {Path.GetFileName(path)} (PID {process.Id})");
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception)
            {
                // Чужой процесс без доступа к пути — не наш.
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var service in UpdateInstaller.DriverServices)
        {
            try
            {
                using var sc = Process.Start(new ProcessStartInfo("sc.exe", $"stop {service}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });

                sc?.WaitForExit(5000);
            }
            catch (Exception)
            {
                // Службы нет — нечего и выгружать.
            }
        }
    }

    /// <summary>
    /// Заменяет файлы; при сбое — какой файл и кто его держит.
    /// </summary>
    /// <returns><c>null</c> — все файлы на месте.</returns>
    /// <remarks>Повторный вызов продолжает с незаменённых.</remarks>
    public async Task<ApplyFailure?> CopyAsync(IProgress<ApplyProgress>? progress, CancellationToken cancellationToken)
    {
        Log($"подмена: {_files.Count} файлов, заменено раньше — {_done.Count}");

        foreach (var file in _files)
        {
            if (_done.Contains(file))
                continue;

            cancellationToken.ThrowIfCancellationRequested();

            var from = Path.Combine(_source, file);
            var to = Path.Combine(_target, file);
            Exception? last = null;

            for (int attempt = 1; attempt <= Attempts; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);

                    // Копия прежнего — один раз: при повторе на месте лежит
                    // уже он же, а после замены — новый, и его копировать нельзя.
                    if (!_backedUp.Contains(file) && !_created.Contains(file))
                    {
                        if (File.Exists(to))
                        {
                            var saved = Path.Combine(_backup, file);
                            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                            File.Copy(to, saved, overwrite: true);
                            _backedUp.Add(file);
                        }
                        else
                        {
                            _created.Add(file);
                        }
                    }

                    File.Copy(from, to, overwrite: true);
                    last = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    last = ex;

                    if (attempt < Attempts)
                        await Task.Delay(Pause, cancellationToken);
                }
            }

            if (last is not null)
            {
                var lockers = FileLockers.Find(to);
                var who = lockers.Count == 0 ? "" : " — держит " + string.Join(", ", lockers.Select(FileLockers.Describe));

                // «ERROR » в начале — по нему FailedFiles находит сбой в журнале.
                Log($"ERROR {last.HResult & 0xFFFF} Copying File {to}: {last.Message}{who}");

                return new ApplyFailure(file, last.Message, lockers);
            }

            _done.Add(file);
            progress?.Report(new ApplyProgress(_done.Count, _files.Count, file));
        }

        Log("подмена закончена");
        return null;
    }

    /// <summary>Возвращает прежние файлы и стирает добавленные; сколько файлов не вернулось.</summary>
    public int RollBack()
    {
        int failed = 0;
        Log($"откат: вернуть {_backedUp.Count}, убрать {_created.Count}");

        foreach (var file in _backedUp)
        {
            if (!Retry(() => File.Copy(Path.Combine(_backup, file), Path.Combine(_target, file), overwrite: true)))
            {
                failed++;
                Log($"ERROR откат: не вернулся {file}");
            }
        }

        foreach (var file in _created)
        {
            var path = Path.Combine(_target, file);

            if (File.Exists(path) && !Retry(() => File.Delete(path)))
            {
                failed++;
                Log($"ERROR откат: не убран {file}");
            }
        }

        _done.Clear();
        _backedUp.Clear();
        _created.Clear();

        Log(failed == 0 ? "откат закончен" : $"откат закончен, не вышло с {failed} файлами");
        return failed;
    }

    private static bool Retry(Action action)
    {
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < Attempts)
                    Thread.Sleep(Pause);
            }
        }

        return false;
    }

    private void Log(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_log)!);
            File.AppendAllText(_log, $"{DateTime.Now:yyyy/MM/dd HH:mm:ss} {line}\r\n", new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Журнал — для разбора; без него подмена всё равно идёт.
        }
    }
}
