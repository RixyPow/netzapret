using System.Text.RegularExpressions;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Своя копия файлов Zapret GUI (22.10): сборка берёт её из репозитория,
/// а не из установки, и снимается она только по слову владельца.
/// </summary>
public sealed class ZapretGuiCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-zapretgui-{Guid.NewGuid():N}");

    private string Install => Path.Combine(_root, "install");

    private string Copy => Path.Combine(_root, "copy");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Put(string root, string path, string content)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private void FakeInstall()
    {
        Put(Install, @"lists\ipset-all.txt", "1.1.1.0/24\r\n2.2.2.0/24\r\n");
        Put(Install, @"lists\base\claude.txt", "claude.ai\r\n");
        Put(Install, @"lists\user\autohostlist.txt", "my-own-site.example\r\n");
        Put(Install, @"lua\zapret-lib.lua", "-- lib\r\n");
        Put(Install, @"bin\stun.bin", "\0\u0001stun");
        Put(Install, @"windivert.filter\windivert.all.txt", "tcp");
        Put(Install, @"exe\Monkey64.sys", "\0driver");
        Put(Install, @"exe\WinDivert.dll", "\0dll");
        Put(Install, @"exe\winws.exe", "\0old engine");
        Put(Install, @"exe\stop.bat", "taskkill");
        Put(Install, @"exe\dnscrypt-proxy.exe", "\0dns");
        Put(Install, @"system\hosts_catalog.sqlite3", "\0catalog");
        Put(Install, @"system\fakes_catalog.sqlite3", "\0fakes");
        Put(Install, @"settings\owner.json", "{}");
    }

    [Fact]
    public void The_copy_takes_what_the_build_needs_and_nothing_of_the_owner()
    {
        FakeInstall();

        ZapretGuiCopy.Take(Install, Copy, new DateTime(2026, 10, 22, 1, 0, 0));

        Assert.Equal(
            [
                @"bin\stun.bin",
                @"exe\Monkey64.sys",
                @"exe\WinDivert.dll",
                @"lists\base\claude.txt",
                @"lists\ipset-all.txt",
                @"lua\zapret-lib.lua",
                @"system\hosts_catalog.sqlite3",
                @"windivert.filter\windivert.all.txt",
            ],
            ZapretGuiCopy.Managed(Copy));

        // Свои списки человека в Zapret GUI не уезжают никуда.
        Assert.False(Directory.Exists(Path.Combine(Copy, @"lists\user")));
        Assert.StartsWith("Zapret GUI ", ZapretGuiCopy.CopyVersion(Copy));
    }

    [Fact]
    public void The_copy_is_byte_for_byte()
    {
        // Концы CRLF и последняя строка без перевода — как у них, иначе
        // архив вёз бы не те файлы, что в установке.
        FakeInstall();

        ZapretGuiCopy.Take(Install, Copy, DateTime.Now);

        Assert.Equal(
            File.ReadAllBytes(Path.Combine(Install, @"lists\ipset-all.txt")),
            File.ReadAllBytes(Path.Combine(Copy, @"lists\ipset-all.txt")));
        Assert.Equal("tcp", File.ReadAllText(Path.Combine(Copy, @"windivert.filter\windivert.all.txt")));
    }

    [Fact]
    public void The_report_says_what_changed_before_anything_is_taken()
    {
        FakeInstall();
        ZapretGuiCopy.Take(Install, Copy, DateTime.Now);

        Put(Install, @"lists\ipset-all.txt", "1.1.1.0/24\r\n3.3.3.0/24\r\n4.4.4.0/24\r\n");
        Put(Install, @"lists\new.txt", "new.example\r\n");
        File.Delete(Path.Combine(Install, @"lua\zapret-lib.lua"));
        Put(Install, @"exe\Monkey64.sys", "\0driver v2");

        var diff = ZapretGuiCopy.Compare(Install, Copy);

        Assert.Equal([@"lists\new.txt"], diff.Added);
        Assert.Equal([@"lua\zapret-lib.lua"], diff.Removed);
        Assert.Equal(
            [
                new ZapretGuiChange(@"exe\Monkey64.sys", Binary: true, 0, 0),
                new ZapretGuiChange(@"lists\ipset-all.txt", Binary: false, LinesAdded: 2, LinesRemoved: 1),
            ],
            diff.Changed);

        // Сверка ничего не трогает: копия прежняя, пока её не сняли.
        Assert.True(File.Exists(Path.Combine(Copy, @"lua\zapret-lib.lua")));
        Assert.False(File.Exists(Path.Combine(Copy, @"lists\new.txt")));
    }

    [Fact]
    public void Taking_again_follows_the_install_both_ways()
    {
        FakeInstall();
        ZapretGuiCopy.Take(Install, Copy, DateTime.Now);

        File.Delete(Path.Combine(Install, @"lua\zapret-lib.lua"));
        Put(Install, @"lists\new.txt", "new.example\r\n");

        ZapretGuiCopy.Take(Install, Copy, DateTime.Now);

        Assert.True(ZapretGuiCopy.Compare(Install, Copy).Same);
        Assert.False(Directory.Exists(Path.Combine(Copy, "lua")));
    }

    private static string? Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;
        }

        return null;
    }

    /// <summary>
    /// Всё, что называют пресеты, — в репозитории: в копии Zapret GUI или
    /// среди своих списков и образцов (приставка nz-).
    /// </summary>
    /// <remarks>
    /// Пока файлы Zapret GUI брались из установки, такое видно было только
    /// вживую: winws2 не поднимался, не найдя списка или модуля. Теперь это
    /// ловит CI.
    /// </remarks>
    [Fact]
    public void Every_file_a_preset_names_is_in_the_repository()
    {
        var root = Root();
        if (root is null)
            return;

        var copy = Path.Combine(root, ZapretGuiCopy.Folder);
        var reference = new Regex(@"(?:^|[=,@])((?:lists|lua|bin|windivert\.filter)/[^\s,:]+)");
        var missing = new List<string>();

        foreach (var preset in Directory.EnumerateFiles(Path.Combine(root, "presets"), "*.txt"))
        {
            foreach (var line in File.ReadLines(preset).Where(l => !l.TrimStart().StartsWith('#')))
            {
                foreach (Match match in reference.Matches(line))
                {
                    var path = match.Groups[1].Value;
                    var name = Path.GetFileName(path);

                    var found = name.StartsWith("nz-", StringComparison.Ordinal)
                        ? File.Exists(Path.Combine(root, "config", path.StartsWith("bin/") ? "blobs" : "lists", name["nz-".Length..]))
                        : File.Exists(Path.Combine(copy, path.Replace('/', Path.DirectorySeparatorChar)));

                    if (!found)
                        missing.Add($"{Path.GetFileName(preset)}: {path}");
                }
            }
        }

        Assert.Empty(missing.Distinct());
    }
}
