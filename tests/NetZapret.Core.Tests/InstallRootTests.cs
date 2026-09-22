using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Где программа считает себя установленной.
/// </summary>
/// <remarks>
/// Issue #3: повышенный запуск получает рабочим каталогом System32, а там
/// есть папка config. Прежняя проверка принимала её за свою, и окно просило
/// rules.yaml, собирая подписку в ноль серверов.
/// </remarks>
public sealed class InstallRootTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nz-root-" + Guid.NewGuid().ToString("N")[..8]);

    public InstallRootTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
            // Временный каталог мог быть занят — не беда проверки.
        }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string Install(params string[] parts)
    {
        var path = Dir(parts);
        Directory.CreateDirectory(Path.Combine(path, "config"));
        File.WriteAllText(Path.Combine(path, InstallRoot.Marker), "rules: []");
        return path;
    }

    [Fact]
    public void A_foreign_config_folder_is_not_taken_for_ours()
    {
        // Как в System32: папка config есть, правил в ней нет.
        var system32 = Dir("Windows", "System32");
        Directory.CreateDirectory(Path.Combine(system32, "config"));

        var install = Install("NetZapret");

        Assert.Equal(install, InstallRoot.Find(system32, install));
    }

    [Fact]
    public void In_the_working_copy_it_climbs_out_of_build()
    {
        var project = Install("netzapret");
        var build = Dir("netzapret", "build");

        Assert.Equal(project, InstallRoot.Find(build, build));
    }

    [Fact]
    public void Its_own_current_directory_is_kept()
    {
        // Запуск из своего каталога со своим набором правил — законный случай.
        var own = Install("mine");
        var program = Install("NetZapret");

        Assert.Equal(own, InstallRoot.Find(own, program));
    }

    [Fact]
    public void Nothing_found_is_said_so()
    {
        Assert.Null(InstallRoot.Find(Dir("a"), Dir("b")));
    }
}
