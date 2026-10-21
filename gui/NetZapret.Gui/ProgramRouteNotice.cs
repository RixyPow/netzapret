using System.Windows;
using NetZapret.Core.Connections;
using NetZapret.Core.Rules;

namespace NetZapret.Gui;

/// <summary>
/// Что сказать, когда наблюдение дописало в маршрут программы (13.10).
/// </summary>
/// <remarks>
/// <para>
/// В журнал — каждое дописанное. Перезапуск — предложить, только если
/// маршрут этого списка включён: выключенный ничего не уводит, и
/// перезапуск ради него ничего не изменит. Движок берёт списки при запуске,
/// как и у голоса Discord (<see cref="DiscordVoiceWatch"/>), — сами не
/// перезапускаем: игра оборвалась бы посреди матча.
/// </para>
/// <para>
/// Предложение — не чаще раза в две минуты: в первые минуты игра открывает
/// десяток имён подряд, и уведомление на каждое заслонило бы окно.
/// </para>
/// </remarks>
internal static class ProgramRouteNotice
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMinutes(2);

    private static DateTime _offeredAt = DateTime.MinValue;

    /// <summary>Подписчик <see cref="Supervisor.ConnectionWatch.Learned"/> — зовётся из потока чтения.</summary>
    public static void Learned(ProgramLearned learned)
    {
        var what = learned.IsDomain ? "домен" : "адреса";

        Journal.Write("маршрут программы", $"{learned.Program}: дописан {what} {learned.Value}");

        if (!Enabled(learned.ListPath) || DateTime.UtcNow - _offeredAt < Quiet)
            return;

        _offeredAt = DateTime.UtcNow;

        RestartOffer.Show($"{learned.Program}: в маршрут дописано {learned.Value}");
    }

    /// <summary>Включён ли маршрут этого списка.</summary>
    /// <remarks>
    /// <see cref="RulesShare.Read"/>, а не <see cref="UserRulesFile.Load"/>:
    /// тот испорченный файл отодвигает в сторону, а фоновому трогать файл
    /// человека нельзя. Списки программ — по именам (HostList), список
    /// адресов игры из каталога — по адресам (IpSet, 21.10).
    /// </remarks>
    private static bool Enabled(string listPath)
    {
        try
        {
            return RulesShare.Read(UserRulesFile.DefaultPath)
                .Any(e => e.Enabled && !e.Off && e.Match is (MatchKind.HostList or MatchKind.IpSet) && e.Matches(listPath));
        }
        catch (Exception)
        {
            return false;
        }
    }
}
