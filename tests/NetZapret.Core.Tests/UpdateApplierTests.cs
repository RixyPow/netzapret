using System.Diagnostics;
using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Подмена файлов окном вместо cmd — с копией прежних и откатом (владелец 12.10).
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-applier-{Guid.NewGuid():N}");

    private string Source => Path.Combine(_root, "Иван", "runtime", "update", "files", "NetZapret");
    private string Target => Path.Combine(_root, "Иван");
    private string Backup => Path.Combine(_root, "Иван", "runtime", "update", "backup");

    public UpdateApplierTests()
    {
        Write(Source, "a.txt", "новый");
        Write(Source, Path.Combine("engines", "b.txt"), "новый b");
        Write(Source, "NetZapret.exe", "новая программа");
        Write(Source, Path.Combine("config", "netzapret.json"), "{ \"из архива\": true }");

        Write(Target, "a.txt", "прежний");
        Write(Target, "NetZapret.exe", "прежняя программа");
        Write(Target, Path.Combine("config", "netzapret.json"), "{ \"мои настройки\": true }");

        UpdateApplier.Attempts = 2;
        UpdateApplier.Pause = TimeSpan.FromMilliseconds(50);
    }

    public void Dispose()
    {
        UpdateApplier.Attempts = 10;
        UpdateApplier.Pause = TimeSpan.FromSeconds(1);

        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void Write(string root, string file, string text)
    {
        var path = Path.Combine(root, file);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private string Read(string file) => File.ReadAllText(Path.Combine(Target, file));

    [Fact]
    public async Task FilesAreReplacedAndPersonalOnesKept()
    {
        var applier = new UpdateApplier(Source, Target, Backup);

        Assert.Equal(3, applier.Total);
        Assert.Null(await applier.CopyAsync(null, CancellationToken.None));

        Assert.Equal("новый", Read("a.txt"));
        Assert.Equal("новый b", Read(Path.Combine("engines", "b.txt")));
        Assert.Equal("новая программа", Read("NetZapret.exe"));
        Assert.Equal("{ \"мои настройки\": true }", Read(Path.Combine("config", "netzapret.json")));
        Assert.Equal("прежний", File.ReadAllText(Path.Combine(Backup, "a.txt")));
    }

    [Fact]
    public async Task RollBackReturnsTheOldVersion()
    {
        var applier = new UpdateApplier(Source, Target, Backup);
        await applier.CopyAsync(null, CancellationToken.None);

        Assert.True(applier.CanRollBack);
        Assert.Equal(0, applier.RollBack());

        Assert.Equal("прежний", Read("a.txt"));
        Assert.Equal("прежняя программа", Read("NetZapret.exe"));
        Assert.False(File.Exists(Path.Combine(Target, "engines", "b.txt")));
        Assert.Equal("{ \"мои настройки\": true }", Read(Path.Combine("config", "netzapret.json")));
    }

    /// <summary>
    /// Занятый файл — сбой с его именем и тем, кто держит; программа остаётся
    /// прежней (её очередь последняя); повтор продолжает, не испортив копию прежнего.
    /// </summary>
    [Fact]
    public async Task LockedFileNamesItsHolderAndRetryResumes()
    {
        var applier = new UpdateApplier(Source, Target, Backup);
        ApplyFailure? failure;

        using (new FileStream(Path.Combine(Target, "a.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            failure = await applier.CopyAsync(null, CancellationToken.None);

        Assert.NotNull(failure);
        Assert.Equal("a.txt", failure.File);
        Assert.Contains(failure.Lockers, l => l.ProcessId == Environment.ProcessId);
        Assert.Equal("прежняя программа", Read("NetZapret.exe"));
        Assert.Contains("ERROR ", File.ReadAllText(Path.Combine(Target, UpdateInstaller.LogFile)));

        Assert.Null(await applier.CopyAsync(null, CancellationToken.None));
        Assert.Equal("новый", Read("a.txt"));
        Assert.Equal("прежний", File.ReadAllText(Path.Combine(Backup, "a.txt")));

        applier.RollBack();
        Assert.Equal("прежний", Read("a.txt"));
    }

    [Fact]
    public async Task AnExitedProcessIsNotWaitedFor()
    {
        using var finished = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        finished.WaitForExit();

        Assert.True(await UpdateApplier.WaitForExitAsync(finished.Id, TimeSpan.FromSeconds(2), CancellationToken.None));
    }
}
