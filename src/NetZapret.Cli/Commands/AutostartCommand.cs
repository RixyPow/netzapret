using System.Security.Principal;
using System.Text;
using NetZapret.Supervisor;

namespace NetZapret.Cli.Commands;

/// <summary>
/// Управляет автозапуском через планировщик заданий.
/// </summary>
internal static class AutostartCommand
{
    public static int Run(CommandLine cmd)
    {
        var action = cmd.Positional.Count > 0 ? cmd.Positional[0].ToLowerInvariant() : "status";
        var taskName = cmd.Value("task-name", AutostartTask.DefaultTaskName);

        return action switch
        {
            "install" => Install(cmd, taskName),
            "remove" or "uninstall" => Remove(taskName),
            "status" => Status(taskName),
            _ => Unknown(action),
        };
    }

    private static int Install(CommandLine cmd, string taskName)
    {
        var executable = Environment.ProcessPath;

        if (executable is null)
        {
            Console.Error.WriteLine("Не удалось определить путь к собственному исполняемому файлу.");
            return 1;
        }

        var workingDirectory = cmd.Value("work-dir", Directory.GetCurrentDirectory());
        var arguments = BuildArguments(cmd);

        var options = new AutostartOptions
        {
            TaskName = taskName,
            ExecutablePath = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UserId = WindowsIdentity.GetCurrent().Name,
        };

        // Холостой прогон проверяется до прав: он ничего не меняет,
        // и требовать администратора ради показа параметров незачем.
        if (cmd.Has("dry-run"))
        {
            Console.WriteLine("Холостой прогон: задача не создаётся.");
            Console.WriteLine();
            Console.WriteLine($"Имя задачи:      {taskName}");
            Console.WriteLine($"Программа:       {executable}");
            Console.WriteLine($"Аргументы:       {arguments}");
            Console.WriteLine($"Рабочий каталог: {workingDirectory}");
            Console.WriteLine($"Пользователь:    {options.UserId}");
            return 0;
        }

        if (!IsElevated())
        {
            Console.Error.WriteLine(
                "Установка задачи требует прав администратора: она регистрируется " +
                "с наивысшими привилегиями, иначе TUN и WinDivert не поднимутся.");
            return 3;
        }

        var (ok, output) = AutostartTask.Install(options);

        if (!ok)
        {
            Console.Error.WriteLine($"Не удалось создать задачу: {output}");
            return 1;
        }

        Console.WriteLine($"Задача '{taskName}' создана.");
        Console.WriteLine($"Программа:       {executable}");
        Console.WriteLine($"Аргументы:       {arguments}");
        Console.WriteLine($"Рабочий каталог: {workingDirectory}");
        Console.WriteLine();
        Console.WriteLine("Запустится при следующем входе в систему. Проверить сейчас:");
        Console.WriteLine($"  schtasks /run /tn \"{taskName}\"");
        Console.WriteLine("Убрать:");
        Console.WriteLine("  netzapret autostart remove");

        return 0;
    }

    /// <summary>
    /// Собирает командную строку для задачи.
    /// </summary>
    /// <remarks>
    /// Пути делаются абсолютными: задача стартует до того, как пользователь
    /// куда-либо перейдёт, и относительный путь к конфигу превратился бы
    /// в путь от system32.
    /// </remarks>
    private static string BuildArguments(CommandLine cmd)
    {
        var builder = new StringBuilder("start");

        var proxyConfig = cmd.Value("proxy-config", Path.Combine("runtime", "singbox.json"));
        builder.Append(" --proxy-config \"").Append(Path.GetFullPath(proxyConfig)).Append('"');

        if (cmd.Value("preset") is { } preset)
            builder.Append(" --preset \"").Append(preset).Append('"');

        if (cmd.Has("verify-traffic"))
            builder.Append(" --verify-traffic");

        if (cmd.Value("zapret-root") is { } root)
            builder.Append(" --zapret-root \"").Append(root).Append('"');

        // Состояние тоже абсолютным: иначе status из другого каталога
        // не найдёт запущенный экземпляр.
        builder.Append(" --state \"")
            .Append(Path.GetFullPath(cmd.Value("state", SupervisorState.DefaultPath)))
            .Append('"');

        return builder.ToString();
    }

    private static int Remove(string taskName)
    {
        if (!IsElevated())
        {
            Console.Error.WriteLine("Удаление задачи требует прав администратора.");
            return 3;
        }

        if (!AutostartTask.IsInstalled(taskName))
        {
            Console.WriteLine($"Задача '{taskName}' не установлена.");
            return 0;
        }

        var (ok, output) = AutostartTask.Remove(taskName);

        if (!ok)
        {
            Console.Error.WriteLine($"Не удалось удалить задачу: {output}");
            return 1;
        }

        Console.WriteLine($"Задача '{taskName}' удалена. Автозапуск выключен.");
        return 0;
    }

    private static int Status(string taskName)
    {
        bool installed = AutostartTask.IsInstalled(taskName);

        Console.WriteLine(installed
            ? $"Автозапуск включён: задача '{taskName}' зарегистрирована."
            : $"Автозапуск выключен: задачи '{taskName}' нет.");

        if (!installed)
        {
            Console.WriteLine();
            Console.WriteLine("Включить (из консоли администратора):");
            Console.WriteLine("  netzapret autostart install --preset \"Universal V6\"");
        }

        return installed ? 0 : 1;
    }

    private static int Unknown(string action)
    {
        Console.Error.WriteLine($"Неизвестное действие '{action}'. Допустимы: install, remove, status.");
        return 2;
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
