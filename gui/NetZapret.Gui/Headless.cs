using System.IO;
using System.Text;
using NetZapret.Core;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>
/// Управление движками из командной строки — тем же кодом, что и кнопками.
/// </summary>
/// <remarks>
/// <para>
/// Заведено 21.09 по замечанию владельца: «хочу, чтобы ты оставил себе все
/// нужные инструменты, которые работают по тому же принципу, что и окно».
/// Замечание точное, и диагноз в нём верный.
/// </para>
/// <para>
/// Беда прежней консоли была не в том, что она существует, а в том, что
/// у неё своя копия логики: <c>BlockCheckCommand</c> держит девятьсот
/// строк собственных решений, <c>MenuCommand</c> — свою сборку конфига.
/// Две копии одного расходятся, и 16.09 за вечер нашлись четыре
/// расхождения, все в пользу консоли.
/// </para>
/// <para>
/// Здесь своей логики нет ни одной строки — и это не аккуратность,
/// а устройство. Каждый ключ сводится к одному вызову в тот же код,
/// который зовёт кнопка окна. Разойтись такому не с чем: расходятся
/// копии, а копии здесь нет.
/// </para>
/// <para>
/// Мерка проста: если ключу понадобилось решение — решение это принадлежит
/// библиотеке, где им сможет воспользоваться и окно. Появится здесь
/// <c>if</c> про то, как чинить имя, — значит ошиблись местом.
/// </para>
/// </remarks>
internal static class Headless
{
    public const string StartSwitch = "--start";

    /// <summary>
    /// Поднимает движки — ровно как кнопка «Запустить».
    /// </summary>
    /// <remarks>
    /// Через <see cref="EngineControl.StartAsync"/>, а не своим запуском
    /// процессов. Свой запуск отличался бы рабочим каталогом, правами
    /// или ключами — и отладка велась бы не над тем, что у человека.
    /// </remarks>
    public static async Task<int> StartAsync(CancellationToken cancellationToken)
    {
        var outcome = await EngineControl.StartAsync(cancellationToken);

        Say(outcome.Message);

        return outcome.Ok ? 0 : 1;
    }

    /// <summary>
    /// Печатает в консоль, из которой запустили.
    /// </summary>
    /// <remarks>
    /// Окно собрано как <c>WinExe</c> и своей консоли не имеет: без
    /// присоединения к родительской вывод уходит в никуда, и ключ выглядит
    /// молча сработавшим. Присоединяемся один раз и на всё время.
    /// </remarks>
    private static void Say(string line)
    {
        Attach();
        Console.Out.WriteLine(line);
        Console.Out.Flush();
    }

    private static bool _attached;

    private static void Attach()
    {
        if (_attached)
            return;

        _attached = true;

        try
        {
            AttachConsole(unchecked((uint)-1));

            var stdout = Console.OpenStandardOutput();

            Console.SetOut(new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true });
        }
        catch (Exception)
        {
            // Родительской консоли может не быть вовсе — запустили из
            // проводника. Печатать тогда некуда, и это не беда: коды
            // возврата работают всё равно.
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
}
