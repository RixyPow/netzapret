using NetZapret.Core;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Закреплённый ярлык программы получает значок темы (21.10): только ярлык
/// на наш exe, без лишних записей, и со сменой темы — новый файл значка.
/// </summary>
public sealed class PinnedIconTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-pin-{Guid.NewGuid():N}");

    public PinnedIconTests() => Directory.CreateDirectory(Path.Combine(_root, "pinned"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void OnlyOurPinGetsTheThemeIcon()
    {
        var pinned = Path.Combine(_root, "pinned");
        var icons = Path.Combine(_root, "runtime");
        var exe = Path.Combine(_root, "NetZapret.exe");
        var other = Path.Combine(_root, "Other.exe");

        PinnedIcon.CreateShortcut(Path.Combine(pinned, "NetZapret.lnk"), exe);
        PinnedIcon.CreateShortcut(Path.Combine(pinned, "Other.lnk"), other);

        Assert.Equal(1, PinnedIcon.Apply([1, 2, 3], exe, icons, pinned, notify: false));

        var (icon, index) = PinnedIcon.IconOf(Path.Combine(pinned, "NetZapret.lnk"));
        Assert.Equal(0, index);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(icon));
        Assert.Equal(string.Empty, PinnedIcon.IconOf(Path.Combine(pinned, "Other.lnk")).Path);

        // Тот же значок — ярлык не переписывается.
        Assert.Equal(0, PinnedIcon.Apply([1, 2, 3], exe, icons, pinned, notify: false));

        // Другая тема — новый файл, прежний убран.
        Assert.Equal(1, PinnedIcon.Apply([4, 5], exe, icons, pinned, notify: false));
        var next = PinnedIcon.IconOf(Path.Combine(pinned, "NetZapret.lnk")).Path;
        Assert.NotEqual(icon, next);
        Assert.False(File.Exists(icon));
        Assert.Single(Directory.GetFiles(icons, "*.ico"));
    }

    [Fact]
    public void NotPinnedMeansNoIconFile()
    {
        var icons = Path.Combine(_root, "runtime");

        Assert.Equal(0, PinnedIcon.Apply([1], Path.Combine(_root, "NetZapret.exe"), icons, Path.Combine(_root, "pinned"), notify: false));
        Assert.False(Directory.Exists(icons));
    }
}
