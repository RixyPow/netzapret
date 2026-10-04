using System.IO;
using System.Windows;
using System.Windows.Threading;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Gui;

/// <summary>
/// Сторож голоса Discord: новые адреса из журнала Discord — в список голоса (владелец, 04.10).
/// </summary>
/// <remarks>
/// <para>
/// Раз в полминуты, пока движки работают и голос стоит «через VPN»
/// (<see cref="DiscordVoiceLearn.RoutedToVpn"/>): читает новые строки журналов
/// Discord и дописывает в список сети тех голосовых серверов, которых в нём
/// нет. Работает, пока жив интерфейс — окно или значок в трее.
/// </para>
/// <para>
/// Маршруты TUN движок берёт при запуске, поэтому дописанное заработает
/// после перезапуска. Сам сторож не перезапускает — звонок оборвался бы
/// посреди разговора: предлагает тем же уведомлением, что и смена маршрута
/// (<see cref="MainWindow.OfferRestart"/>), и пишет в журнал.
/// </para>
/// <para>
/// Правила читаются <see cref="RulesShare.Read"/>, а не <see cref="UserRulesFile.Load"/>:
/// тот испорченный файл отодвигает в сторону, а фоновому сторожу трогать
/// файл человека нельзя.
/// </para>
/// </remarks>
internal static class DiscordVoiceWatch
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private static readonly LogTail Tail = new();

    private static bool _started;

    /// <summary>Последняя ошибка — чтобы не писать одну и ту же раз в полминуты.</summary>
    private static string? _lastFailure;

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
            await Task.Delay(Every).ConfigureAwait(false);

            try
            {
                var added = Check();

                if (added.Count == 0)
                    continue;

                var networks = string.Join(", ", added);

                Journal.Write("голос Discord", $"дописаны сети из журнала Discord: {networks} — заработают после перезапуска движков");

                _ = ui.BeginInvoke(() =>
                    Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault()
                        ?.OfferRestart($"Голос Discord: новые адреса серверов — {networks}"));

                _lastFailure = null;
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;

                if (message != _lastFailure)
                    Journal.Write("голос Discord", "не вышло прочитать или дописать: " + message);

                _lastFailure = message;
            }
        }
    }

    private static IReadOnlyList<string> Check()
    {
        // Выключатель в «Ещё → Прочее» — читается на каждом круге, без перезапуска.
        if (!Core.AppSettings.Load(Core.AppSettings.DefaultPath).LearnDiscordVoice)
            return [];

        if (!EngineControl.IsRunning || !File.Exists(UserRulesFile.DefaultPath))
            return [];

        if (!DiscordVoiceLearn.RoutedToVpn(RulesShare.Read(UserRulesFile.DefaultPath)))
            return [];

        var log = Tail.ReadNew(DiscordVoiceLearn.LogPaths());

        return log.Length == 0 ? [] : DiscordVoiceLearn.Learn(log);
    }
}
