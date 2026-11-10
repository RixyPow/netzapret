using NetZapret.Supervisor;

namespace NetZapret.Gui;

/// <summary>
/// Раз в секунду смотрит, не оставил ли кто просьбу о движках (<see cref="EngineRequests"/>),
/// и исполняет её тем же кодом, что кнопки окна.
/// </summary>
/// <remarks>
/// <para>
/// Просит <c>nz start</c>, <c>nz stop</c> и <c>nz restart</c>: прав администратора у неё нет,
/// у окна есть. Своей логики здесь нет — три вызова <see cref="EngineControl"/>, и очередь
/// у них общая с кнопками (<see cref="EngineGate"/>): просьба посреди запуска не поднимет
/// второй надзор.
/// </para>
/// <para>
/// Опросом, а не слежением за папкой: в <c>runtime\</c> пишутся журналы, и события о них
/// будили бы окно куда чаще, чем проверка «есть ли файл» раз в секунду.
/// </para>
/// </remarks>
internal static class EngineRequestWatch
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

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
        while (true)
        {
            await Task.Delay(Tick).ConfigureAwait(false);

            // Выход гасит движки сам; просьба, пришедшая в этот миг, осталась бы без ответа
            // и устарела — исполнять её наперегонки с выходом незачем.
            if (App.Exiting)
                continue;

            EngineRequest? request;

            try
            {
                request = EngineRequests.Take(DateTimeOffset.Now);
            }
            catch (Exception)
            {
                continue;
            }

            if (request is null)
                continue;

            var reply = await DoAsync(request).ConfigureAwait(false);

            try
            {
                EngineRequests.Answer(reply);
            }
            catch (Exception ex)
            {
                Journal.Write("движки", "ответ на просьбу не записан: " + ex.GetBaseException().Message);
            }
        }
    }

    private static async Task<EngineReply> DoAsync(EngineRequest request)
    {
        // Кто позвал — в журнал, как у кнопок (EngineControl): по нему потом
        // видно, что движки погасил не человек.
        string who = "просьба «" + request.From + "»";

        try
        {
            switch (request.Verb)
            {
                case EngineVerb.Start:
                    return Reply(request, await EngineControl.StartAsync(who, CancellationToken.None).ConfigureAwait(false));

                case EngineVerb.Stop:
                    var said = await EngineControl.StopAsync(who, CancellationToken.None).ConfigureAwait(false);
                    return new EngineReply { Id = request.Id, Ok = true, Message = said };

                default:
                    return Reply(request, await EngineControl.RestartAsync(who, CancellationToken.None).ConfigureAwait(false));
            }
        }
        catch (Exception ex)
        {
            var message = ex.GetBaseException().Message;
            Journal.Write("движки", $"{who}: не вышло — {message}");

            return new EngineReply { Id = request.Id, Ok = false, Message = "Не вышло: " + message };
        }
    }

    private static EngineReply Reply(EngineRequest request, EngineOutcome outcome) =>
        new() { Id = request.Id, Ok = outcome.Ok, Message = outcome.Message };
}
