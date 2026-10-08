using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Папка над <c>--writable</c> пресета создаётся до запуска winws2 (08.10).
/// </summary>
/// <remarks>
/// Пресет Zapret GUI «white sni (circular)» пишет <c>--writable=user/winws2</c>.
/// Саму папку winws2 создаёт, а родитель обязан быть: у нас <c>user</c> не было,
/// и winws2 падал на старте — «bad file 'user/winws2'», код 1.
/// </remarks>
public sealed class WinwsWritableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-writable-{Guid.NewGuid():N}");

    public WinwsWritableTests() => Directory.CreateDirectory(_root);

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

    [Fact]
    public void TheParentOfTheWritableDirIsMade()
    {
        WinwsCommandLine.PrepareWritable(["--lua-init=@lua/zapret-lib.lua", "--writable=user/winws2"], _root);

        Assert.True(Directory.Exists(Path.Combine(_root, "user")), "нет папки user");

        // Саму папку создаёт winws2 — её делать незачем.
        Assert.False(Directory.Exists(Path.Combine(_root, "user", "winws2")));
    }

    [Fact]
    public void WithoutAPathNothingIsMade()
    {
        WinwsCommandLine.PrepareWritable(["--writable", "--writable="], _root);

        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }
}
