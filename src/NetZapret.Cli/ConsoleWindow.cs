using System.Runtime.InteropServices;

namespace NetZapret.Cli;

/// <summary>
/// Отвечает на вопрос, исчезнет ли окно вместе с программой.
/// </summary>
/// <remarks>
/// <para>
/// Нужно ровно для одного: решить, придерживать ли вывод в конце. Запущенная
/// из терминала программа заканчивается — и вывод остаётся, читать его можно
/// сколько угодно. Запущенная двойным щелчком получает собственное окно,
/// которое закрывается вместе с ней, и всё написанное пропадает в тот же миг.
/// </para>
/// <para>
/// Различаются эти случаи по владельцу консоли. Если окно создано для нас,
/// в нём нет никого, кроме нас, — <c>GetConsoleProcessList</c> вернёт один
/// процесс. Запущенные из оболочки делят консоль с ней, и процессов будет
/// хотя бы два. Способ подсказан документацией Windows и живёт в чужих
/// программах не первый десяток лет.
/// </para>
/// </remarks>
internal static class ConsoleWindow
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint count);

    /// <summary>Закроется ли окно, унеся с собой вывод.</summary>
    public static bool ClosesWithUs()
    {
        // Перенаправленный вывод никуда не денется: он уже в файле или трубе,
        // и пауза там означала бы висящий насмерть сценарий.
        if (Console.IsOutputRedirected || Console.IsInputRedirected)
            return false;

        try
        {
            var buffer = new uint[4];
            uint count = GetConsoleProcessList(buffer, (uint)buffer.Length);

            // Ноль означает, что консоли нет вовсе, — придерживать нечего.
            return count == 1;
        }
        catch (Exception)
        {
            // Нет функции или нет консоли: не придерживаем. Ошибиться в эту
            // сторону безопаснее — забытая пауза лишь неудобна, а лишняя
            // подвесит программу там, где её ждут завершившейся.
            return false;
        }
    }
}
