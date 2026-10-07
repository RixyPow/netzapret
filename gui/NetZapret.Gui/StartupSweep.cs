using System.IO;
using NetZapret.Core;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>
/// «Замерить все» при запуске программы — если включено в «Настройках туннеля».
/// </summary>
/// <remarks>
/// <para>
/// Владелец 07.10: «добавь возможность включить это в настройках впн»
/// (<see cref="AppSettings.MeasureOnStart"/>). Сам замер — в библиотеке
/// (<see cref="ServerSweep"/>), тот же, что у кнопки; здесь — только когда
/// и что мерить: список подписок (<see cref="SubscriptionBook"/>) живёт в окне.
/// </para>
/// <para>
/// Меряется то, через что пойдёт туннель: серверы подписок в работе и ключи —
/// тем же пулом, что собирает конфиг, с теми же тегами, — а при WARP только
/// WARP. Выключенные из работы подписки туннель не берёт, и ходить к их панелям
/// при каждом запуске незачем: их меряет кнопка. Пул читает свежий запас
/// подписок без сети — движки только что прочитали их сами.
/// </para>
/// </remarks>
internal static class StartupSweep
{
    /// <summary>
    /// Через сколько после запуска мерить: движкам — подняться, сети после
    /// входа в Windows — ожить. При работающем движке свои выходы меряет он,
    /// без процесса sing-box на каждый сервер.
    /// </summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMinutes(1);

    private static CancellationTokenSource? _stop;
    private static Task? _running;

    /// <summary>Заводит замер в фоне; выключен в настройках — ничего не делает.</summary>
    public static void Start()
    {
        try
        {
            if (!AppSettings.Load(AppSettings.DefaultPath).MeasureOnStart)
                return;
        }
        catch (Exception)
        {
            return;
        }

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _running = Task.Run(() => RunAsync(token));
    }

    /// <summary>
    /// Останавливает замер при выходе из программы и ждёт, пока пробник
    /// погасит свои процессы.
    /// </summary>
    /// <remarks>
    /// Процессы пробника не в Job-объекте: брошенные на выходе, они остались бы
    /// работать без окна — по процессу sing-box на сервер. Отмена доходит
    /// до каждой проверки, и та в <c>finally</c> гасит свой движок.
    /// </remarks>
    public static void Stop()
    {
        if (_running is not { IsCompleted: false } running)
            return;

        _stop?.Cancel();

        try
        {
            running.Wait(TimeSpan.FromSeconds(6));
        }
        catch (Exception)
        {
            // Отмена и сбой замера на выходе не важны: важно, что он кончился.
        }
    }

    private static async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Delay, cancellationToken);

            // Движки ещё поднимаются (автозапуск ждёт сеть и повторяет) —
            // подождать до двух минут: через движок замер бережнее.
            for (int i = 0; i < 24 && Starting(); i++)
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            var settings = AppSettings.Load(AppSettings.DefaultPath);

            if (!settings.MeasureOnStart)
                return;

            var health = ServerHealthCache.Load();
            bool engine = EngineRunning();

            // WARP — путь вместо подписок (Warp.TunnelExits): мерить их незачем.
            // Его выход пробник не берёт, меряет только работающий движок.
            if (settings.WarpEnabled)
            {
                if (!engine)
                {
                    Journal.Write("замер", "при запуске: WARP меряется только работающим движком, а он не поднят");
                    return;
                }

                await ServerSweep.ThroughEngineAsync(Warp.Exits(), health, cancellationToken, settings);
                Journal.Write("замер", "при запуске: WARP " + (health.Find(Warp.Exits()[0].Tag) is { Success: true } ? "отвечает" : "не отвечает"));
                return;
            }

            var servers = await PoolAsync(settings, cancellationToken);

            if (servers.Count == 0)
            {
                Journal.Write("замер", "при запуске: замерять нечего — серверов в работе нет");
                return;
            }

            var singBox = Path.Combine(AppContext.BaseDirectory, "engines", "sing-box", "sing-box.exe");

            if (!File.Exists(singBox))
            {
                Journal.Write("замер", "при запуске: sing-box не найден рядом с программой — замерять нечем");
                return;
            }

            int alive = await ServerSweep.RunAsync(servers, singBox, engine, health, null, cancellationToken, settings);

            Journal.Write("замер", $"при запуске: отвечают {alive} из {servers.Count}"
                + (engine ? " (через движок)" : " (пробником, движок не поднят)"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Выход из программы посреди замера.
        }
        catch (Exception ex)
        {
            // Без текста ошибки: в нём мог бы оказаться адрес подписки.
            Journal.Write("замер", "при запуске не удался: " + ex.GetType().Name);
        }
    }

    /// <summary>
    /// Серверы подписок в работе и ключи — тем же пулом и с теми же тегами,
    /// что у конфига туннеля (<see cref="TunnelConfig"/>).
    /// </summary>
    private static async Task<List<ProxyServer>> PoolAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var book = SubscriptionBook.Load();

        var sources = book.Pool
            .Select(e => new PoolSource(e.Name, e.Url))
            .ToList();

        if (sources.Count == 0 && !string.IsNullOrWhiteSpace(settings.SubscriptionUrl))
            sources.Add(new PoolSource("Основная", settings.SubscriptionUrl));

        if (sources.Count == 0 && book.PoolKeys.Count == 0)
            return [];

        var pool = await SubscriptionPool.BuildAsync(sources, cancellationToken, keys: book.PoolKeys);

        return pool.Servers
            .Where(s => s.IsUsableOutbound && s.IsMeasurable)
            .DistinctBy(s => s.Tag)
            .ToList();
    }

    /// <summary>Работает ли движок туннеля — тогда свои выходы меряет он.</summary>
    private static bool EngineRunning() =>
        SupervisorState.Load(SupervisorState.DefaultPath) is { } state
        && state.IsSupervisorAlive()
        && state.Services.Any(s => s.Name == "sing-box");

    /// <summary>Надзор жив, а движок туннеля ещё не здоров — поднимается.</summary>
    private static bool Starting() =>
        SupervisorHost.IsRunning()
        && !(SupervisorState.Load(SupervisorState.DefaultPath) is { } state
            && state.Services.Any(s => s.Name == "sing-box" && s.Health == ServiceHealth.Healthy));
}
