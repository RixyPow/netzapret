using System.Net;
using System.Net.Sockets;
using NetZapret.Proxy;
using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Замер через туннель (01.10): отказ входа проверки и неготовый туннель
/// называются своими словами, а не «сервер замера недоступен».
/// </summary>
public sealed class TunnelReadinessTests
{
    private static SupervisorState State(params ServiceState[] services) => new()
    {
        // Живой процесс — сам тест: надзор «жив».
        SupervisorProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.Now,
        Services = services,
    };

    private static ServiceState Tunnel(ServiceHealth health, DateTimeOffset started) => new()
    {
        Name = "sing-box",
        Health = health,
        StartedAt = started,
        HealthSince = started.AddSeconds(15),
    };

    [Fact]
    public void NothingRunningIsSaid()
    {
        Assert.Equal("Движки не запущены.", TunnelReadiness.Why(null, DateTimeOffset.Now));
        Assert.Equal("Туннель выключен.", TunnelReadiness.Why(State(), DateTimeOffset.Now));
    }

    /// <summary>Надзор видит «трафик не идёт» — так и сказать, с WARP это каждый запуск.</summary>
    [Fact]
    public void ADegradedTunnelIsNamed()
    {
        var why = TunnelReadiness.Why(State(Tunnel(ServiceHealth.Degraded, DateTimeOffset.Now.AddSeconds(-24))), DateTimeOffset.Now);

        Assert.NotNull(why);
        Assert.Contains("трафик через туннель не идёт", why);
        Assert.Contains("WARP", why);
    }

    [Fact]
    public void AFreshTunnelNeedsTime()
    {
        var now = DateTimeOffset.Now;

        Assert.Contains("поднят 20 с назад", TunnelReadiness.Why(State(Tunnel(ServiceHealth.Healthy, now.AddSeconds(-20))), now));
        Assert.Null(TunnelReadiness.Why(State(Tunnel(ServiceHealth.Healthy, now.AddMinutes(-2))), now));
    }

    /// <summary>
    /// Входа проверки нет — отказ на петле. До 0.10.3 так было у всех
    /// с выключенной «Проверкой прохода трафика».
    /// </summary>
    [Fact]
    public async Task ARefusedHealthInboundIsNotAServerProblem()
    {
        // Свободный порт: заняли и отпустили — на нём никто не слушает.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var result = await new SpeedTest(new SpeedTestOptions
        {
            Proxy = new WebProxy($"http://127.0.0.1:{port}"),
        }).RunAsync(null, CancellationToken.None);

        Assert.Equal("вход проверки движка не отвечает — перезапустите движки", result.Problem);
    }
}
