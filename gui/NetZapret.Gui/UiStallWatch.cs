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

        while (!dispatcher.HasShutdownStarted)
        {
            Thread.Sleep(Period);

            answered.Reset();
            var clock = Stopwatch.StartNew();

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
            while (!answered.Wait(TimeSpan.FromSeconds(1)))
            {
                if (dispatcher.HasShutdownStarted)
                    return;
            }

            var late = clock.Elapsed;

            // Раздел — на момент, когда окно освободилось: подвисает чаще всего
            // само открытие раздела, и записать надо тот, что открывали.
            var section = Section;

            if (late >= Threshold)
                Journal.Write("окно", $"поток окна был занят {late.TotalMilliseconds:0} мс, раздел «{section}»");
        }
    }
}
