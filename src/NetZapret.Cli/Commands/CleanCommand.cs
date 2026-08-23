using NetZapret.Supervisor;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Убирает рабочие файлы: конфиги движка, логи, временные артефакты.
/// </summary>
/// <remarks>
/// Сгенерированные конфиги содержат UUID и пароли открытым текстом —
/// иначе движок не сможет подключиться. Держать на диске те, что остались
/// от прошлых опытов, незачем.
/// </remarks>
internal static class CleanCommand
{
    public static int Run(CommandLine cmd)
    {
        var runtime = cmd.Value("runtime", "runtime");

        if (!Directory.Exists(runtime))
        {
            Console.WriteLine($"Каталог {runtime} пуст или не существует.");
            return 0;
        }

        var state = SupervisorState.Load(SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
        {
            Console.Error.WriteLine(
                $"Супервизор работает (PID {state.SupervisorProcessId}) и использует эти файлы. " +
                "Сначала остановите: netzapret stop");

            return 2;
        }

        // Действующий конфиг сохраняем: без него не запуститься, а собрать
        // заново можно только с подпиской под рукой.
        var keep = Path.GetFullPath(cmd.Value("keep", Path.Combine("runtime", "singbox.json")));
        bool all = cmd.Has("all");

        var removed = 0;
        long freed = 0;

        foreach (var file in Directory.EnumerateFiles(runtime, "*", SearchOption.AllDirectories))
        {
            var full = Path.GetFullPath(file);

            if (!all && string.Equals(full, keep, StringComparison.OrdinalIgnoreCase))
                continue;

            var extension = Path.GetExtension(file);

            bool removable = extension is ".json" or ".log"
                || extension.StartsWith(".log.", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Contains(".log.", StringComparison.OrdinalIgnoreCase);

            if (!removable)
                continue;

            try
            {
                long size = new FileInfo(file).Length;
                File.Delete(file);

                removed++;
                freed += size;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  не удалось удалить {Path.GetFileName(file)}: {ex.GetBaseException().Message}");
            }
        }

        Console.WriteLine($"Удалено файлов: {removed}, освобождено {freed / 1024.0:0.#} КБ.");

        if (!all && File.Exists(keep))
            Console.WriteLine($"Сохранён действующий конфиг: {keep}");

        return 0;
    }
}
