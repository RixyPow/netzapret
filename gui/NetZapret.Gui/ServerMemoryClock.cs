using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Proxy;

namespace NetZapret.Gui;

/// <summary>
/// Забывает старые проверки серверов по сроку из «Настроек туннеля» (<see cref="ServerMemory"/>).
/// </summary>
/// <remarks>
/// При запуске программы и раз в час: в трее она живёт неделями, и проверки
/// только при запуске срок проспали бы. Забывание по возрасту ничего не помнит
/// о прошлом прогоне — повторять его можно сколько угодно.
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
            int forgotten = ServerMemory.ForgetOld(DateTimeOffset.Now);

            if (forgotten > 0)
            {
                int days = AppSettings.Load(AppSettings.DefaultPath).ServerMemoryDays;
                Journal.Write("замер", $"забыто проверок серверов старше {days} дн.: {forgotten}");
            }
        }
        catch (Exception ex)
        {
            Journal.Write("замер", "старые проверки серверов не забылись: " + ex.GetType().Name);
        }
    });
}
