using System.Diagnostics;
using System.Text;

namespace NetZapret.Zapret;

/// <summary>
/// Пробный winws2: один рецепт (или пресет целиком) — только на портах пробы,
/// рядом с работающим десинком.
/// </summary>
/// <remarks>
/// <para>
/// Окно рецептов поднимало такой же winws2, но требовало остановить движки:
/// второй экземпляр обрабатывал бы те же пакеты поверх первого. С 27.10
/// основной winws2 порты пробы не перехватывает (<see cref="TunnelCapture.KeepProbesOut"/>),
/// а этот перехватывает только их (<see cref="WinwsCommandLine.ProbeLine"/>), —
/// подбор маршрута проверяет рецепт, не трогая соединений человека
/// (владелец: «отдельный winws2»).
/// </para>
/// <para>
/// Нужны права администратора: winws2 открывает WinDivert. Окно их имеет.
/// </para>
/// </remarks>
public sealed class RecipeTrial : IAsyncDisposable
{
    /// <summary>Строка winws2 о начатом перехвате — раньше неё пакеты не снимаются.</summary>
    private const string CaptureStarted = "capture is started";

    /// <summary>Сколько ждать начала перехвата.</summary>
    private static readonly TimeSpan Ready = TimeSpan.FromSeconds(6);

    /// <summary>
    /// Доводка после начала перехвата: между разбором настроек и первым
    /// перехваченным пакетом драйвер успевает не всегда (окно рецептов, 21.09).
    /// </summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(250);

    /// <summary>Сколько ждать ухода снятого процесса: следующий не поднимется, пока WinDivert занят.</summary>
    private static readonly TimeSpan Leaves = TimeSpan.FromMilliseconds(1500);

    private readonly Process _process;

    private RecipeTrial(Process process) => _process = process;

    /// <summary>Файл имён пробного профиля.</summary>
    public static string ListPath => Path.GetFullPath(Path.Combine("runtime", "probe", "recipe.txt"));

    /// <summary>
    /// Поднимает пробный winws2 с рецептом <paramref name="recipe"/>, а без него — с пресетом целиком.
    /// </summary>
    /// <returns>Поднятый пробный winws2 либо причина, по которой он не поднялся.</returns>
    public static async Task<(RecipeTrial? Trial, string? Failure)> StartAsync(
        ZapretPaths paths,
        ZapretPreset preset,
        OwnDesyncProfile? recipe,
        CancellationToken cancellationToken)
    {
        var line = WinwsCommandLine.ProbeLine(preset, recipe);

        if (line is null)
            return (null, "у пресета свой фильтр перехвата файлом — сузить его до портов пробы нечем");

        if (!File.Exists(paths.ExecutablePath))
            return (null, "winws2 не найден рядом с программой");

        WinwsCommandLine.PrepareWritable(line, paths.Root);

        var start = new ProcessStartInfo(paths.ExecutablePath)
        {
            WorkingDirectory = paths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        foreach (var argument in line)
            start.ArgumentList.Add(argument);

        var said = new List<string>();
        var capturing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void Heard(string? text)
        {
            if (text is null)
                return;

            lock (said)
            {
                if (said.Count < 40)
                    said.Add(text);
            }

            if (text.Contains(CaptureStarted, StringComparison.OrdinalIgnoreCase))
                capturing.TrySetResult();
        }

        Process process;

        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("процесс не создан");
        }
        catch (Exception ex)
        {
            return (null, "winws2 не запустился: " + ex.GetBaseException().Message);
        }

        process.OutputDataReceived += (_, e) => Heard(e.Data);
        process.ErrorDataReceived += (_, e) => Heard(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var trial = new RecipeTrial(process);

        try
        {
            var exited = process.WaitForExitAsync(cancellationToken);
            var first = await Task.WhenAny(capturing.Task, exited, Task.Delay(Ready, cancellationToken));

            if (first != capturing.Task)
            {
                string tail;

                lock (said)
                    tail = string.Join(" | ", said.Where(s => s.Length > 0).TakeLast(3));

                await trial.DisposeAsync();

                return (null, process.HasExited
                    ? $"winws2 завершился с кодом {process.ExitCode}" + (tail.Length > 0 ? $": {tail}" : string.Empty)
                    : "winws2 не начал перехват за 6 с");
            }

            await Task.Delay(Settle, cancellationToken);
            return (trial, null);
        }
        catch
        {
            await trial.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);

                using var leave = new CancellationTokenSource(Leaves);
                await _process.WaitForExitAsync(leave.Token);
            }
        }
        catch (Exception)
        {
            // Ушёл сам или не дождались — следующий запуск это покажет.
        }

        _process.Dispose();
    }
}
