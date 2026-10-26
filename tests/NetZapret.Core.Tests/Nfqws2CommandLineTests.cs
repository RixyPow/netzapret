using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// nfqws2 на телефоне с root — та же строка, что у winws2, без ключей Windows
/// (<c>nfqws2.md</c> §3.8).
/// </summary>
/// <remarks>
/// Ключи <c>--wf-*</c> nfqws2 не знает: с любым из них он печатает справку
/// и выходит. Всё остальное обязано дойти до него тем же, что на Windows, —
/// иначе правка пресета или щита на Windows не доехала бы до телефона.
/// </remarks>
public sealed class Nfqws2CommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-nfqws2-{Guid.NewGuid():N}");

    public Nfqws2CommandLineTests() => Directory.CreateDirectory(_root);

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

    private static readonly Nfqws2Host Host = new()
    {
        ZapretRoot = "/data/user/0/org.netzapret/files/zapret",
        Executable = "/data/app/~~x/org.netzapret-1/lib/arm64/libnfqws2.so",
        Uid = 10234,
        PidFile = "/data/user/0/org.netzapret/files/runtime/nfqws2.pid",
        LogFile = "/data/user/0/org.netzapret/files/runtime/nfqws2.log",
    };

    private static string? Repository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetZapret.sln")))
                return directory.FullName;
        }

        return null;
    }

    private static IEnumerable<ZapretPreset> ShippedPresets()
    {
        if (Repository() is not { } root)
            yield break;

        foreach (var file in Directory.GetFiles(Path.Combine(root, "presets"), "*.txt").OrderBy(f => f))
            yield return new PresetReader().Load(file);
    }

    private static ZapretPreset? Shipped(string name) =>
        ShippedPresets().FirstOrDefault(p => Path.GetFileNameWithoutExtension(p.FilePath) == name);

    /// <summary>
    /// На всех пресетах, с game filter и без, на каждой ширине перехвата:
    /// ни одного ключа Windows, а остальное — строка Windows ключ в ключ.
    /// </summary>
    [Fact]
    public void EveryPresetIsTheWindowsLineWithoutWindowsKeys()
    {
        int checkedLines = 0;

        foreach (var preset in ShippedPresets())
        {
            foreach (var game in new[] { false, true })
            {
                foreach (var width in Enum.GetValues<CaptureWidth>())
                {
                    var launch = Nfqws2CommandLine.Build(preset, Host, gameFilter: game, capture: width);
                    var globals = Nfqws2CommandLine.LinuxGlobals(Host);

                    Assert.DoesNotContain(launch.Arguments, Nfqws2CommandLine.IsWindowsOnly);
                    Assert.Equal(globals, launch.Arguments.Take(globals.Count));

                    var windows = WinwsCommandLine.Build(preset, gameFilter: game, capture: width)
                        .Where(a => !Nfqws2CommandLine.IsWindowsOnly(a));

                    Assert.Equal(windows, launch.Arguments.Skip(globals.Count));
                    checkedLines++;
                }
            }
        }

        Assert.True(Repository() is null || checkedLines >= 15 * 2 * 3);
    }

    /// <summary>Щит, свои рецепты и копии исключений — те же, что на Windows.</summary>
    [Fact]
    public void ShieldAndOwnRecipesAreTheWindowsOnes()
    {
        if (Shipped("Universal V12 Lite") is not { } preset)
            return;

        var exclude = Path.Combine(_root, "desync-exclude.txt");
        File.WriteAllLines(exclude, ["riotgames.com", "playvalorant.com"]);

        var keep = Path.Combine(_root, "desync-keep.txt");
        File.WriteAllLines(keep, ["sub.riotgames.com"]);

        var own = new List<OwnDesyncProfile>
        {
            new()
            {
                Name = "Свой & (скобки)",
                Steps = ["multisplit:pos=1"],
                HostListPath = Path.Combine(_root, "own.txt"),
                Ports = "443",
            },
        };

        var launch = Nfqws2CommandLine.Build(preset, Host, exclude, own, keepList: keep);
        var windows = WinwsCommandLine.Build(preset, exclude, own, keepList: keep)
            .Where(a => !Nfqws2CommandLine.IsWindowsOnly(a));

        Assert.Equal(windows, launch.Arguments.Skip(Nfqws2CommandLine.LinuxGlobals(Host).Count));
        Assert.Contains("--name=NetZapret: не трогать", launch.Arguments);
        Assert.Contains("--name=NetZapret: Свой & (скобки)", launch.Arguments);
    }

    /// <summary>План V12 Lite — как в разборе (<c>nfqws2.md</c> §3.7).</summary>
    [Fact]
    public void V12LitePlan()
    {
        if (Shipped("Universal V12 Lite") is not { } preset)
            return;

        var plan = Nfqws2CommandLine.Build(preset, Host).Capture;

        Assert.Equal([(80, 80), (443, 65535)], plan.TcpOut);
        Assert.Equal([(443, 443), (6568, 6568), (19294, 19344), (50000, 51000)], plan.UdpOut);
        Assert.Empty(plan.TcpIn);
        Assert.Empty(plan.UdpIn);
        Assert.True(plan.RawParts.SetEquals([RawPart.Stun, RawPart.DiscordMedia, RawPart.WireGuard]));
        Assert.Equal(new CaptureBudget(22, 20, 10, 10), plan.Budget);
        Assert.Contains("Discord UDP (обычно не нужно)", plan.BeyondBudget);
        Assert.True(plan.FilterLan && plan.FilterLoopback && plan.Ipv4 && plan.Ipv6);
        Assert.Empty(plan.Dropped);
    }

    [Fact]
    public void GameFilterWidensTheCapture()
    {
        if (Shipped("Universal V12 Lite") is not { } preset)
            return;

        var plan = Nfqws2CommandLine.Build(preset, Host, gameFilter: true).Capture;

        Assert.Contains(plan.TcpOut, r => r.First <= 1024 && r.Last == 65535);
        Assert.Contains(plan.UdpOut, r => r.First <= 1024 && r.Last == 65535);
    }

    /// <summary>«white sni» перехватывает и входящее; окна у него длинные.</summary>
    [Fact]
    public void WhiteSniBudget()
    {
        if (Shipped("white sni (circular)") is not { } preset)
            return;

        var plan = Nfqws2CommandLine.Build(preset, Host).Capture;

        Assert.Equal(62, plan.Budget.TcpOut);
        Assert.Equal(54, plan.Budget.TcpIn);
        Assert.NotEmpty(plan.TcpIn);
    }

    [Fact]
    public void UnknownRawPartIsDroppedNotPassed()
    {
        var (arguments, plan) = Nfqws2CommandLine.Translate(
        [
            "--wf-tcp-out=443",
            "--wf-raw-part=@windivert.filter/windivert_part.stun.txt",
            "--wf-raw-part=@windivert.filter/свой.txt",
            "--wf-raw-filter=(ip.DstAddr==1.2.3.4 ? false : true)",
            "--wf-save=x.txt",
            "--ssid-filter=home",
            "--lua-init=@lua/zapret-lib.lua",
            "--new",
            "--filter-ssid=home",
            "--lua-desync=pass",
        ]);

        Assert.Equal(["--lua-init=@lua/zapret-lib.lua", "--new", "--filter-ssid=home", "--lua-desync=pass"], arguments);
        Assert.True(plan.RawParts.SetEquals([RawPart.Stun]));
        Assert.Contains(plan.Dropped, d => d.Contains("свой.txt", StringComparison.Ordinal));
        Assert.Contains(plan.Dropped, d => d.Contains("--wf-raw-filter", StringComparison.Ordinal));
        Assert.Contains(plan.Dropped, d => d.Contains("--ssid-filter", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Dropped, d => d.Contains("--wf-save", StringComparison.Ordinal));
    }

    [Fact]
    public void PortsMergeAndNegate()
    {
        var (_, plan) = Nfqws2CommandLine.Translate(["--wf-tcp-out=443,80,444-500,0", "--wf-udp-out=~1-1023", "--wf-l3=ipv4"]);

        Assert.Equal([(80, 80), (443, 500)], plan.TcpOut);
        Assert.Equal([(1024, 65535)], plan.UdpOut);
        Assert.True(plan.Ipv4);
        Assert.False(plan.Ipv6);

        // Без портов nfqws2 перехватывать нечего — порты по умолчанию и запись в журнал.
        var (_, none) = Nfqws2CommandLine.Translate(["--lua-desync=pass"]);
        Assert.Equal([(80, 80), (443, 443)], none.TcpOut);
        Assert.Equal([(443, 443)], none.UdpOut);
        Assert.NotEmpty(none.Dropped);
    }

    [Fact]
    public void LinuxGlobalsComeFirstOnce()
    {
        var globals = Nfqws2CommandLine.LinuxGlobals(Host);

        Assert.Equal(
            [
                "--qnum=1717",
                "--fwmark=0x40000000",
                "--uid=10234:10234",
                "--pidfile=/data/user/0/org.netzapret/files/runtime/nfqws2.pid",
                "--bind-fix4",
                "--bind-fix6",
            ],
            globals);

        if (Shipped("Universal V12 Lite") is not { } preset)
            return;

        var arguments = Nfqws2CommandLine.Build(preset, Host).Arguments;

        Assert.Single(arguments, a => a.StartsWith("--qnum=", StringComparison.Ordinal));
        Assert.Single(arguments, a => a.StartsWith("--uid=", StringComparison.Ordinal));
        Assert.Single(arguments, a => a.StartsWith("--pidfile=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, a => a.StartsWith("--daemon", StringComparison.Ordinal));
    }

    [Fact]
    public void BudgetCountsPacketsPerProtocol()
    {
        var budget = Nfqws2CommandLine.BudgetFor(
        [
            "--name=a", "--filter-tcp=443", "--out-range=-d10",
            "--new", "--name=b", "--filter-udp=443", "--out-range=-n30",
            "--new", "--name=c", "--filter-tcp=443", "--in-range=-s29500",
            "--new", "--name=d", "--filter-udp=443", "--out-range=a",
            "--new", "--name=e", "--filter-tcp=443", "--out-range=x",
        ], out var beyond);

        Assert.Equal(new CaptureBudget(22, 30, 54, 10), budget);
        Assert.Equal(["d"], beyond);
    }

    [Fact]
    public void PreflightLines()
    {
        var arguments = Nfqws2CommandLine.LinuxGlobals(Host).Append("--lua-desync=pass").ToList();

        var dry = Nfqws2CommandLine.DryRun(arguments);
        Assert.DoesNotContain(dry, a => a.StartsWith("--uid=", StringComparison.Ordinal) || a.StartsWith("--pidfile=", StringComparison.Ordinal));
        Assert.Equal("--dry-run", dry[^1]);

        var lua = Nfqws2CommandLine.LuaCheck(arguments);
        Assert.Contains("--uid=10234:10234", lua);
        Assert.DoesNotContain(lua, a => a.StartsWith("--pidfile=", StringComparison.Ordinal));
        Assert.Equal("--intercept=0", lua[^1]);
    }

    [Fact]
    public void QuoteSingleQuotesEverything()
    {
        Assert.Equal("'--filter-tcp=*'", Nfqws2CommandLine.Quote("--filter-tcp=*"));
        Assert.Equal("'--name=Голосовые звонки/чаты'", Nfqws2CommandLine.Quote("--name=Голосовые звонки/чаты"));
        Assert.Equal("'EpicGames & Fortnite (x)'", Nfqws2CommandLine.Quote("EpicGames & Fortnite (x)"));
        Assert.Equal("'it'\\''s'", Nfqws2CommandLine.Quote("it's"));
        Assert.Equal("''", Nfqws2CommandLine.Quote(""));
    }

    [Fact]
    public void StartScriptTearsDownAppliesAndExecs()
    {
        var script = Nfqws2CommandLine.StartScript(
            Host,
            ["--qnum=1717", "--filter-tcp=*", "--name=it's"],
            4321,
            "org.netzapret",
            "/f/runtime/nfqws2-fw-down.sh",
            "/f/runtime/nfqws2-fw-up.sh");

        Assert.StartsWith("#!/system/bin/sh\n", script);
        Assert.DoesNotContain('\r', script);

        int cd = script.IndexOf("cd '/data/user/0/org.netzapret/files/zapret' || exit 90", StringComparison.Ordinal);
        int down = script.IndexOf("sh '/f/runtime/nfqws2-fw-down.sh'", StringComparison.Ordinal);
        int up = script.IndexOf("sh '/f/runtime/nfqws2-fw-up.sh'", StringComparison.Ordinal);
        int exec = script.IndexOf("exec '/data/app/~~x/org.netzapret-1/lib/arm64/libnfqws2.so'", StringComparison.Ordinal);

        Assert.True(cd >= 0 && cd < down && down < up && up < exec, script);
        Assert.Contains("'--filter-tcp=*'", script);
        Assert.Contains("'--name=it'\\''s'", script);
        Assert.Contains("APP=4321", script);
        Assert.Contains("'org.netzapret'*)", script);
        Assert.Contains("NZ_PID", script);
        Assert.EndsWith(">>'/data/user/0/org.netzapret/files/runtime/nfqws2.log' 2>&1\n", script);
    }

    [Fact]
    public void StopScriptRemovesRulesThenKillsOnlyOurProcess()
    {
        var script = Nfqws2CommandLine.StopScript(Host, "/f/runtime/nfqws2-fw-down.sh");

        int down = script.IndexOf("sh '/f/runtime/nfqws2-fw-down.sh'", StringComparison.Ordinal);
        int term = script.IndexOf("kill -TERM", StringComparison.Ordinal);
        int kill = script.IndexOf("kill -KILL", StringComparison.Ordinal);

        Assert.True(down >= 0 && down < term && term < kill, script);
        Assert.Contains("cat '/data/user/0/org.netzapret/files/runtime/nfqws2.pid'", script);
        Assert.Contains("*libnfqws2.so*", script);
    }

    [Fact]
    public void ScriptsBundleEverythingForSu()
    {
        if (Shipped("Universal V12 Lite") is not { } preset)
            return;

        var launch = Nfqws2CommandLine.Build(preset, Host);
        var kernel = new KernelFeatures
        {
            Mangle4 = true, Nfqueue4 = true, Connbytes4 = true, U32_4 = true, Connmark4 = true,
            Mangle6 = true, Nfqueue6 = true, Connbytes6 = true, U32_6 = true, Connmark6 = true,
            RestoreWait = true,
        };

        var scripts = Nfqws2CommandLine.Scripts(
            Host, launch, new FirewallExclusions(), kernel, "/data/user/0/org.netzapret/files/runtime", 4321, "org.netzapret");

        string[] names =
            [Nfqws2Scripts.Rules4Name, Nfqws2Scripts.Rules6Name, Nfqws2Scripts.DownName, Nfqws2Scripts.UpName, Nfqws2Scripts.StartName, Nfqws2Scripts.StopName];

        Assert.Equal(names.Order(StringComparer.Ordinal), scripts.Files.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(["su", "-c", "sh '/data/user/0/org.netzapret/files/runtime/nfqws2-start.sh'"], scripts.Start);
        Assert.Equal(["su", "-c", "sh '/data/user/0/org.netzapret/files/runtime/nfqws2-stop.sh'"], scripts.Stop);
        Assert.Contains("iptables-restore -w --noflush < '/data/user/0/org.netzapret/files/runtime/nfqws2-fw4.rules'", scripts.Files[Nfqws2Scripts.UpName]);
        Assert.Contains("ip6tables-restore -w --noflush < '/data/user/0/org.netzapret/files/runtime/nfqws2-fw6.rules'", scripts.Files[Nfqws2Scripts.UpName]);

        foreach (var argument in launch.Arguments)
            Assert.Contains(Nfqws2CommandLine.Quote(argument), scripts.Files[Nfqws2Scripts.StartName]);

        // Ядро без IPv6 в mangle: правил IPv6 нет, и об этом сказано.
        var noV6 = Nfqws2CommandLine.Scripts(
            Host, launch, new FirewallExclusions(), kernel with { Mangle6 = false }, "/r", 1, "org.netzapret");

        Assert.False(noV6.Files.ContainsKey(Nfqws2Scripts.Rules6Name));
        Assert.DoesNotContain("ip6tables-restore", noV6.Files[Nfqws2Scripts.UpName]);
        Assert.Contains(noV6.Warnings, w => w.Contains("IPv6", StringComparison.Ordinal));
    }
}
