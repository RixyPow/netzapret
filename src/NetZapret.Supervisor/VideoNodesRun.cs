using NetZapret.Core;
using NetZapret.Proxy;

namespace NetZapret.Supervisor;

/// <summary>
/// Замер зарубежных узлов видео YouTube с оглядкой на то, что сейчас поднято.
/// </summary>
/// <remarks>
/// Сам замер (<see cref="VideoNodes"/>) о движках не знает, а без этого знания
/// его числа не прочесть: работает ли десинк и можно ли пройти мимо него
/// с портов пробы, решается по состоянию надзора. Здесь — чтобы окно
/// и <c>nz</c> решали одинаково.
/// </remarks>
public static class VideoNodesRun
{
    /// <summary>
    /// Мерит узлы; <c>null</c> — мерить бессмысленно, и <paramref name="why"/> говорит почему.
    /// </summary>
    public static async Task<(VideoNodesReport? Report, string? Why)> MeasureAsync(
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var state = SupervisorState.Load(SupervisorState.DefaultPath);
        bool engines = state is not null && state.IsSupervisorAlive();
        var winws = engines ? state!.Services.FirstOrDefault(s => s.Name == "winws2") : null;

        // Пробы идут через физический адаптер, мимо туннеля, а аварийный
        // выключатель туда не пускает ничего: вышло бы «узлы закрыты» про сеть,
        // в которой YouTube идёт туннелем.
        if (engines && settings.KillSwitchActive)
            return (null, "аварийный выключатель закрывает всё мимо туннеля — узлы видео не мерились");

        var report = await VideoNodes.MeasureAsync(
            desync: winws is not null,
            aside: winws is null or { KeepsProbesOut: true },
            cancellationToken);

        return (report with { Recipe = Recipe(settings, desync: winws is not null) }, null);
    }

    /// <summary>
    /// Пресет и стоит ли в нём рецепт узлов видео — строкой для отчёта.
    /// </summary>
    /// <remarks>
    /// От этого зависит, как читать числа: с шагом <c>nz_first2</c> срезанное
    /// соединение сбрасывается и повторяется с малым окном, без него — виснет.
    /// Читается файл пресета, а не строка запуска: шаг либо есть в пресете,
    /// либо его нет, а у работающего winws2 без прав строку не спросить.
    /// </remarks>
    public static string Recipe(AppSettings settings, bool desync)
    {
        if (!desync)
            return "Десинк не работает: числа ниже — о сети без обхода.";

        var name = settings.PresetName;

        if (string.IsNullOrEmpty(name))
            return "Пресет: не выбран.";

        string[] steps;

        try
        {
            steps = File.ReadLines(Path.Combine(Zapret.ZapretPaths.PresetDirectory, name + ".txt"))
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("--lua-desync=nz_first2", StringComparison.Ordinal))
                .Select(line => line["--lua-desync=".Length..])
                .Distinct()
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"Пресет: {name} (файл не прочитан).";
        }

        return steps.Length == 0
            ? $"Пресет: {name}; рецепта узлов видео (малое окно по надобности) в нём нет."
            : $"Пресет: {name}; рецепт узлов видео есть: {string.Join(", ", steps)}.";
    }
}
