using System.Diagnostics;
using System.IO;
using NetZapret.Supervisor;

namespace NetZapret.Gui.Views;

/// <summary>
/// Отчёт для разбора — одно на «Диагностику» и простую «Главную».
/// </summary>
/// <remarks>
/// С 11.11 кнопка есть и внизу простой «Главной» («Собрать отчёт для разбора»):
/// за отчётом приходят, когда что-то не так, и идти за ним в «Диагностику» —
/// лишний шаг. Сборка и вычистка — в библиотеке (<see cref="SupportReport"/>),
/// ссылки подписок она находит сама; здесь только запуск в фоне и проводник.
/// </remarks>
internal static class ReportMaker
{
    /// <summary>
    /// Собирает отчёт и показывает его в проводнике.
    /// </summary>
    /// <returns>Итог словами: где отчёт и что внутри — или почему не собрался.</returns>
    /// <remarks>
    /// Проводник открывается с выделенным файлом — чтобы его сразу можно было
    /// перетащить в чат.
    /// </remarks>
    public static async Task<string> MakeAsync()
    {
        try
        {
            var result = await Task.Run(() => SupportReport.Create(MainWindow.Version(), machine: true));

            try
            {
                Process.Start("explorer.exe", $"/select,\"{result.Path}\"");
            }
            catch (Exception)
            {
                // Проводник не открылся — путь назван в итоге, файл на месте.
            }

            return $"Отчёт готов: {Path.GetFileName(result.Path)} — в папке reports. "
                + $"Внутри: {string.Join(", ", result.Files)}.";
        }
        catch (Exception ex)
        {
            return "Отчёт не собрался: " + ex.Message;
        }
    }
}
