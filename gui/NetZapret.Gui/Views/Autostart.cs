using System.Diagnostics;
using System.IO;
using NetZapret.Core;
using NetZapret.Core.Rules;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Автозапуск при входе в Windows — одно на «Ещё» и подробную «Главную».
/// </summary>
/// <remarks>
/// С 11.11 выключатель стоит в двух местах (в «Ещё» всегда, на «Главной» —
/// в подробном виде), и логика у них одна: что ответил планировщик, не
/// устарела ли задача, завести или убрать. Прежде она жила в разделе
/// «Главная» целиком.
/// </remarks>
internal static class Autostart
{
    /// <summary>Что планировщик ответил в прошлый раз — на время работы окна.</summary>
    public static (bool Installed, bool Stale)? Known { get; private set; }

    /// <summary>Пока ответа нет — показывается это.</summary>
    public const string Asking = "Задача планировщика: проверяю…";

    /// <summary>Задача осталась от версии до 0.5.0 и запускает консольную программу, которой больше нет.</summary>
    public const string StaleText = "Задача устарела — выключите и включите снова.";

    /// <summary>
    /// Спрашивает планировщик в фоне.
    /// </summary>
    /// <remarks>
    /// Замер 28.09: два запуска schtasks — «заведена ли» и «не устарела ли» —
    /// 73–91 мс на главном потоке при каждом открытии раздела.
    /// </remarks>
    public static async Task<(bool Installed, bool Stale)> QueryAsync()
    {
        var fresh = await Task.Run(() =>
        {
            bool installed = AutostartTask.IsInstalled(AutostartTask.DefaultTaskName);
            return (Installed: installed, Stale: installed && IsStale());
        });

        Known = fresh;
        return fresh;
    }

    /// <summary>
    /// Что поднимется при входе — режим, выбранный на «Главной»; своего выбора
    /// у автозапуска нет намеренно.
    /// </summary>
    /// <param name="atLogon">«При входе в Windows» в начале — где этого не сказал заголовок.</param>
    public static string Raises(AppSettings settings, bool atLogon)
    {
        var said = WorkModes.Of(settings.Engines) is { } raised
            ? $"поднимется режим «{WorkModes.Name(raised)}»."
            : "ничего не поднимется: режим не выбран.";

        return atLogon ? "При входе в Windows " + said : char.ToUpperInvariant(said[0]) + said[1..];
    }

    /// <summary>
    /// Заводит задачу, если её нет, иначе убирает.
    /// </summary>
    /// <returns>Почему не вышло; <c>null</c> — вышло.</returns>
    /// <remarks>
    /// Задача поднимает эту же программу в трей, а движки та заводит сама —
    /// теми же ключами, что и кнопка «Запустить». Ответ планировщика после
    /// этого устарел и спрашивается заново.
    /// </remarks>
    public static string? Toggle()
    {
        Known = null;

        var exe = Environment.ProcessPath;

        if (exe is null)
            return "Не удалось определить путь к программе — задача не заведена.";

        try
        {
            if (AutostartTask.IsInstalled(AutostartTask.DefaultTaskName))
            {
                var (removed, output) = AutostartTask.Remove(AutostartTask.DefaultTaskName);
                return removed ? null : "Не удалось убрать автозапуск: " + output;
            }

            var (ok, installOutput) = AutostartTask.Install(new AutostartOptions
            {
                TaskName = AutostartTask.DefaultTaskName,
                ExecutablePath = exe,
                Arguments = TrayIcon.Switch,
                WorkingDirectory = Path.GetFullPath("."),
                // SID, а не имя: см. AutostartTask.CurrentUserId.
                UserId = AutostartTask.CurrentUserId(),
            });

            return ok ? null : "Не удалось завести автозапуск: " + installOutput;
        }
        catch (Exception ex)
        {
            return "Планировщик отказал: " + ex.GetBaseException().Message;
        }
    }

    /// <summary>
    /// Запускает ли задача не ту программу.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Задача переживает обновление, а её команда — нет: она записана внутрь
    /// задачи целиком, путём и ключами. До 0.5.0 автозапуск поднимал
    /// netzapret.exe, которого в поставке уже нет, и «заведена» при мёртвой
    /// команде — худший из возможных ответов.
    /// </para>
    /// <para>
    /// Сверяется имя файла, а не путь целиком, и не ключи: путь может
    /// содержать кириллицу, а schtasks при перенаправлении отвечает
    /// однобайтовой OEM — имя же латинское и переживает любую из них.
    /// </para>
    /// </remarks>
    private static bool IsStale()
    {
        try
        {
            // Кодировка не задаётся намеренно: schtasks пишет однобайтовый текст,
            // а прочитанный парами байт он превращался в кашу, и всякая задача
            // объявлялась устаревшей.
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                ArgumentList = { "/query", "/tn", AutostartTask.DefaultTaskName, "/xml" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return false;

            var xml = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            if (xml.Length == 0)
                return false;

            return Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name
                && !xml.Contains(name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Не прочиталось — молчим: пугать задачей, о которой ничего не выяснили, хуже.
            return false;
        }
    }
}
