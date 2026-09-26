using System.Diagnostics;
using System.Windows.Threading;

namespace NetZapret.Gui;

/// <summary>
/// Сторож подвисаний: пишет в журнал, когда поток окна занят дольше порога.
/// </summary>
/// <remarks>
/// <para>
/// Владелец 24.09: «интерфейс даже у меня на RTX 4060 подвисать может».
/// Профилировщиком к окну не подключиться — оно работает с правами
/// администратора, — а замер в тестах ловит только то, что догадались
/// открыть. Сторож меряет там, где подвисает на самом деле: у человека,
/// в его разделе, на его данных. Журнал попадает в отчёт для разбора.
/// </para>
/// <para>
/// Устроен просто: фоновый поток раз в четверть секунды ставит в очередь
/// окна пустую задачу и смотрит, когда она выполнится. Опоздание и есть
/// время, на которое окно было занято. Сам сторож ничего в потоке окна
/// не делает, кроме этой пустой задачи.
/// </para>
/// </remarks>
internal static class UiStallWatch
{
    /// <summary>С какого опоздания писать: меньше человек не замечает.</summary>
    private static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(300);

    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Какой раздел открыт — чтобы подвисание было к чему привязать.
    /// </summary>
    public static string Section { get; set; } = "Главная";

    private static Thread? _thread;

    public static void Start(Dispatcher dispatcher)
    {
        if (_thread is not null)
            return;

        _thread = new Thread(() => Watch(dispatcher))
        {
            IsBackground = true,
            Name = "Сторож подвисаний",
        };

        _thread.Start();
    }

    private static void Watch(Dispatcher dispatcher)
    {
        using var answered = new ManualResetEventSlim();

        using var self = Process.GetCurrentProcess();

        while (!dispatcher.HasShutdownStarted)
        {
            Thread.Sleep(Period);

            answered.Reset();
            var clock = Stopwatch.StartNew();

            self.Refresh();
            var cpuBefore = self.TotalProcessorTime;
            var sectionBefore = Section;
            var ownLate = TimeSpan.Zero;

            try
            {
                // Приоритет Input: ниже него идут фоновые задачи, которые
                // и так могут ждать; выше — отрисовка. Опоздание на этом уровне —
                // ровно то, что человек видит как «не отзывается на щелчок».
                dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(answered.Set));
            }
            catch (Exception)
            {
                return;
            }

            // Ждём сколько угодно: подвисание на десять секунд тоже надо
            // записать, а не бросить на пороге.
            //
            // Каждое ожидание — секунда; если оно длилось дольше, опоздал уже
            // сам сторож. Его поток окном не занят, и опоздать он может, только
            // если стояла вся машина.
            var tick = Stopwatch.StartNew();

            while (!answered.Wait(TimeSpan.FromSeconds(1)))
            {
                var over = tick.Elapsed - TimeSpan.FromSeconds(1);

                if (over > ownLate)
                    ownLate = over;

                tick.Restart();

                if (dispatcher.HasShutdownStarted)
                    return;
            }

            var late = clock.Elapsed;

            // Раздел — на момент, когда окно освободилось: подвисает чаще всего
            // само открытие раздела, и записать надо тот, что открывали.
            var section = Section;

            if (late < Threshold)
                continue;

            // Сколько процессора окно съело за время подвисания. 26.09 «Десинк»
            // простоял 8765 мс, и владелец видел замерший курсор — а по одной
            // длительности не понять, считало окно, ждало или стояла вся
            // машина. Замер того же открытия вне окна дал 50 мс.
            self.Refresh();
            var cpu = self.TotalProcessorTime - cpuBefore;

            var where = sectionBefore == section ? $"раздел «{section}»" : $"раздел «{sectionBefore}» → «{section}»";

            var why = late.TotalMilliseconds >= 1000
                ? ownLate.TotalMilliseconds >= 300
                    ? $"; стояла вся машина — сторож сам опоздал на {ownLate.TotalMilliseconds:0} мс"
                    : cpu.TotalMilliseconds >= late.TotalMilliseconds * 0.5
                        ? "; окно считало"
                        : "; окно ждало — не процессор"
                : string.Empty;

            Journal.Write("окно", $"поток окна был занят {late.TotalMilliseconds:0} мс, {where}; "
                + $"процессора окна за это время {cpu.TotalMilliseconds:0} мс{why}");
        }
    }
}
