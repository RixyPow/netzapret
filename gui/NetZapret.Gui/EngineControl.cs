using System.Diagnostics;
using System.IO;
using NetZapret.Core;
using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>Чем закончилось управление движками.</summary>
internal sealed record EngineOutcome(bool Ok, string Message);

/// <summary>
/// Запуск, остановка и перезапуск движков — одним путём для всего окна.
/// </summary>
/// <remarks>
/// Прежде это лежало в трёх местах: на «Главной», в «VPN» и в уведомлении
/// главного окна. Каждое звало консольную программу само, и когда сборка
/// конфига появилась перед запуском, добавить её пришлось бы во все три —
/// то есть однажды забыть в одном и получить движки со старым конфигом
/// ровно там, где настройку только что и меняли.
/// </remarks>
internal static class EngineControl
{
    /// <summary>
    /// По одной операции за раз (отчёт reaass, 01.10) — см. <see cref="EngineGate"/>.
    /// </summary>
    private static readonly EngineGate Gate = new();

    /// <summary>
    /// Супервизор, поднятый последним запуском окна.
    /// </summary>
    /// <remarks>
    /// Остановка находит его и тогда, когда он ещё не взял замок и не записал
    /// состояние: иначе выход посреди запуска оставлял его жить без окна.
    /// </remarks>
    private static int? _spawned;

    /// <summary>Жив ли супервизор — в том числе поднимающийся, ещё без файла состояния.</summary>
    public static bool IsRunning => SupervisorHost.IsRunning(_spawned);

    private static int _starting;
    private static int _stopping;

    /// <summary>
    /// Движки поднимаются: идёт запуск или перезапуск, либо супервизор жив,
    /// а состояния ещё не написал.
    /// </summary>
    /// <remarks>
    /// «Главная» судила по файлу состояния, а он появляется, когда подняты
    /// все службы. Перезапуск из всплывающего сообщения шёл без неё, и всё
    /// это время на ней горело «Запустить»: 11.10 в 21:46 владелец нажал его
    /// через пять секунд после перезапуска — второй супервизор сменил первый,
    /// и движки поднимались заново.
    /// </remarks>
    public static bool Coming =>
        Volatile.Read(ref _stopping) == 0
        && (Volatile.Read(ref _starting) > 0 || IsRunning);

    /// <summary>
    /// Поднять или погасить прокси Telegram, который окно держит без движков
    /// (<see cref="TgWsProxyKeeper"/>), — в пуле, не в потоке окна.
    /// </summary>
    /// <remarks>
    /// Зовётся при запуске окна, с выключателя вкладки и после каждого запуска
    /// и остановки движков: надзор, поднятый до включения настройки, держит свой
    /// прокси, и пока он жив, второй на тот же порт не встанет.
    /// </remarks>
    public static void SyncTelegramProxy() => _ = Task.Run(() =>
    {
        // Выход гасит движки той же остановкой — поднимать прокси посреди
        // выхода незачем: объект задания всё равно унесёт его с окном.
        if (App.Exiting)
            return;

        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath);
            var state = SupervisorState.Load(SupervisorState.DefaultPath);

            bool enginesHoldIt = state is not null && state.IsSupervisorAlive()
                && state.Services.Any(s => s.Name == TgWsProxyService.ServiceName);

            TgWsProxyKeeper.Sync(settings, enginesHoldIt);
        }
        catch (Exception ex)
        {
            Journal.Write("TG Proxy", "без движков не поднялся: " + ex.GetBaseException().Message);
        }
    });

    /// <summary>Сколько ждать, пока поднятый супервизор станет виден остальным.</summary>
    private static readonly TimeSpan RegisterLimit = TimeSpan.FromSeconds(15);

    /// <param name="who">
    /// Кто позвал — пишется в журнал. Обязательный: 22:16 25.09 поднялись два
    /// супервизора подряд, и по журналу не понять было, откуда второй — кнопка,
    /// трей, всплывающее сообщение или автозапуск (26.09).
    /// </param>
    public static async Task<EngineOutcome> StartAsync(string who, CancellationToken cancellationToken)
    {
        Journal.Write("движки", $"запуск — {who}");
        Interlocked.Increment(ref _starting);

        try
        {
            using var turn = await Gate.StartAsync(cancellationToken);

            // Пока ждали очереди, движки поднял другой запуск: второй супервизор
            // сменил бы первый, погасив только что поднятые движки, — а человек
            // просто нажал кнопку, пока первый ждал подписку.
            if (turn.Superseded && SupervisorHost.IsRunning(_spawned))
            {
                Journal.Write("движки", $"запуск — {who}: движки уже поднимает запуск перед ним, второй не нужен");
                return new EngineOutcome(true, "Движки уже поднимаются.");
            }

            return await StartTurnAsync(who, turn.Token, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _starting);
        }
    }

    /// <summary>Сам запуск — внутри своей очереди.</summary>
    private static async Task<EngineOutcome> StartTurnAsync(string who, CancellationToken turn, CancellationToken caller)
    {
        try
        {
            // В пуле, а не в потоке окна. Разбор правил идёт дважды (конфиг
            // и списки десинка), по 43–143 мс, исключения десинка — 31–60 мс,
            // и после ответа подписки продолжение возвращалось в поток окна
            // вместе со сборкой конфига (замер 01.10). Сторож подвисаний
            // писал в миг «Запустить» «поток окна был занят 400 мс» при
            // стольких же мс процессора.
            var outcome = await Task.Run(() => StartCoreAsync(turn), turn);
            SyncTelegramProxy();
            return outcome;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            Journal.Write("движки", $"запуск — {who}: отменён остановкой");
            return new EngineOutcome(false, "Запуск отменён: движки остановили.");
        }
    }

    private static async Task<EngineOutcome> StartCoreAsync(CancellationToken cancellationToken)
    {
        var settings = AppSettings.Load(AppSettings.DefaultPath);
        var note = string.Empty;

        // Конфиг собирается при каждом запуске: иначе смена сервера, правки
        // маршрутов и переключение режима показываются новыми, а до туннеля
        // не доходят.
        if (settings.NeedsProxy)
        {
            var built = await TunnelConfig.BuildAsync(settings, cancellationToken);

            if (built.Warning is not null) note += built.Warning + " ";
            if (!built.Ok)
            {
                // Прежний конфиг — не повод не запуститься: он мог просто
                // устареть, а обход по старому лучше, чем никакого.
                if (!File.Exists(settings.ProxyConfigPath))
                    return new EngineOutcome(false, built.Message);

                note = built.Message + " Запускаю с ранее собранным конфигом. ";
            }
        }

        // При одном десинке и DNS «через туннель» — движок только ради DNS
        // (AppSettings.NeedsDnsEngine). Не собрался — десинк поднимается без
        // него, как до 03.10: хуже задуманного, но не хуже прежнего.
        bool dnsEngine = false;

        if (settings.NeedsDnsEngine)
        {
            var built = TunnelConfig.BuildDnsOnly(settings);
            dnsEngine = built.Ok;
            if (built.Warning is not null) note += built.Warning + " ";

            if (!built.Ok)
                note += built.Message + " ";
        }

        // Списки десинка — при каждом его запуске, с туннелем или без.
        // Прежде они писались одной сборкой туннеля, и с выключенным
        // туннелем winws2 брал вчерашние (issue #1).
        if (settings.NeedsDesync)
        {
            var lists = TunnelConfig.WriteDesyncLists(settings);

            if (!lists.Ok)
                note += lists.Message + " ";
        }

        var exe = Environment.ProcessPath;

        if (exe is null)
            return new EngineOutcome(false, "Не удалось определить путь к программе.");

        // Последняя точка, где запуск ещё можно отменить: дальше супервизор
        // поднят, и снимать его будет уже остановка.
        cancellationToken.ThrowIfCancellationRequested();

        int spawned;

        try
        {
            // Та же программа с ключом супервизора, а не консольная рядом:
            // окно обязано работать и там, где её нет вовсе. Без повышения —
            // окно и так запущено от администратора, а движкам нужны
            // именно его права.
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = SupervisorHost.BuildArguments(settings, dnsEngine),
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (started is null)
                return new EngineOutcome(false, "Не удалось запустить: процесс не создан.");

            spawned = started.Id;
        }
        catch (Exception ex)
        {
            return new EngineOutcome(false, "Не удалось запустить: " + ex.GetBaseException().Message);
        }

        _spawned = spawned;
        Gate.Started();

        // Очередь отпускается, когда супервизор виден остальным: запуск или
        // остановка следом должны его найти. Остановка, пришедшая раньше,
        // ожидание прерывает — снимет она его по номеру.
        try
        {
            await SupervisorHost.WaitRegisteredAsync(spawned, RegisterLimit, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        return new EngineOutcome(true, note + "Движки поднимаются.");
    }

    /// <remarks>
    /// Запуски, попросившие очереди раньше, отменяются (<see cref="EngineGate"/>):
    /// «Выход» посреди перезапуска гасил прежний супервизор, а запуск,
    /// дождавшись подписки, поднимал новый уже после выхода.
    /// </remarks>
    public static async Task<string> StopAsync(string who, CancellationToken cancellationToken)
    {
        Journal.Write("движки", $"остановка — {who}");
        Interlocked.Increment(ref _stopping);

        try
        {
            using var turn = await Gate.StopAsync(cancellationToken);

            var result = await SupervisorHost.StopAsync(cancellationToken, _spawned);
            _spawned = null;

            // Остановленный надзор мог держать прокси — теперь его место свободно.
            SyncTelegramProxy();
            return result;
        }
        finally
        {
            Interlocked.Decrement(ref _stopping);
        }
    }

    public static async Task<EngineOutcome> RestartAsync(string who, CancellationToken cancellationToken)
    {
        Journal.Write("движки", $"перезапуск — {who}");
        Interlocked.Increment(ref _starting);

        try
        {
            return await RestartTurnAsync(who, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _starting);
        }
    }

    private static async Task<EngineOutcome> RestartTurnAsync(string who, CancellationToken cancellationToken)
    {
        using var turn = await Gate.RestartAsync(cancellationToken);

        try
        {
            await SupervisorHost.StopAsync(cancellationToken, _spawned);
            _spawned = null;

            // Пауза, а не гонка: супервизор освобождает TUN и снимает фильтр
            // не мгновенно, и запуск, начатый сразу, наткнулся бы на ещё живой
            // адаптер — sing-box падает с «Cannot create a file when that file
            // already exists» и не поднимается ни разу из трёх попыток.
            await Task.Delay(TimeSpan.FromSeconds(3), turn.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Journal.Write("движки", $"перезапуск — {who}: отменён остановкой");
            return new EngineOutcome(false, "Перезапуск отменён: движки остановили.");
        }

        Journal.Write("движки", $"запуск — {who}");
        return await StartTurnAsync(who, turn.Token, cancellationToken);
    }
}
