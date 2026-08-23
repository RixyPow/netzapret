using NetZapret.Supervisor;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

public sealed class SupervisorStateTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"netzapret-state-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private SupervisorState Sample(int pid = 4242) => new()
    {
        SupervisorProcessId = pid,
        StartedAt = DateTimeOffset.Now.AddMinutes(-5),
        Services =
        [
            new ServiceState
            {
                Name = "sing-box",
                Health = ServiceHealth.Healthy,
                ProcessId = 1234,
                StartedAt = DateTimeOffset.Now.AddMinutes(-5),
                RestartCount = 2,
            },
        ],
    };

    [Fact]
    public void StateSurvivesRoundTrip()
    {
        Sample().Save(_path);
        var loaded = SupervisorState.Load(_path);

        Assert.NotNull(loaded);
        Assert.Equal(4242, loaded!.SupervisorProcessId);
        Assert.Single(loaded.Services);
        Assert.Equal(ServiceHealth.Healthy, loaded.Services[0].Health);
        Assert.Equal(2, loaded.Services[0].RestartCount);
    }

    [Fact]
    public void UpdatedAtIsStampedOnSave()
    {
        var before = DateTimeOffset.Now.AddSeconds(-1);
        Sample().Save(_path);

        Assert.True(SupervisorState.Load(_path)!.UpdatedAt > before);
    }

    [Fact]
    public void MissingFileLoadsAsNull() =>
        Assert.Null(SupervisorState.Load(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json")));

    [Fact]
    public void CorruptedFileLoadsAsNullInsteadOfThrowing()
    {
        // Файл могли обрезать при жёстком завершении: status обязан пережить это.
        File.WriteAllText(_path, "{ это не json");

        Assert.Null(SupervisorState.Load(_path));
    }

    [Fact]
    public void DeadSupervisorIsDetected()
    {
        // PID, которого заведомо нет: состояние от убитого процесса не должно
        // выглядеть как работающий супервизор.
        var state = Sample(pid: 999_999);
        state.Save(_path);

        Assert.False(SupervisorState.Load(_path)!.IsSupervisorAlive());
    }

    [Fact]
    public void OwnProcessIsSeenAsAlive()
    {
        var state = Sample(pid: Environment.ProcessId);
        state.Save(_path);

        Assert.True(SupervisorState.Load(_path)!.IsSupervisorAlive());
    }

    [Fact]
    public void ClearRemovesTheFile()
    {
        Sample().Save(_path);
        SupervisorState.Clear(_path);

        Assert.False(File.Exists(_path));
    }
}

public sealed class WinwsCommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-winws-{Guid.NewGuid():N}");

    public WinwsCommandLineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "presets"));
        Directory.CreateDirectory(Path.Combine(_root, "lists"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private const string Preset = """
        # Preset: Test
        --wf-tcp-out=80,443
        --blob=tls_google:@bin/x.bin

        --new

        --name=YouTube
        --filter-tcp=443
        --hostlist=lists/youtube.txt
        --lua-desync=multidisorder:pos=1

        --new

        --name=Discord
        --ipset=lists/ipset-discord.txt
        --unknown-future-key=value
        --lua-desync=pass
        """;

    private ZapretPreset Load()
    {
        var path = Path.Combine(_root, "presets", "test.txt");
        File.WriteAllText(path, Preset);
        return new PresetReader().Load(path);
    }

    [Fact]
    public void GlobalArgumentsComeFirstThenSectionsSeparatedByNew()
    {
        var arguments = WinwsCommandLine.Build(Load()).ToList();

        Assert.Equal("--wf-tcp-out=80,443", arguments[0]);
        Assert.Equal(2, arguments.Count(a => a == "--new"));
        Assert.True(
            arguments.IndexOf("--wf-tcp-out=80,443") < arguments.IndexOf("--new"),
            "глобальные ключи обязаны идти до первой секции");
    }

    [Fact]
    public void UnknownKeysArePassedThroughUntouched()
    {
        // Модель разбора не знает про --unknown-future-key, но winws2 может знать.
        var arguments = WinwsCommandLine.Build(Load());

        Assert.Contains("--unknown-future-key=value", arguments);
    }

    [Fact]
    public void PassThroughSectionsAreStillLaunched()
    {
        // pass — часть логики пресета: она сообщает winws2 «не трогать».
        // Выбросить такую секцию значит изменить поведение.
        var arguments = WinwsCommandLine.Build(Load());

        Assert.Contains("--lua-desync=pass", arguments);
    }

    [Fact]
    public void MissingListFilesAreReported()
    {
        File.WriteAllText(Path.Combine(_root, "lists", "youtube.txt"), "youtube.com");

        var missing = WinwsCommandLine.FindMissingFiles(Load(), _root);

        Assert.Single(missing);
        Assert.Equal("lists/ipset-discord.txt", missing[0]);
    }

    [Fact]
    public void NothingIsReportedWhenAllFilesExist()
    {
        File.WriteAllText(Path.Combine(_root, "lists", "youtube.txt"), "youtube.com");
        File.WriteAllText(Path.Combine(_root, "lists", "ipset-discord.txt"), "1.2.3.0/24");

        Assert.Empty(WinwsCommandLine.FindMissingFiles(Load(), _root));
    }
}
