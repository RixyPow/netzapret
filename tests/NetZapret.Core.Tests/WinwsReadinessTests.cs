using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// winws2 готов, когда начал перехват, а не когда процесс жив (01.10): иначе
/// туннель, поднятый следом, открывал первые соединения мимо десинка.
/// </summary>
public sealed class WinwsReadinessTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 9, 38, 10, TimeSpan.FromHours(3));

    [Fact]
    public void ReadyOnceCaptureHasStarted()
    {
        Assert.False(WinwsService.Ready(alive: true, capturing: false, Start, Start.AddSeconds(2)));
        Assert.True(WinwsService.Ready(alive: true, capturing: true, Start, Start.AddSeconds(2)));
    }

    /// <summary>Строки о перехвате нет — живому процессу верят после срока.</summary>
    [Fact]
    public void WithoutTheLineAliveIsEnoughAfterAWhile()
    {
        Assert.True(WinwsService.Ready(alive: true, capturing: false, Start, Start + WinwsService.CaptureGrace));
    }

    [Fact]
    public void ADeadProcessIsNeverReady()
    {
        Assert.False(WinwsService.Ready(alive: false, capturing: true, Start, Start.AddMinutes(1)));
    }

    /// <summary>Строка, которой winws2 1.0.3 объявляет начатый перехват (его журнал).</summary>
    [Fact]
    public void TheLineIsWhatWinws2Prints()
    {
        Assert.Contains(WinwsService.CaptureStarted, "windivert initialized. capture is started.", StringComparison.Ordinal);
    }
}
