using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Строка «Сейчас» на вкладке DNS (обсуждение №55): куда имена идут на деле,
/// а не что выбрано.
/// </summary>
public sealed class DnsNowTests
{
    private static readonly AppSettings Quad9 = new() { DnsServer = "9.9.9.10", DnsVia = DnsRoute.Auto };

    /// <summary>Состояние живого надзора — с номером этого процесса, чтобы «жив» было правдой.</summary>
    private static SupervisorState Alive(params ServiceState[] services) => new()
    {
        SupervisorProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.Now,
        Services = services,
    };

    private static ServiceState Engine(string name = "sing-box", string? notice = null) =>
        new() { Name = name, Health = ServiceHealth.Healthy, DnsNotice = notice };

    [Fact]
    public void WithoutEnginesWindowsAnswersItself() =>
        Assert.Contains("движки не запущены", DnsNow.Describe(Quad9, null, null));

    /// <summary>«Десинк» без движка ради DNS: выбранный резолвер не спрашивается вовсе.</summary>
    [Fact]
    public void DesyncWithoutTheDnsEngineSaysTheResolverIsUnused()
    {
        var said = DnsNow.Describe(Quad9, Alive(new ServiceState { Name = "winws2", Health = ServiceHealth.Healthy }), null);

        Assert.Contains("сама Windows", said);
        Assert.Contains("через туннель", said);
    }

    [Fact]
    public void AHealthyEngineNamesTheResolverAndThePath()
    {
        Assert.Equal("Сейчас: Quad9 без фильтра (9.9.9.10), напрямую.", DnsNow.Describe(Quad9, Alive(Engine()), "9.9.9.10"));
        Assert.EndsWith("через туннель.", DnsNow.Describe(Quad9 with { DnsVia = DnsRoute.Tunnel }, Alive(Engine()), "9.9.9.10"));
        Assert.EndsWith("движком, напрямую.", DnsNow.Describe(Quad9, Alive(Engine(NetZapret.Proxy.DnsEngine.ServiceName)), "9.9.9.10"));
    }

    /// <summary>Надзор ушёл на ступень — строка говорит его словами.</summary>
    [Fact]
    public void TheSupervisorsStepIsShown()
    {
        var notice = "резолвер Quad9 без фильтра (9.9.9.10) не отвечает — имена спрашиваются у запасного Google (8.8.4.4)";

        Assert.Equal("Сейчас: " + notice + ".", DnsNow.Describe(Quad9, Alive(Engine(notice: notice)), "9.9.9.10"));
    }

    /// <summary>Выбран другой, а движок ещё со старым — назван тот, кого спрашивают на деле.</summary>
    [Fact]
    public void ANotYetAppliedChoiceIsNotPassedOffAsCurrent()
    {
        var said = DnsNow.Describe(Quad9, Alive(Engine()), "8.8.4.4");

        Assert.StartsWith("Сейчас: Google (8.8.4.4)", said);
        Assert.Contains("после перезапуска", said);
    }
}
