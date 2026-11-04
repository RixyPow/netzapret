using NetZapret.Core;
using NetZapret.Proxy;

namespace NetZapret.Gui;

/// <summary>
/// Раз в <see cref="PinRefresh.Every"/> (6 часов с 05.11) проверяет пины к посредникам (<see cref="PinRefresh"/>).
/// </summary>
/// <remarks>
/// <para>
/// Владелец 03.11: «их банят только так… стоит внедрить автозамену», «каждый
/// час — перебор, лучше раз в сутки». Выключатель — <see cref="AppSettings.AutoRepin"/>
/// на вкладке DNS, читается на каждом круге. С 05.11 срок — 6 часов, по образцу
/// Zapret GUI: проверка стала дешёвой (только первый адрес имени), а переезд
/// посредника ловится, лишь пока о нём спрашивают.
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
                // Срок смотрится ещё раз под замком проверки, и отметку ставит она же
                // (и после неудачи): нажатая в это время кнопка уже проверила — следом
                // второй залп не идёт.
                if (await PinRefresh.RunIfDueAsync(CancellationToken.None).ConfigureAwait(false) is { } result)
                    Write("сама", result);
            }
            catch (Exception ex)
            {
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

        if (result.Moved.Count > 0)
            Journal.Write("пин", $"посредник переехал ({how}): " + string.Join("; ", result.Moved));

        if (result.Reordered.Count > 0)
            Journal.Write("пин", $"переставлен живой первым ({how}): " + string.Join("; ", result.Reordered));

        if (result.Dead.Count > 0)
            Journal.Write("пин", $"мёртвые без живой замены ({how}): {string.Join(", ", result.Dead.Take(8))}"
                + (result.Dead.Count > 8 ? $" и ещё {result.Dead.Count - 8}" : string.Empty));

        if (result.Silent.Count > 0)
            Journal.Write("пин", $"не ответили по DoH ({how}): {string.Join(", ", result.Silent)}");

        if (result.Error is { } error)
            Journal.Write("пин", $"проверка адресов ({how}): {error}");

        if (result.Changes.Count == 0 && result.Moved.Count == 0 && result.Reordered.Count == 0
            && result.Dead.Count == 0 && result.Error is null)
        {
            Journal.Write("пин", $"проверка адресов ({how}): проверено {result.Checked}, менять нечего"
                + (result.Suspects > 0 ? $"; не ответили {result.Suspects} — заменю, если промолчат и в следующий раз" : string.Empty));
        }
    }
}
