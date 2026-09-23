using NetZapret.Supervisor;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Файл состояния, занятый читающим.
/// </summary>
/// <remarks>
/// 19.09 супервизор упал с «Access denied» на замене файла состояния:
/// окно читало его в тот же миг. Ловилось одно IOException, а Windows
/// отвечает на такое UnauthorizedAccessException, — и движки ушли вместе
/// с супервизором.
/// </remarks>
public sealed class SupervisorStateBusyTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"nz-state-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + ".tmp" })
        {
            if (File.Exists(file))
                File.Delete(file);
        }
    }

    private static SupervisorState State() => new()
    {
        SupervisorProcessId = Environment.ProcessId,
        StartedAt = DateTimeOffset.Now,
        Services = [],
    };

    [Fact]
    public void Held_for_long_it_fails_as_busy()
    {
        // Держат дольше, чем ждёт повтор. Замена обязана провалиться
        // именно тем, что вызывающий ловит, — а не чем-то, что уронит его.
        State().Save(_path);

        using var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var thrown = Record.Exception(() => State().Save(_path));

        Assert.NotNull(thrown);
        Assert.True(SupervisorState.IsBusy(thrown));
    }

    [Fact]
    public void A_short_read_is_outwaited()
    {
        // Окно читает миллисекунды. Повтор обязан это пережить,
        // а не отдать отказ наверх.
        State().Save(_path);

        var reader = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Отпускает свой поток, а не пул. Прежде здесь стояло
        // Task.Delay(30).ContinueWith: под нагрузкой всего прогона пул
        // был занят, продолжение запаздывало дольше, чем повтор ждёт
        // (200 мс на все попытки), и тест падал — 23.09 дважды за день,
        // при исправном Save.
        var release = new Thread(() =>
        {
            Thread.Sleep(30);
            reader.Dispose();
        });

        release.Start();

        State().Save(_path);
        release.Join();
    }

    [Fact]
    public void What_was_saved_reads_back()
    {
        State().Save(_path);

        Assert.Equal(Environment.ProcessId, SupervisorState.Load(_path)?.SupervisorProcessId);
    }
}
