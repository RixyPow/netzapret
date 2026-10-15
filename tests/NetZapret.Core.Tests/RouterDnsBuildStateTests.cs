using NetZapret.Core;
using NetZapret.Proxy;
using Xunit;

namespace NetZapret.Core.Tests;

public sealed class RouterDnsBuildStateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nz-dns-state-" + Guid.NewGuid().ToString("N"));
    private string Config => Path.Combine(_folder, "singbox.json");
    private AppSettings Requested => new() { UseRouterDns = true, RouterDnsAddress = "192.168.1.1", ProxyConfigPath = Config };
    public void Dispose() { if (Directory.Exists(_folder)) Directory.Delete(_folder, true); }

    [Fact]
    public void WarningDoesNotReadOrRequireSingBoxJson()
    {
        RouterDnsBuildState.Save(Config, Requested, "DoH fallback; PBR unavailable");
        Assert.False(File.Exists(Config));
        Assert.Equal("DoH fallback; PBR unavailable", RouterDnsConnection.ConfigWarning(Requested));
        // Произвольный большой и даже невалидный конфиг никак не влияет на статус.
        File.WriteAllText(Config, new string('x', 1_000_000));
        for (int i = 0; i < 100; i++)
            Assert.Equal("DoH fallback; PBR unavailable", RouterDnsConnection.ConfigWarning(Requested));
    }

    [Fact]
    public void SuccessfulRouterBuildClearsOldWarning()
    {
        RouterDnsBuildState.Save(Config, Requested, "old fallback");
        RouterDnsBuildState.Save(Config, Requested, null);
        Assert.Null(RouterDnsConnection.ConfigWarning(Requested));
        Assert.Null(RouterDnsBuildState.Load(Config)!.Warning);
    }

    [Fact]
    public void DisabledOrDifferentRouterDoesNotShowOldWarning()
    {
        RouterDnsBuildState.Save(Config, Requested, "old fallback");
        Assert.Null(RouterDnsConnection.ConfigWarning(Requested with { UseRouterDns = false }));
        Assert.Null(RouterDnsConnection.ConfigWarning(Requested with { RouterDnsAddress = "192.168.2.1" }));
        RouterDnsBuildState.Save(Config, Requested with { UseRouterDns = false }, null);
        Assert.Null(RouterDnsConnection.ConfigWarning(Requested));
    }

    [Fact]
    public void MissingOrCorruptStateDoesNotPreventShowingStatus()
    {
        Assert.Null(RouterDnsConnection.ConfigWarning(Requested));
        Directory.CreateDirectory(_folder);
        File.WriteAllText(RouterDnsBuildState.PathFor(Config), "invalid");
        Assert.Null(RouterDnsConnection.ConfigWarning(Requested));
    }

    [Fact]
    public void ExternalBuildStateChangeInvalidatesCache()
    {
        RouterDnsBuildState.Save(Config, Requested, "old fallback");
        string path = RouterDnsBuildState.PathFor(Config);
        File.WriteAllText(path, """{"RequestedRouterAddress":"192.168.1.1","Warning":"external build; fallback"}""");
        Assert.Equal("external build; fallback", RouterDnsConnection.ConfigWarning(Requested));
    }

    [Fact]
    public void OversizedStateIsRejectedWithoutParsing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(RouterDnsBuildState.PathFor(Config), new string('x', 1_000_000));
        Assert.Null(RouterDnsConnection.ConfigWarning(Requested));
    }
}
