using System.IO;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>
/// Строка в общий журнал.
/// </summary>
/// <remarks>
/// Тот же файл, что у супервизора, и тот же, что показывает раздел
/// «Журнал»: у окна консоли нет, а заводить второй журнал ради одной
/// строки значило бы разложить историю одного запуска по двум файлам.
/// Писать в него из двух процессов разом безопасно —
/// <see cref="SharedLogWriter"/> для того и заведён.
/// </remarks>
internal static class Journal
{
    /// <param name="source">Кто пишет: «конфиг», «подписка».</param>
    public static void Write(string source, string message)
    {
        try
        {
            using var log = SharedLogWriter.TryOpen(
                Path.Combine("runtime", "supervisor.log"));

            log?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {source}: {message}");
        }
        catch (Exception)
        {
            // Потеря строки журнала не должна ронять то, о чём она.
        }
    }
}
