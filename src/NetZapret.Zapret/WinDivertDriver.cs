using System.Diagnostics;

namespace NetZapret.Zapret;

/// <summary>
/// Драйвер перехвата WinDivert: выгрузить, когда он больше никому не нужен.
/// </summary>
/// <remarks>
/// <para>
/// WinDivert ставит себя службой ядра при первом открытии и после выхода
/// winws2 остаётся загруженным — до перезагрузки или явной остановки
/// службы. Пока он загружен, Windows держит его файл, и папку программы
/// не удалить: жалоба пользователя 23.09 — «Monkey64.sys не удаляется даже
/// после остановки программы». Замер на машине владельца в тот же вечер:
/// движки опущены, а служба <c>Monkey</c> в состоянии RUNNING с путём
/// в build\engines\zapret\exe\Monkey64.sys.
/// </para>
/// <para>
/// Имя службы — <c>Monkey</c>, а не <c>WinDivert</c>: сборка движка
/// из Zapret переименовывает драйвер. Сценарий удаления до 23.09
/// останавливал <c>Monkey64</c> — службы с таким именем нет, и драйвер
/// переживал удаление.
/// </para>
/// <para>
/// Драйвер общий с Zapret GUI: у него тот же Monkey64.sys и то же имя
/// службы. Поэтому выгружается только при отсутствии живых winws и winws2 —
/// любых, не только наших. Остановить занятый драйвер Windows и так
/// не даст, но и просить её об этом незачем.
/// </para>
/// <para>
/// И только свой: служба останавливается, лишь если её драйвер лежит в нашей
/// папке <c>engines</c> (с 22.10). До того останавливались обе службы по имени,
/// а под исходным именем <c>WinDivert</c> свой драйвер держат GoodbyeDPI и
/// другие обходы — их процессов среди <see cref="Users"/> нет. Обсуждение №33:
/// рядом работал GoodbyeDPI, после остановки перед обновлением до 1.0.0 наш
/// winws2 шесть раз подряд падал с кодом 433 («A device which does not exist
/// was specified»). Что виновата именно эта остановка, не доказано, но чужой
/// драйвер трогать незачем в любом случае: занятый, он не выгружается, а лишь
/// откладывает выгрузку, и новые открытия его устройства Windows отвергает.
/// </para>
/// </remarks>
public static class WinDivertDriver
{
    /// <summary>Имена службы: нынешнее и исходное WinDivert.</summary>
    public static IReadOnlyList<string> ServiceNames { get; } = ["Monkey", "WinDivert"];

    /// <summary>
    /// Код выхода winws2, когда WinDivert не открыл своё устройство:
    /// ERROR_NO_SUCH_DEVICE, «A device which does not exist was specified».
    /// </summary>
    public const int NoSuchDevice = 433;

    /// <summary>Что значит код выхода winws2 и что делать; <c>null</c> — объяснения нет.</summary>
    /// <remarks>
    /// <para>
    /// Только 433 (обсуждение №33, 22.10): сам код человеку ничего не говорит,
    /// а «Диагностика» показывала про упавший winws2 одно «остановлен».
    /// </para>
    /// <para>
    /// Найден рядом другой обход — он и назван, с процессом или службой
    /// (владелец 24.10: «WinDivert занят другим обходом (GoodbyeDPI)»). У знакомого
    /// владельца 22.10 так и было: работал goodbyedpi.exe, а его служба с
    /// автозапуском подняла бы его снова после перезагрузки.
    /// </para>
    /// </remarks>
    /// <param name="nearby">Чужие обходы рядом (<see cref="Core.Diagnostics.OtherBypassScan"/>).</param>
    public static string? Explain(int exitCode, IReadOnlyList<Core.Diagnostics.OtherBypass>? nearby = null)
    {
        if (exitCode != NoSuchDevice)
            return null;

        var running = nearby?.Where(b => b.Running).ToList() ?? [];
        var services = nearby?.Where(b => !b.Running).ToList() ?? [];

        if (running.Count == 0)
        {
            return "драйвер перехвата WinDivert не открылся. Так бывает, когда рядом работает "
                + "другой обход на WinDivert (GoodbyeDPI, Zapret) или драйвер завис после остановки: "
                + "закройте другой обход, перезагрузите компьютер и запустите снова";
        }

        var names = string.Join(", ", running.Select(b => b.Name).Distinct());
        var where = string.Join(", ", running.Select(b => b.Where));

        var text = $"драйвер перехвата WinDivert занят: рядом работает {names} ({where}). "
            + "Закройте его и запустите снова; не поможет — перезагрузите компьютер";

        if (services.Count > 0)
        {
            text += $". После перезагрузки его поднимет {string.Join(", ", services.Select(b => b.Where))} — "
                + "её стоит отключить";
        }

        return text;
    }

    /// <summary>Процессы, которые держат драйвер: наш движок и движок Zapret GUI.</summary>
    public static IReadOnlyList<string> Users { get; } = ["winws2", "winws"];

    /// <summary>
    /// Выгружает драйвер, если им никто не пользуется.
    /// </summary>
    /// <returns>Что сделано — одной строкой для журнала.</returns>
    /// <remarks>
    /// Нужны права администратора. Окно их имеет всегда, а без них
    /// <c>sc stop</c> вернёт отказ, и мы честно его назовём.
    /// </remarks>
    public static async Task<string> TryUnloadAsync(CancellationToken cancellationToken)
    {
        // Движок, которого только что погасили, может ещё завершаться:
        // убит супервизор вместе с деревом, а ждали только его самого.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (Running().Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(200, cancellationToken);

        var alive = Running();

        if (alive.Count > 0)
            return $"Драйвер перехвата оставлен: им пользуется {string.Join(", ", alive)}.";

        var engines = Path.Combine(AppContext.BaseDirectory, ZapretPaths.BundleDirectory);
        var stopped = new List<string>();
        var foreign = new List<string>();

        foreach (var name in ServiceNames)
        {
            // Службы нет — останавливать нечего; драйвер не из нашей папки — не наш.
            if (ImagePath(name) is not { } image)
                continue;

            if (!IsOurs(image, engines))
            {
                foreign.Add($"{name} ({Path.GetFileName(DriverFile(image))})");
                continue;
            }

            var (code, output) = await ScAsync(["stop", name], cancellationToken);

            // 1060 — службы нет, 1062 — уже остановлена: для нас это успех.
            if (code == 0)
                stopped.Add(name);
            else if (code is not (1060 or 1062))
                return $"Драйвер {name} не выгружен: sc stop вернул {code}. {output.Trim()}";
        }

        var others = foreign.Count > 0 ? $" Чужие драйверы не тронуты: {string.Join(", ", foreign)}." : string.Empty;

        return (stopped.Count > 0
            ? $"Драйвер перехвата выгружен ({string.Join(", ", stopped)}): файлы движка свободны."
            : "Драйвер перехвата не был загружен.") + others;
    }

    /// <summary>Путь драйвера службы из реестра (только чтение); <c>null</c> — службы нет.</summary>
    private static string? ImagePath(string service)
    {
        // Службы драйвера бывают только в Windows: вне её — «службы нет».
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{service}");
            return key?.GetValue("ImagePath") as string;
        }
        catch (Exception)
        {
            // Не прочли — не знаем, чей он, и не трогаем.
            return null;
        }
    }

    /// <summary>
    /// Файл драйвера по записи службы: «\??\C:\…», «\SystemRoot\…» и «System32\drivers\…».
    /// </summary>
    public static string? DriverFile(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        var path = imagePath.Trim().Trim('"');
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];

        if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(windows, path[@"\SystemRoot\".Length..]);

        return Path.IsPathRooted(path) ? path : Path.Combine(windows, path);
    }

    /// <summary>Лежит ли драйвер службы в нашей папке движков.</summary>
    public static bool IsOurs(string? imagePath, string engines)
    {
        if (DriverFile(imagePath) is not { } file)
            return false;

        try
        {
            var root = Path.GetFullPath(engines).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static List<string> Running()
    {
        var found = new List<string>();

        foreach (var name in Users)
        {
            try
            {
                var processes = Process.GetProcessesByName(name);

                if (processes.Length > 0)
                    found.Add(name + ".exe");

                foreach (var process in processes)
                    process.Dispose();
            }
            catch (Exception)
            {
                // Перечисление могло отказать — тогда не выгружаем вслепую.
                found.Add(name + ".exe (не проверено)");
            }
        }

        return found;
    }

    private static async Task<(int Code, string Output)> ScAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);

            return (process.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.GetBaseException().Message);
        }
    }
}
