namespace NetZapret.Zapret;

/// <summary>
/// Шаги нашего модуля, которые секция пресета ставит перед своим рецептом
/// и которые свой профиль обязан унести с собой.
/// </summary>
/// <remarks>
/// <para>
/// Свой профиль (рецепт, выбранный в «Маршрутах») встаёт перед секциями
/// пресета и забирает имя себе целиком (<see cref="WinwsCommandLine"/>). У секций
/// «googlevideo.com (CDN сервера)» и «youtube.com (интерфейс)» пресета Universal
/// V13 YT FIX перед рецептом стоит шаг <c>nz_first2</c> — он ставит приветствию
/// малое окно и сбрасывает зависшее соединение. Профиль рецепта нёс только сам
/// рецепт, и YT FIX молча выключался: 11.11 в отчёте из Тулы (Ростелеком)
/// у человека в «Маршрутах» стояли свои рецепты для googlevideo и youtube,
/// и узлы видео через обход отвечали 0 из 24 — малое окно не включилось ни разу.
/// </para>
/// <para>
/// Шаг <c>nz_first2</c> рецептом не является: соединению, которому секция
/// по адресам не убрала множитель окна, он не меняет ни одного пакета. Поэтому
/// он едет вместе с любым рецептом, кроме «не трогать» (pass обещает, что имя
/// не тронет никто), и не дублируется, если выбран рецепт самой секции YT FIX.
/// </para>
/// </remarks>
public static class PresetCompanions
{
    /// <summary>Шаг модуля, который сопровождает рецепт.</summary>
    public const string Step = "nz_first2";

    /// <summary>
    /// Шаги <see cref="Step"/> секций, чьи списки покрывают эти имена, — без
    /// шага для SYN: тот живёт в секции по адресам и своему профилю не нужен.
    /// </summary>
    public static IReadOnlyList<string> ForDomains(
        ZapretPreset preset,
        string? zapretRoot,
        IReadOnlyList<string> domains)
    {
        if (domains.Count == 0)
            return [];

        var steps = new List<string>();

        foreach (var section in PresetZones.Build(preset, zapretRoot).AllFor(domains))
        {
            foreach (var recipe in section.DesyncRecipes)
            {
                if (IsCompanion(recipe) && !recipe.Split(':').Skip(1).Contains("syn", StringComparer.OrdinalIgnoreCase)
                    && !steps.Contains(recipe, StringComparer.OrdinalIgnoreCase))
                {
                    steps.Add(recipe);
                }
            }
        }

        return steps;
    }

    /// <summary>Шаги своего профиля: сопровождающие шаги перед рецептом.</summary>
    public static IReadOnlyList<string> With(IReadOnlyList<string> companions, IReadOnlyList<string> steps)
    {
        if (companions.Count == 0 || steps.Count == 0 || RecipeCatalog.IsPass(steps) || steps.Any(IsCompanion))
            return steps;

        return [.. companions, .. steps];
    }

    private static bool IsCompanion(string step)
    {
        int colon = step.IndexOf(':');
        var function = colon < 0 ? step : step[..colon];

        return function.Equals(Step, StringComparison.OrdinalIgnoreCase);
    }
}
