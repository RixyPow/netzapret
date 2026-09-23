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
/// </remarks>
public static class WinDivertDriver
{
    /// <summary>Имена службы: нынешнее и исходное WinDivert.</summary>
    public static IReadOnlyList<string> ServiceNames { get; } = ["Monkey", "WinDivert"];

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

        var stopped = new List<string>();

        foreach (var name in ServiceNames)
        {
            var (code, output) = await ScAsync(["stop", name], cancellationToken);

            // 1060 — службы нет, 1062 — уже остановлена: для нас это успех.
            if (code == 0)
                stopped.Add(name);
            else if (code is not (1060 or 1062))
                return $"Драйвер {name} не выгружен: sc stop вернул {code}. {output.Trim()}";
        }

        return stopped.Count > 0
            ? $"Драйвер перехвата выгружен ({string.Join(", ", stopped)}): файлы движка свободны."
            : "Драйвер перехвата не был загружен.";
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
