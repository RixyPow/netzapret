using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Нагрузка движков на «Главной» — в долях ядра (19.10): winws2 однопоточный,
/// и доля всей машины прятала, что он занят целиком.
/// </summary>
public sealed class EngineLoadTests
{
    [Fact]
    public void ShareIsProcessorTimeOverWallTime()
    {
        Assert.Equal(0.5, EngineLoad.Share(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), 3);
        Assert.Equal(0, EngineLoad.Share(TimeSpan.FromSeconds(1), TimeSpan.Zero));
    }

    [Fact]
    public void CoresAreShownAsAPercentOfOneCore()
    {
        // Точкой, как размеры рядом («20.5 МБ», SubscriptionUsage.Size).
        Assert.Equal("1.2 %", EngineLoad.Cores([new("winws2", 0.012, 0)]));
        Assert.Equal("25 %", EngineLoad.Cores([new("winws2", 0.2, 0), new("sing-box", 0.05, 0)]));

        // Первый замер сравнивать не с чем — честнее прочерк, чем ноль.
        Assert.Equal("—", EngineLoad.Cores([new("winws2", null, 0)]));
        Assert.Equal("—", EngineLoad.Cores([]));
    }

    [Fact]
    public void OnlyTheSupervisorsOwnProcessesAreMeasured()
    {
        var state = new SupervisorState
        {
            SupervisorProcessId = Environment.ProcessId,
            StartedAt = DateTimeOffset.Now,
            Services =
            [
                new ServiceState { Name = "winws2", Health = ServiceHealth.Healthy, ProcessId = Environment.ProcessId },

                // Номера нет — движок не поднят; его и нет в замере.
                new ServiceState { Name = "sing-box", Health = ServiceHealth.Stopped },
            ],
        };

        var load = new EngineLoad();

        var first = Assert.Single(load.Sample(state));
        Assert.Equal("winws2", first.Name);
        Assert.Null(first.Cores);
        Assert.True(first.Memory > 0);

        // Немного работы, чтобы процессорное время сдвинулось.
        var until = DateTime.UtcNow.AddMilliseconds(80);
        while (DateTime.UtcNow < until)
        {
        }

        var second = Assert.Single(load.Sample(state));
        Assert.NotNull(second.Cores);
        Assert.True(second.Cores >= 0);
    }
}
