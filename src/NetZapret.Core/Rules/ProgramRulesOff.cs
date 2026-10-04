namespace NetZapret.Core.Rules;

/// <summary>
/// Правила «программа → …» сняты, пока обход по программе на переработке (владелец, 04.10).
/// </summary>
/// <remarks>
/// <para>
/// Окно обещало: «Через VPN» для программы заводит в туннель весь трафик
/// машины. С 01.10 это верно только без десинка
/// (<see cref="ProgramCapture.ForcesFullCapture"/>): в «Гибриде» правило
/// действует лишь на соединения, попавшие в туннель по другим правилам,
/// а серверы игры по голым адресам идут напрямую. Человек ставил игру
/// «через VPN» и не получал ничего — Dead by Daylight у Максима, 04.10:
/// «добавление программы ничем не помогло». Владелец: «убери их, а то мы
/// их обманываем».
/// </para>
/// <para>
/// Снимаются все правила по программе, какой бы путь у них ни стоял, один
/// раз (<see cref="AppSettings.ProgramRulesRemoved"/>). Снятые запоминаются
/// в настройках (<see cref="AppSettings.RemovedProgramRules"/>) — чтобы
/// предложить их вернуть, когда обход по программе переделают.
/// </para>
/// </remarks>
public static class ProgramRulesOff
{
    /// <summary>
    /// Снимает правила по программе, если это ещё не делалось.
    /// </summary>
    /// <returns>Снятые правила словами: «Diablo IV.exe → VPN»; пусто — снимать было нечего или уже снято.</returns>
    public static IReadOnlyList<string> RemoveOnce(string settingsPath, string rulesPath)
    {
        var settings = AppSettings.Load(settingsPath);

        if (settings.ProgramRulesRemoved == true)
            return [];

        var file = UserRulesFile.Load(rulesPath);
        var programs = file.Entries.Where(e => e.Match == MatchKind.Process).ToList();

        if (programs.Count > 0)
        {
            file.ReplaceAll(file.Entries.Where(e => e.Match != MatchKind.Process).ToList());
            file.Save();
        }

        var removed = programs.Select(Describe).ToList();

        (settings with
        {
            ProgramRulesRemoved = true,
            RemovedProgramRules = removed.Count > 0 ? removed : settings.RemovedProgramRules,
        }).Save(settingsPath);

        return removed;
    }

    /// <summary>Правило словами: «Diablo IV.exe → VPN».</summary>
    public static string Describe(UserRuleEntry entry) => $"{entry.Value} → {entry.DescribeMode()}";
}
