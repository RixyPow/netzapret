using System.Text.Json;
using NetZapret.Core.Rules;
using NetZapret.Proxy;
using NetZapret.Subscriptions;
using NetZapret.Zapret;
using Xunit;

namespace NetZapret.Core.Tests;

/// <summary>
/// Секция capture: что дополнительно заводить в туннель.
/// </summary>
/// <remarks>
/// Нужна приложениям, которые ходят по голым адресам без DNS — Telegram
/// к своим дата-центрам. Им fakeip не выдаётся, и без явного перехвата
/// правило по процессу не срабатывает: их трафик не доходит до туннеля.
/// </remarks>
public sealed class CaptureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"netzapret-capture-{Guid.NewGuid():N}");

    public CaptureTests() => Directory.CreateDirectory(Path.Combine(_root, "lists"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void WriteList(string name, string content) =>
        File.WriteAllText(Path.Combine(_root, "lists", name), content);

    [Fact]
    public void CaptureSectionIsParsedFromYaml()
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            capture:
              - lists/ipset-telegram.txt
              - 91.108.4.0/22
            rules:
              - match: process
                value: "telegram.exe"
                mode: proxy
            """);

        Assert.Equal(2, engine.RuleSet.CaptureEntries.Count);
        Assert.Contains("91.108.4.0/22", engine.RuleSet.CaptureEntries);
    }

    [Fact]
    public void PlainCidrEntriesPassThrough()
    {
        var result = AddressListReader.Expand(["91.108.4.0/22", "149.154.160.0/20"], _root, out var problems);

        Assert.Equal(2, result.Count);
        Assert.Empty(problems);
    }

    [Fact]
    public void ListFilesAreExpandedRelativeToZapretRoot()
    {
        WriteList("ipset-telegram.txt", "91.108.4.0/22\n149.154.160.0/20\n# комментарий\n\n");

        var result = AddressListReader.Expand(["lists/ipset-telegram.txt"], _root, out var problems);

        Assert.Equal(2, result.Count);
        Assert.Empty(problems);
    }

    [Fact]
    public void BareAddressesInListsGetAFullPrefix()
    {
        // route_address принимает только префиксы.
        WriteList("ipset-telegram.txt", "149.154.167.51\n");

        var result = AddressListReader.Expand(["lists/ipset-telegram.txt"], _root, out _);

        Assert.Equal("149.154.167.51/32", result[0]);
    }

    [Fact]
    public void DuplicatesAcrossEntriesAreCollapsed()
    {
        WriteList("a.txt", "91.108.4.0/22\n");
        WriteList("b.txt", "91.108.4.0/22\n");

        var result = AddressListReader.Expand(["lists/a.txt", "lists/b.txt", "91.108.4.0/22"], _root, out _);

        Assert.Single(result);
    }

    [Fact]
    public void UnreadableEntriesAreReportedNotSilentlyDropped()
    {
        // Молча пропустить значит оставить пользователя с неработающим
        // правилом и без единого намёка почему.
        var result = AddressListReader.Expand(["lists/нет-такого.txt", "не-адрес"], _root, out var problems);

        Assert.Empty(result);
        Assert.Equal(2, problems.Count);
    }

    [Fact]
    public void EmptyListIsReportedAsAProblem()
    {
        WriteList("empty.txt", "# только комментарий\n");

        AddressListReader.Expand(["lists/empty.txt"], _root, out var problems);

        Assert.Single(problems);
    }

    [Fact]
    public void CapturedAddressesReachTheTunnelConfig()
    {
        var engine = RuleSetLoader.Load("""
            mode: selective
            rules:
              - match: process
                value: "telegram.exe"
                mode: proxy
            """);

        var server = new ProxyServer
        {
            Protocol = ProxyProtocol.Hysteria2,
            Tag = "node",
            Host = "example.com",
            Port = 4443,
            Credential = "PLACEHOLDER",
            Security = "tls",
        };

        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, [server], new SingBoxOptions
        {
            Scope = TunnelScope.ProxyOnly,
            CaptureAddresses = ["91.108.4.0/22"],
        }).Json;

        var captured = JsonDocument.Parse(json).RootElement
            .GetProperty("inbounds")[0].GetProperty("route_address")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Contains("91.108.4.0/22", captured);
    }

    [Fact]
    public void CaptureIsIgnoredWithoutSelectiveInterception()
    {
        // При сплошном перехвате в туннель заходит всё, и route_address
        // не выставляется вовсе.
        var engine = RuleSetLoader.Load("mode: selective\nrules: []");

        var json = new SingBoxConfigCompiler().Compile(engine.RuleSet, Array.Empty<ProxyServer>(), new SingBoxOptions
        {
            Scope = TunnelScope.Everything,
            CaptureAddresses = ["91.108.4.0/22"],
        }).Json;

        Assert.False(JsonDocument.Parse(json).RootElement
            .GetProperty("inbounds")[0].TryGetProperty("route_address", out _));
    }
}
