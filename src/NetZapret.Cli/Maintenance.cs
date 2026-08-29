using System.Diagnostics;

namespace NetZapret.Cli;

/// <summary>Чем закончилось действие обслуживания.</summary>
public sealed record ActionResult
{
    public required bool Ok { get; init; }

    public required string Message { get; init; }

    /// <summary>Нужна ли перезагрузка, чтобы сделанное вступило в силу.</summary>
    public bool NeedsReboot { get; init; }

    public static ActionResult Good(string message, bool reboot = false) =>
        new() { Ok = true, Message = message, NeedsReboot = reboot };

    public static ActionResult Bad(string message) =>
        new() { Ok = false, Message = message };
}

/// <summary>
/// Разовые действия, которых иначе пришлось бы искать по инструкциям.
/// </summary>
/// <remarks>
/// Каждое снимает отдельное «а как…». Ни одно не хитрое — вся ценность в том,
/// что они собраны в одном месте, названы по-человечески и предупреждают
/// о последствиях до того, как их вызвали, а не после.
/// </remarks>
internal static class Maintenance
{
    /// <summary>
    /// Возвращает настройки к заводским.
    /// </summary>
    /// <param name="keepSubscription">Оставить ссылку подписки.</param>
    /// <remarks>
    /// Подписку по умолчанию оставляем: это единственное здесь, что нельзя
    /// восстановить самой программой — её выдаёт поставщик, и потерять её
    /// значит остаться без VPN до тех пор, пока человек не найдёт письмо.
    /// </remarks>
    public static ActionResult ResetSettings(bool keepSubscription)
    {
        // При работающих движках в runtime\ лежит состояние супервизора и его
        // журналы. Стереть их под ним значит потерять след запущенных процессов:
        // остановить их станет нечем, а следующий запуск упрётся в занятый
        // драйвер и осиротевший TUN.
        var state = Supervisor.SupervisorState.Load(Supervisor.SupervisorState.DefaultPath);

        if (state is not null && state.IsSupervisorAlive())
            return ActionResult.Bad("Сначала остановите движки — иначе потеряется след запущенных процессов.");

        try
        {
            string? subscription = null;

            if (keepSubscription)
            {
                var settings = Core.AppSettings.Load(Core.AppSettings.DefaultPath);
                subscription = settings.SubscriptionUrl;
            }

            foreach (var file in new[] { Core.AppSettings.DefaultPath, Core.Rules.UserRulesFile.DefaultPath })
            {
                if (File.Exists(file))
                    File.Delete(file);
            }

            // runtime\ целиком: там собранные конфиги и журналы, всё
            // воссоздаётся при следующем запуске.
            if (Directory.Exists("runtime"))
                Directory.Delete("runtime", recursive: true);

            if (subscription is not null)
            {
                var fresh = new Core.AppSettings { SubscriptionUrl = subscription };
                fresh.Save(Core.AppSettings.DefaultPath);

                return ActionResult.Good("Настройки сброшены, подписка сохранена.");
            }

            return ActionResult.Good("Настройки сброшены полностью.");
        }
        catch (Exception ex)
        {
            return ActionResult.Bad($"Не вышло: {ex.GetBaseException().Message}");
        }
    }

    /// <summary>
    /// Сброс сетевого стека Windows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Стандартное лечение для сети, оставшейся в странном состоянии после
    /// опытов с адаптерами и DNS. Делает две вещи: <c>winsock reset</c>
    /// возвращает каталог сетевых поставщиков, <c>int ip reset</c> — настройки
    /// стека TCP/IP.
    /// </para>
    /// <para>
    /// Требует администратора и перезагрузки, и сказать об этом надо до, а не
    /// после: сброс без перезагрузки оставляет сеть в состоянии переходном,
    /// то есть хуже исходного.
    /// </para>
    /// </remarks>
    public static ActionResult ResetNetwork()
    {
        if (!IsElevated())
            return ActionResult.Bad("Нужны права администратора.");

        foreach (var arguments in new[] { "int ip reset", "winsock reset" })
        {
            var code = Run("netsh", arguments);

            if (code != 0)
                return ActionResult.Bad($"netsh {arguments} завершился с кодом {code}.");
        }

        return ActionResult.Good("Сетевой стек сброшен.", reboot: true);
    }

    /// <summary>Открывает папку программы в проводнике.</summary>
    public static ActionResult OpenFolder()
    {
        try
        {
            var path = Path.GetFullPath(".");
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

            return ActionResult.Good(path);
        }
        catch (Exception ex)
        {
            return ActionResult.Bad(ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Открывает документацию — местную, если она рядом, иначе на GitHub.
    /// </summary>
    /// <remarks>
    /// Местная впереди: в сборке лежит README, написанный для того, кто
    /// распаковал архив, и он вернее отвечает на вопросы такого человека,
    /// чем репозиторный, написанный для того, у кого есть исходники.
    /// </remarks>
    public static ActionResult OpenDocs()
    {
        var local = new[] { "README.md", Path.Combine("docs", "README.dist.md") }
            .FirstOrDefault(File.Exists);

        var target = local is not null
            ? Path.GetFullPath(local)
            : $"https://github.com/{Core.Updates.UpdateCheck.Repository}";

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            return ActionResult.Good(target);
        }
        catch (Exception ex)
        {
            return ActionResult.Bad(ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// Заводит папку программы в исключения Microsoft Defender.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Не отключение антивируса — именно исключение каталога. Defender удаляет
    /// <c>winws2.exe</c>, опознавая в нём WinDivert; наблюдалось вживую: файл
    /// исчез посреди сборки, и архив не собрался. Снимать защиту со всей
    /// системы ради этого не нужно и нельзя — программа, глушащая антивирус,
    /// сама выглядит как вредонос.
    /// </para>
    /// <para>
    /// На системах с защитой от изменений (Tamper Protection) команда молча
    /// не сработает. Поэтому результат проверяется чтением списка исключений,
    /// а не доверием к коду возврата.
    /// </para>
    /// </remarks>
    public static ActionResult ExcludeFromDefender()
    {
        if (!IsElevated())
            return ActionResult.Bad("Нужны права администратора.");

        var path = Path.GetFullPath(".");

        var code = Run(
            "powershell",
            $"-NoProfile -Command \"Add-MpPreference -ExclusionPath '{path.Replace("'", "''")}'\"");

        if (code != 0)
            return ActionResult.Bad($"Не вышло, код {code}. Возможно, включена защита от изменений.");

        // Проверяем делом: Tamper Protection отклоняет правку, не сообщая
        // об этом кодом возврата.
        if (!IsExcluded(path))
        {
            return ActionResult.Bad(
                "Команда прошла, но исключение не появилось — включена защита от изменений. " +
                "Добавьте вручную: Безопасность Windows → Защита от вирусов → Исключения.");
        }

        return ActionResult.Good($"В исключениях: {path}");
    }

    /// <summary>Есть ли уже такое исключение.</summary>
    public static bool IsExcluded(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -Command \"(Get-MpPreference).ExclusionPath -join [char]10\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            return output.Split('\n')
                .Any(line => string.Equals(line.Trim().TrimEnd('\\'), path.TrimEnd('\\'),
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Перезапускает Discord.
    /// </summary>
    /// <remarks>
    /// Discord запоминает голосовые серверы на сеанс: сменили маршрут голоса —
    /// а он продолжает ходить по-старому, пока не перезапустится. Перезапуск
    /// посреди звонка хуже задержки с применением, поэтому сам он не делается
    /// никогда — только по прямой просьбе.
    /// </remarks>
    public static ActionResult RestartDiscord()
    {
        try
        {
            var running = Process.GetProcessesByName("Discord");

            if (running.Length == 0)
                return ActionResult.Bad("Discord не запущен.");

            var path = running.Select(p =>
            {
                try { return p.MainModule?.FileName; }
                catch (Exception) { return null; }
            }).FirstOrDefault(p => p is not null);

            foreach (var process in running)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(10_000);
                }
                catch (Exception)
                {
                }
            }

            if (path is null)
                return ActionResult.Good("Discord закрыт; запустите его сами — путь определить не удалось.");

            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            return ActionResult.Good("Discord перезапущен.");
        }
        catch (Exception ex)
        {
            return ActionResult.Bad(ex.GetBaseException().Message);
        }
    }

    private static int Run(string file, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
                return -1;

            process.WaitForExit(60_000);
            return process.ExitCode;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
