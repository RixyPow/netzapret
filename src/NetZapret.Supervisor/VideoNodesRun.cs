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

        var addon = settings.Youtube is { Active: true } tune ? $" Автонастройка YouTube: {Zapret.YoutubeAddon.Describe(tune)}." : string.Empty;

        return (steps.Length == 0
            ? $"Пресет: {name}; рецепта узлов видео (малое окно по надобности) в нём нет."
            : $"Пресет: {name}; рецепт узлов видео есть: {string.Join(", ", steps)}.") + addon;
    }

    /// <summary>Работал ли рецепт узлов во время замера: в пресете (YT FIX) или надстройкой.</summary>
    public static bool RecipeOn(AppSettings settings) => settings.Youtube is { Nodes: true } || PresetHasRecipe(settings);

    /// <summary>Рецепт узлов — в самом пресете (YT FIX): надстройка его не трогает.</summary>
    public static bool PresetHasRecipe(AppSettings settings)
    {
        try
        {
            return settings.PresetName is { Length: > 0 } name
                && File.ReadLines(Path.Combine(Zapret.ZapretPaths.PresetDirectory, name + ".txt"))
                    .Any(line => line.TrimStart().StartsWith("--lua-desync=nz_first2", StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Автонастройка YouTube: замер узлов и решение по нему; <c>null</c> — мерить бессмысленно.
    /// </summary>
    public static async Task<(VideoNodesReport? Report, YoutubeDecision? Decision, string? Why)> TuneAsync(
        AppSettings settings,
        CancellationToken cancellationToken,
        string who = "окно")
    {
        var (report, why) = await MeasureAsync(settings, cancellationToken);

        if (report is null)
        {
            YoutubeTuneLog.Failed("замер не вышел: " + why, who);
            return (null, null, why);
        }

        if (!report.Desync)
        {
            YoutubeTuneLog.Measured(settings, report, null, who);
            return (report, null, "десинк не работает — настраивать нечего; запустите движки");
        }

        var decision = YoutubeTuner.Decide(report, RecipeOn(settings), PresetHasRecipe(settings));
        YoutubeTuneLog.Measured(settings, report, decision, who);
        return (report, decision, null);
    }

    /// <summary>Записывает настройку в файл настроек; <c>null</c> — снять, пресет как есть.</summary>
    public static AppSettings Save(YoutubeTune? tune, string? path = null, string who = "окно")
    {
        var file = path ?? AppSettings.DefaultPath;
        var before = AppSettings.Load(file);
        var settings = before with { Youtube = tune };
        settings.Save(file);

        if (path is null)
            YoutubeTuneLog.Saved(before.Youtube, tune, !Zapret.YoutubeAddon.SameLine(before.Youtube, tune, PresetHasRecipe(settings)), who);

        return settings;
    }

    /// <summary>На сколько соединений из 30 расходятся одинаковые прогоны (14.11 дома: 22–27).</summary>
    public const int Spread = 8;

    /// <summary>
    /// Было → стало после применения: стало ли хуже по TCP через обход.
    /// </summary>
    /// <remarks>
    /// По TCP, а не по QUIC: выброшенный нарочно QUIC в перезамере — 0, и это не поломка.
    /// Хуже — потеряно больше восьми соединений из тридцати. Порог — по разбросу: 14.11 дома
    /// одна и та же строка за пять минут дала 22, 27, 27 и 24 из 30, а прогон до и после
    /// «Применить», ничего в строке не менявшего, — 23 и 19; по прежнему порогу (4) такая
    /// разница ещё сошла бы за «не хуже», но читалась владельцем как «стало хуже».
    /// </remarks>
    public static (bool Worse, string Said) Compare(VideoNodesReport before, VideoNodesReport after)
    {
        int lost = before.Tcp - after.Tcp;
        bool worse = before.Tries == after.Tries && lost > Spread;
        int lagBefore = before.Measured.Sum(n => n.Lagged), lagAfter = after.Measured.Sum(n => n.Lagged);

        return (worse,
            $"TCP через обход: было {before.Tcp} из {before.Tries}, стало {after.Tcp} из {after.Tries}; "
            + $"QUIC: было {before.Quic} из {before.QuicTries}, стало {after.Quic} из {after.QuicTries}; "
            + $"ответов с малым окном: было {lagBefore}, стало {lagAfter}");
    }
}
