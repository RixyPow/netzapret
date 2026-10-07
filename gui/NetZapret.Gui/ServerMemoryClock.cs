using System.Windows.Threading;
using NetZapret.Proxy;

namespace NetZapret.Gui;

/// <summary>
/// Автоочистка памяти замеров серверов по сроку из «Настроек туннеля» (<see cref="ServerMemory"/>).
/// </summary>
/// <remarks>
/// Срок проверяется при запуске программы и раз в час: в трее она живёт
/// неделями, и проверки только при запуске срок проспали бы.
/// </remarks>
internal static class ServerMemoryClock
{
    private static DispatcherTimer? _timer;

    public static void Start()
    {
        if (_timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _timer.Tick += (_, _) => Check();
        _timer.Start();

        Check();
    }

    private static void Check() => _ = Task.Run(() =>
    {
        try
        {
            if (ServerMemory.ClearIfDue(DateTimeOffset.Now))
                Journal.Write("замер", "память замеров серверов очищена по сроку");
        }
        catch (Exception ex)
        {
            Journal.Write("замер", "очистка памяти замеров не удалась: " + ex.GetType().Name);
        }
    });
}
