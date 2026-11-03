using NetZapret.Core;
using NetZapret.Proxy;

namespace NetZapret.Gui;

/// <summary>
/// Раз в сутки меняет мёртвые пины к посредникам на живые (<see cref="PinRefresh"/>).
/// </summary>
/// <remarks>
/// <para>
/// Владелец 03.11: «их банят только так… стоит внедрить автозамену», «каждый
/// час — перебор, лучше раз в сутки». Выключатель — <see cref="AppSettings.AutoRepin"/>
/// на вкладке DNS, читается на каждом круге.
/// </para>
/// <para>
/// Только при поднятых движках и не раньше, чем они проработали два круга
/// подряд: часть посредников напрямую отвечает лишь с десинком, и без него
/// живой адрес показался бы мёртвым. Срок — по отметке в runtime: перезапуск
/// движков или окна проверку не повторяет.
/// </para>
/// </remarks>
internal static class PinRefreshWatch
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

    private static bool _started;

    public static void Start()
    {
        if (_started)
            return;

        _started = true;
        _ = Task.Run(RunAsync);
    }

    private static async Task RunAsync()
    {
        bool wasRunning = false;

        while (true)
        {
            await Task.Delay(Tick).ConfigureAwait(false);

            bool running = EngineControl.IsRunning;
            bool ready = running && wasRunning;
            wasRunning = running;

            if (!ready || !AppSettings.Load(AppSettings.DefaultPath).AutoRepin || !PinRefresh.IsDue(DateTimeOffset.Now))
                continue;

            try
            {
                var result = await PinRefresh.RunAsync(CancellationToken.None).ConfigureAwait(false);

                // Отметка и после неудачи: иначе молчащая сеть повторяла бы проверку каждые пять минут.
                PinRefresh.Mark(DateTimeOffset.Now);

                Write("сама", result);
            }
            catch (Exception ex)
            {
                PinRefresh.Mark(DateTimeOffset.Now);
                Journal.Write("пин", "проверка адресов (сама) не удалась: " + ex.GetBaseException().Message);
            }
        }
    }

    /// <summary>Итог в журнал — и кнопкой, и самой, одними словами.</summary>
    internal static void Write(string how, PinRefreshResult result)
    {
        if (result.Changes.Count > 0)
            Journal.Write("пин", $"адрес умер, заменён живым ({how}): " + string.Join("; ", result.Changes)
                + (result.Backup is null ? string.Empty : $"; копия hosts: {result.Backup}"));

        if (result.Dead.Count > 0)
            Journal.Write("пин", $"мёртвые без живой замены ({how}): {string.Join(", ", result.Dead.Take(8))}"
                + (result.Dead.Count > 8 ? $" и ещё {result.Dead.Count - 8}" : string.Empty));

        if (result.Silent.Count > 0)
            Journal.Write("пин", $"не ответили по DoH ({how}): {string.Join(", ", result.Silent)}");

        if (result.Error is { } error)
            Journal.Write("пин", $"проверка адресов ({how}): {error}");

        if (result.Changes.Count == 0 && result.Dead.Count == 0 && result.Error is null)
            Journal.Write("пин", $"проверка адресов ({how}): проверено {result.Checked}, менять нечего");
    }
}
