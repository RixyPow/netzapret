using NetZapret.Core;

namespace NetZapret.Supervisor;

/// <summary>
/// Прокси Telegram без движков: его держит сам процесс окна (<see cref="AppSettings.TelegramProxyAlways"/>).
/// </summary>
/// <remarks>
/// <para>
/// Владелец 12.10: прокси должен жить и при остановленных движках. Поднимается
/// тем же <see cref="TgWsProxyService"/> под своим <see cref="ProcessSupervisor"/>:
/// тот же перезапуск при падении, тот же журнал <c>runtime\tg-ws-proxy.log</c>,
/// а объект задания уносит прокси вместе с окном. Файл состояния — свой:
/// общий говорил бы «Главной», что движки запущены.
/// </para>
/// <para>
/// Надзор движков такой прокси не поднимает (<see cref="AppSettings.TelegramProxyWithEngines"/>).
/// Но надзор, запущенный до включения настройки, свой ещё держит — пока он
/// жив, этот не поднимается: порт один. Окно зовёт <see cref="Sync"/> снова
/// после каждого запуска и остановки движков.
/// </para>
/// </remarks>
public static class TgWsProxyKeeper
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _stop;
    private static Task? _running;

    public static string StatePath => Path.Combine("runtime", "tg-ws-proxy-state.json");

    /// <summary>Держит ли окно прокси прямо сейчас.</summary>
    public static bool Running
    {
        get
        {
            lock (Gate)
                return _running is { IsCompleted: false };
        }
    }

    /// <summary>Поднимает или гасит прокси по настройкам.</summary>
    /// <param name="enginesHoldIt">Надзор движков сам держит прокси — тогда ждём его ухода.</param>
    public static void Sync(AppSettings settings, bool enginesHoldIt)
    {
        bool wanted = settings.TelegramProxy
            && settings.TelegramProxyAlways
            && TgWsProxy.IsSecret(settings.TelegramProxySecret)
            && !enginesHoldIt;

        if (wanted)
            Start(settings);
        else
            Stop();
    }

    private static void Start(AppSettings settings)
    {
        lock (Gate)
        {
            if (_running is { IsCompleted: false })
                return;

            var service = new TgWsProxyService(
                TgWsProxy.Executable(),
                settings.TelegramProxyPort,
                settings.TelegramProxySecret!,
                Path.GetFullPath("."))
            {
                OutputLogPath = Path.Combine("runtime", "tg-ws-proxy.log"),
            };

            if (service.ValidatePrerequisites() is not null)
                return;

            var supervisor = new ProcessSupervisor([service], new SupervisorOptions { StatePath = StatePath });
            _stop = new CancellationTokenSource();
            var token = _stop.Token;

            _running = Task.Run(() => supervisor.RunAsync(token));
        }
    }

    /// <summary>Гасит прокси окна и ждёт, пока он уйдёт (не дольше 10 с).</summary>
    public static void Stop()
    {
        Task? running;

        lock (Gate)
        {
            _stop?.Cancel();
            running = _running;
            _running = null;
        }

        try
        {
            running?.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException)
        {
            // Упал сам — гасить уже нечего.
        }
    }
}
