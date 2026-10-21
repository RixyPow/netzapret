using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using NetZapret.Core;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Gui;

/// <summary>
/// Наблюдение ради автопополнения — пока программа запущена (владелец 19.10).
/// </summary>
/// <remarks>
/// <para>
/// Прежде окно начинало наблюдение при каждом запуске, если у какой-то программы
/// в «Маршрутах» включено автопополнение, и оно шло весь день: у владельца
/// ради REPO.exe — 11,7 тыс. соединений за утро без единого запуска игры, и он
/// спросил, кто его включает. Теперь раз в три секунды смотрим, запущена ли
/// программа (<see cref="ProgramPresence"/>): запустилась — наблюдение
/// начинается, закрылась — останавливается, если его начали мы.
/// </para>
/// <para>
/// Три секунды — первые соединения игры при запуске важнее всего, их и
/// пополняют; перебор процессов стоит миллисекунды и идёт в фоне.
/// </para>
/// </remarks>
internal static class AutoFillWatch
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(3);

    private static readonly ProgramPresence Presence = new();

    private static bool _started;

    public static void Start(Dispatcher ui)
    {
        if (_started)
            return;

        _started = true;
        _ = Task.Run(() => RunAsync(ui));
    }

    private static async Task RunAsync(Dispatcher ui)
    {
        while (true)
        {
            try
            {
                var settings = AppSettings.Load(AppSettings.DefaultPath);

                // И игры каталога, чьи серверы дописываются сейчас (21.10, ARC Raiders).
                var wanted = settings.ProgramRoutes
                    .Where(p => p.AutoFill)
                    .Select(p => p.Program)
                    .Concat(LearnedGames(settings).Select(g => g.Program))
                    .ToList();

                var change = Presence.Update(Running(), wanted, DateTimeOffset.Now);

                if (change == PresenceChange.Appeared && Presence.Seen is { } program)
                    await ui.InvokeAsync(() => Views.WatchView.StartForProgramAsync(program)).Task.Unwrap().ConfigureAwait(false);
                else if (change == PresenceChange.Gone)
                    await ui.InvokeAsync(Views.WatchView.StopForProgram).Task.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Не прочитались настройки или процессы — посмотрим через три секунды.
            }

            await Task.Delay(Every).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Игры каталога, чьи серверы дописываются сейчас (<see cref="GameServerLearn.Active"/>).
    /// </summary>
    /// <remarks>
    /// <see cref="RulesShare.Read"/>: испорченный файл правил фоновому трогать нельзя.
    /// Правила читаются, только если выключатель хоть одной игры включён.
    /// </remarks>
    internal static IReadOnlyList<LearnedGame> LearnedGames(AppSettings settings) =>
        GameServerLearn.All.Any(g => g.On(settings)) && File.Exists(UserRulesFile.DefaultPath)
            ? GameServerLearn.Active(settings, RulesShare.Read(UserRulesFile.DefaultPath))
            : [];

    private static List<string> Running()
    {
        var names = new List<string>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
                names.Add(process.ProcessName);
        }

        return names;
    }
}
