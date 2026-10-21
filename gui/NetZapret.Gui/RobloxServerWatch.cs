using System.IO;
using NetZapret.Core.Rules;
using NetZapret.Core.Services;

namespace NetZapret.Gui;

/// <summary>
/// Сторож серверов Roblox: новые адреса из журнала Roblox — в список «Игровых серверов»
/// (владелец 21.10, по образцу <see cref="DiscordVoiceWatch"/>).
/// </summary>
/// <remarks>
/// <para>
/// Раз в полминуты, пока движки работают и часть «Игровые серверы (адреса)»
/// стоит «через VPN» (<see cref="RobloxServerLearn.RoutedToVpn"/>): читает новые
/// строки журналов игрока Roblox и дописывает в список сети серверов, которых
/// в нём нет, — плейсы на Amazon, вне сетей самой Roblox.
/// </para>
/// <para>
/// Маршруты TUN движок берёт при запуске, поэтому дописанное заработает после
/// перезапуска движков. Сам сторож не перезапускает — матч оборвался бы:
/// предлагает (<see cref="RestartOffer"/>) и пишет в журнал. Первый вход на новый
/// сервер так и идёт мимо туннеля; помогает следующим — на тот же сервер и
/// соседей по его сети /20.
/// </para>
/// </remarks>
internal static class RobloxServerWatch
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(30);

    private static readonly LogTail Tail = new();

    private static bool _started;

    /// <summary>Последняя ошибка — чтобы не писать одну и ту же раз в полминуты.</summary>
    private static string? _lastFailure;

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
            await Task.Delay(Every).ConfigureAwait(false);

            try
            {
                var added = Check();

                if (added.Count == 0)
                    continue;

                var networks = string.Join(", ", added);

                Journal.Write("Roblox", $"дописаны сети серверов из журнала Roblox: {networks} — заработают после перезапуска движков");

                RestartOffer.Show($"Roblox: новые адреса игровых серверов — {networks}");

                _lastFailure = null;
            }
            catch (Exception ex)
            {
                var message = ex.GetBaseException().Message;

                if (message != _lastFailure)
                    Journal.Write("Roblox", "не вышло прочитать или дописать: " + message);

                _lastFailure = message;
            }
        }
    }

    private static IReadOnlyList<string> Check()
    {
        // Выключатель в «Настройках маршрутов» — читается на каждом круге, без перезапуска.
        if (!Core.AppSettings.Load(Core.AppSettings.DefaultPath).LearnRobloxServers)
            return [];

        if (!EngineControl.IsRunning || !File.Exists(UserRulesFile.DefaultPath))
            return [];

        if (!RobloxServerLearn.RoutedToVpn(RulesShare.Read(UserRulesFile.DefaultPath)))
            return [];

        var log = Tail.ReadNew(RobloxServerLearn.LogPaths());

        return log.Length == 0 ? [] : RobloxServerLearn.Learn(log);
    }
}
