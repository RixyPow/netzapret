using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Очередь запусков и остановок (отчёт reaass, 01.10): второй «Запустить»
/// посреди подъёма не поднимает второй супервизор, «Выход» отменяет запуск.
/// </summary>
public sealed class EngineGateTests
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task OneAtATime()
    {
        var gate = new EngineGate();
        var first = await gate.StartAsync(CancellationToken.None);

        var second = gate.StartAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();
        using var turn = await second.WaitAsync(Soon);
    }

    /// <summary>Второй «Запустить», пока первый ждал подписку, — лишний.</summary>
    [Fact]
    public async Task AStartBehindASuccessfulStartIsSuperseded()
    {
        var gate = new EngineGate();
        var first = await gate.StartAsync(CancellationToken.None);
        var second = gate.StartAsync(CancellationToken.None);

        gate.Started();
        first.Dispose();

        using var turn = await second.WaitAsync(Soon);
        Assert.True(turn.Superseded);
    }

    /// <summary>Первый не поднял ничего — второй не лишний.</summary>
    [Fact]
    public async Task AStartBehindAFailedStartRuns()
    {
        var gate = new EngineGate();
        var first = await gate.StartAsync(CancellationToken.None);
        var second = gate.StartAsync(CancellationToken.None);

        first.Dispose();

        using var turn = await second.WaitAsync(Soon);
        Assert.False(turn.Superseded);
    }

    /// <summary>Перезапуск несёт новые настройки — лишним не бывает.</summary>
    [Fact]
    public async Task ARestartIsNeverSuperseded()
    {
        var gate = new EngineGate();
        var first = await gate.StartAsync(CancellationToken.None);
        var restart = gate.RestartAsync(CancellationToken.None);

        gate.Started();
        first.Dispose();

        using var turn = await restart.WaitAsync(Soon);
        Assert.False(turn.Superseded);
    }

    /// <summary>
    /// «Выход» посреди запуска: запуск отменяется, остановка ждёт, пока он уйдёт,
    /// а запуск, попросивший очереди после остановки, идёт как обычно.
    /// </summary>
    [Fact]
    public async Task AStopCancelsEarlierStartsOnly()
    {
        var gate = new EngineGate();
        var running = await gate.StartAsync(CancellationToken.None);
        var queued = gate.StartAsync(CancellationToken.None);

        var stop = gate.StopAsync(CancellationToken.None);
        var later = gate.StartAsync(CancellationToken.None);

        Assert.True(running.Token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);

        running.Dispose();

        using (var turn = await queued.WaitAsync(Soon))
            Assert.True(turn.Token.IsCancellationRequested);

        using (var turn = await stop.WaitAsync(Soon))
            Assert.False(turn.Token.IsCancellationRequested);

        using (var turn = await later.WaitAsync(Soon))
            Assert.False(turn.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ReleasingTwiceDoesNotOpenTheGateTwice()
    {
        var gate = new EngineGate();
        var first = await gate.StartAsync(CancellationToken.None);

        first.Dispose();
        first.Dispose();

        using var second = await gate.StartAsync(CancellationToken.None);
        var third = gate.StartAsync(CancellationToken.None);

        await Task.Delay(50);
        Assert.False(third.IsCompleted);
    }
}

/// <summary>Замок супервизора: второй не берёт, номер держателя виден, умерший отпускает.</summary>
public sealed class SupervisorLockTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"nz-lock-{Guid.NewGuid():N}", "supervisor.lock");

    public void Dispose()
    {
        var directory = Path.GetDirectoryName(_path)!;

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void HeldOnceAndHolderIsKnown()
    {
        Assert.Null(SupervisorLock.Holder(_path));

        using (var held = SupervisorLock.TryAcquire(_path))
        {
            Assert.NotNull(held);
            Assert.Null(SupervisorLock.TryAcquire(_path));
            Assert.Equal(Environment.ProcessId, SupervisorLock.Holder(_path));
        }

        // Отпущен — номер в файле остался, но держателя нет.
        Assert.Null(SupervisorLock.Holder(_path));

        using var again = SupervisorLock.TryAcquire(_path);
        Assert.NotNull(again);
    }

    /// <summary>Свой процесс и чужой номер «нашим» не считаются — их не снимают.</summary>
    [Fact]
    public void OnlyOtherCopiesOfUsAreOurs()
    {
        Assert.False(SupervisorLock.IsOurs(Environment.ProcessId));
        Assert.False(SupervisorLock.IsOurs(0));
        Assert.False(SupervisorLock.IsOurs(int.MaxValue));
    }
}
