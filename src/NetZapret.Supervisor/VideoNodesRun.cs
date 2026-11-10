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

        return (report, null);
    }
}
