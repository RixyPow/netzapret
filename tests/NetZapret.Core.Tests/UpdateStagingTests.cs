using System.IO.Compression;
using NetZapret.Core.Updates;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Отстойник обновления переживает занятые файлы (жалоба 03.10: «The process cannot
/// access the file 'release.zip' because it is being used by another process»).
/// </summary>
public sealed class UpdateStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-staging-{Guid.NewGuid():N}");

    public UpdateStagingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Archive()
    {
        var source = Path.Combine(_root, "source", "NetZapret");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "NetZapret.exe"), "новая версия");

        var path = Path.Combine(_root, "staging", $"release-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ZipFile.CreateFromDirectory(Path.GetDirectoryName(source)!, path);

        return path;
    }

    /// <summary>Занятый остаток прошлой попытки не мешает начать новую.</summary>
    [Fact]
    public void A_held_leftover_does_not_stop_the_next_attempt()
    {
        var staging = Path.Combine(_root, "update");
        Directory.CreateDirectory(Path.Combine(staging, "files"));
        File.WriteAllText(Path.Combine(staging, "files", "old.txt"), "прошлая попытка");

        var leftover = Path.Combine(staging, "release.zip");
        File.WriteAllText(leftover, "держит антивирус");

        using (new FileStream(leftover, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var prepared = UpdateInstaller.PrepareStaging(staging);

            Assert.Equal(Path.GetFullPath(staging), prepared);
            Assert.False(Directory.Exists(Path.Combine(staging, "files")));
        }
    }

    /// <summary>Архив, который пару секунд держит чужой процесс, распаковывается, когда его отпустят.</summary>
    [Fact]
    public async Task An_archive_held_for_a_moment_is_unpacked_when_released()
    {
        var archive = Archive();
        var held = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None);

        var release = Task.Run(async () =>
        {
            await Task.Delay(1500);
            await held.DisposeAsync();
        });

        var root = await UpdateInstaller.UnpackAsync(archive, Path.GetDirectoryName(archive)!, CancellationToken.None);
        await release;

        Assert.Equal("новая версия", File.ReadAllText(Path.Combine(root, "NetZapret.exe")));
        Assert.False(File.Exists(archive));
    }

    /// <summary>Архив, который не дают удалить, остаётся лежать — обновление не встаёт.</summary>
    [Fact]
    public async Task An_archive_that_cannot_be_deleted_is_left_behind()
    {
        var archive = Archive();

        // Так держит антивирус: читать даёт, удалить — нет.
        using var reader = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read);

        var root = await UpdateInstaller.UnpackAsync(archive, Path.GetDirectoryName(archive)!, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(root, "NetZapret.exe")));
        Assert.True(File.Exists(archive));
    }

    /// <summary>Битый архив — сразу ошибка, без ожидания: его никто не держит.</summary>
    [Fact]
    public async Task A_broken_archive_fails_at_once()
    {
        var archive = Path.Combine(_root, "broken.zip");
        File.WriteAllText(archive, "это не zip");

        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<InvalidDataException>(
            () => UpdateInstaller.UnpackAsync(archive, _root, CancellationToken.None));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
