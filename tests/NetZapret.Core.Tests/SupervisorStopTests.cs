using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Просьба остановиться доходит до супервизора, а без слушателя — честное «нет».
/// </summary>
public sealed class SupervisorStopTests
{
    [Fact]
    public void An_asked_supervisor_hears_it_and_a_silent_one_says_no()
    {
        var stop = SupervisorStop.Listen();

        Assert.False(stop.IsCancellationRequested);
        Assert.True(SupervisorStop.Ask(Environment.ProcessId));
        Assert.True(stop.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));

        // Такого процесса нет — и события тоже: окно тогда гасит по-старому.
        Assert.False(SupervisorStop.Ask(int.MaxValue - 7));
    }
}
