using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Сценарий подмены файлов после выхода программы.
/// </summary>
/// <remarks>
/// Жалоба 06.10: чёрное окно «Update failed. The previous version is untouched.»
/// — и ни слова о том, какой файл не заменился. Вывод robocopy уходил в nul,
/// фраза о нетронутой версии была неправдой (robocopy меняет файл за файлом),
/// а программа после сбоя не открывалась вовсе.
/// </remarks>
public sealed class UpdateApplyScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-apply-{Guid.NewGuid():N}");

    public UpdateApplyScriptTests() => Directory.CreateDirectory(Path.Combine(_root, "runtime"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string[] Script()
    {
        Directory.CreateDirectory(UpdateInstaller.StagingDirectory);

        var path = UpdateInstaller.WriteApplyScript(
            new UpdatePlan { StagedAt = @"C:\nz\runtime\update\NetZapret-9.9.9", Files = 1, Kept = [] },
            @"C:\nz\",
            relaunch: "NetZapret.exe");

        return File.ReadAllLines(path);
    }

    [Fact]
    public void RobocopyWritesItsLogAndShowsIt()
    {
        var copy = Script().Single(l => l.StartsWith("robocopy", StringComparison.Ordinal));

        Assert.Contains("/UNILOG:\"%LOG%\"", copy);
        Assert.Contains("/TEE", copy);
        Assert.DoesNotContain(">nul", copy);
        Assert.Contains("/R:10", copy);
        Assert.Contains("set \"LOG=%TARGET%\\runtime\\update.log\"", Script());
    }

    [Fact]
    public void FailureTellsTheTruthAndStartsTheProgramAgain()
    {
        var lines = Script();
        var failed = Array.IndexOf(lines, ":failed");

        Assert.True(failed > 0);
        Assert.DoesNotContain(lines, l => l.Contains("untouched", StringComparison.OrdinalIgnoreCase));

        var tail = lines.Skip(failed).ToList();
        Assert.Contains(tail, l => l.StartsWith("start \"\" \"%TARGET%\\%LAUNCH%\"", StringComparison.Ordinal));
        Assert.Contains(tail, l => l.Contains("%LOG%", StringComparison.Ordinal));

        // Удачный путь до метки не доходит.
        Assert.Contains("exit /b 0", lines.Take(failed));
    }

    [Fact]
    public void LeftoversFromTheFolderAreStoppedBeforeCopying()
    {
        var lines = Script().ToList();
        int copy = lines.FindIndex(l => l.StartsWith("robocopy", StringComparison.Ordinal));
        int stop = lines.FindIndex(l => l.Contains("Stop-Process", StringComparison.Ordinal));

        Assert.InRange(stop, 0, copy - 1);
        Assert.Contains("$env:TARGET", lines[stop]);
        Assert.Contains(lines.Take(copy), l => l == "sc stop Monkey >nul 2>&1");
    }

    [Fact]
    public void TheScriptIsAscii()
    {
        // cmd.exe читает батник в кодовой странице OEM: кириллица развалится на команды.
        Assert.All(Script(), line => Assert.All(line, c => Assert.True(c < 128, $"не ASCII: {line}")));
    }

    [Fact]
    public void FailedFilesAreReadFromTheLog()
    {
        File.WriteAllLines(Path.Combine(_root, UpdateInstaller.LogFile),
        [
            "   ROBOCOPY     ::     Robust File Copy for Windows",
            "2026/10/07 12:00:01 ERROR 32 (0x00000020) Copying File C:\\nz\\runtime\\update\\x\\engines\\zapret\\exe\\winws2.exe",
            "The process cannot access the file because it is being used by another process.",
            "2026/10/07 12:00:03 ERROR 32 (0x00000020) Copying File C:\\nz\\runtime\\update\\x\\engines\\zapret\\exe\\winws2.exe",
        ]);

        var failed = UpdateInstaller.FailedFiles(_root);

        Assert.Single(failed);
        Assert.Contains("winws2.exe", failed[0]);
        Assert.Empty(UpdateInstaller.FailedFiles(Path.Combine(_root, "нет такой")));
    }
}
